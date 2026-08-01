using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace StorageGroupQuotas
{
    [StaticConstructorOnStartup]
    internal static class ModBootstrap
    {
        static ModBootstrap()
        {
            Harmony harmony = new Harmony("steelshadow.storagegroupquotas");
            harmony.PatchAll();
            PickUpAndHaulCompatibility.Apply(harmony);

            if (ModsConfig.IsActive("Andromeda.StackGap"))
            {
                Log.Error("[Storage Group Quotas] Stack Gap is active. Disable it: both mods patch storage capacity and cannot produce reliable results together.");
            }
        }
    }

    [HarmonyPatch(typeof(StorageSettings), nameof(StorageSettings.ExposeData))]
    internal static class Patch_StorageSettings_ExposeData
    {
        private static void Postfix(StorageSettings __instance)
        {
            StorageQuotaData data = null;
            QuotaDataStore.TryGet(__instance, out data);
            if (Scribe.mode == LoadSaveMode.Saving && data != null && !data.HasPersistentSettings)
            {
                data = null;
            }

            Scribe_Deep.Look(ref data, "storageGroupQuotas");

            if (data != null)
            {
                QuotaDataStore.Set(__instance, data);
            }
        }
    }

    [HarmonyPatch(typeof(StorageSettings), nameof(StorageSettings.CopyFrom))]
    internal static class Patch_StorageSettings_CopyFrom
    {
        private static void Postfix(StorageSettings __instance, StorageSettings other)
        {
            if (QuotaDataStore.TryGet(other, out StorageQuotaData data))
            {
                QuotaDataStore.Set(__instance, data.Clone());
            }
            else
            {
                QuotaDataStore.Set(__instance, null);
            }

            QuotaUtility.NotifySettingsChanged(__instance);
        }
    }

    [HarmonyPatch(typeof(StoreUtility), "NoStorageBlockersIn", new[] { typeof(IntVec3), typeof(Map), typeof(Thing) })]
    internal static class Patch_StoreUtility_NoStorageBlockersIn
    {
        private static void Postfix(IntVec3 c, Map map, Thing thing, ref bool __result)
        {
            if (__result && QuotaUtility.RemainingForDestination(thing, c, map) <= 0)
            {
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(HaulAIUtility), nameof(HaulAIUtility.HaulToCellStorageJob))]
    internal static class Patch_HaulAIUtility_HaulToCellStorageJob
    {
        private static void Postfix(Pawn p, Thing t, IntVec3 storeCell, ref Job __result)
        {
            if (__result == null)
            {
                return;
            }

            int remaining = QuotaUtility.RemainingForDestination(t, storeCell, p.Map);
            if (remaining == int.MaxValue)
            {
                return;
            }

            if (remaining <= 0)
            {
                __result = null;
                return;
            }

            __result.count = Math.Min(__result.count, remaining);
            __result.haulOpportunisticDuplicates = false;
            if (__result.count <= 0)
            {
                __result = null;
            }
        }
    }

    [HarmonyPatch(typeof(ITab_Storage), "FillTab")]
    internal static class Patch_ITab_Storage_FillTab
    {
        private const float QuotaButtonX = 180f;
        private const float QuotaButtonMaxWidth = 90f;
        private const float CloseButtonGap = 8f;
        private const float MinQuotaButtonWidth = 64f;

        private static readonly PropertyInfo SelectedParentProperty =
            AccessTools.Property(typeof(ITab_Storage), "SelStoreSettingsParent");
        private static readonly FieldInfo TabSizeField =
            AccessTools.Field(typeof(InspectTabBase), "size");

        private static void Postfix(ITab_Storage __instance)
        {
            IStoreSettingsParent parent = SelectedParentProperty?.GetValue(__instance, null) as IStoreSettingsParent;
            if (parent == null)
            {
                return;
            }

            Vector2 tabSize = TabSizeField?.GetValue(__instance) is Vector2 currentSize
                ? currentSize
                : new Vector2(300f, 480f);
            float buttonWidth = Mathf.Min(
                QuotaButtonMaxWidth,
                tabSize.x
                    - QuotaButtonX
                    - Widgets.CloseButtonSize
                    - Widgets.CloseButtonMargin
                    - CloseButtonGap);
            if (buttonWidth < MinQuotaButtonWidth)
            {
                return;
            }

            Rect buttonRect = new Rect(QuotaButtonX, 10f, buttonWidth, 24f);
            if (Widgets.ButtonText(buttonRect, "SGQ_QuotaButton".Translate()))
            {
                Find.WindowStack.Add(new Window_StorageQuotas(parent.GetStoreSettings()));
            }
        }
    }

    internal static class PickUpAndHaulCompatibility
    {
        private const string WorkGiverTypeName = "PickUpAndHaul.WorkGiver_HaulToInventory";
        private const string CompTypeName = "PickUpAndHaul.CompHauledToInventory";

        private static Type workGiverType;
        private static Type compType;
        private static MethodInfo capacityAt;
        private static MethodInfo isNotCorpseOrAllowed;
        private static MethodInfo overAllowedGearCapacity;
        private static MethodInfo isAllowedRace;
        private static MethodInfo getHauledThings;
        private static MethodInfo registerHauledItem;
        private static bool batchRuntimeDisabled;
        private static bool invocationWarningLogged;

        internal static void Apply(Harmony harmony)
        {
            workGiverType = AccessTools.TypeByName(WorkGiverTypeName);
            if (workGiverType == null)
            {
                return;
            }

            compType = AccessTools.TypeByName(CompTypeName);
            Type settingsType = AccessTools.TypeByName("PickUpAndHaul.Settings");
            capacityAt = AccessTools.Method(workGiverType, "CapacityAt", new[]
            {
                typeof(Thing), typeof(IntVec3), typeof(Map)
            });
            isNotCorpseOrAllowed = AccessTools.Method(workGiverType, "IsNotCorpseOrAllowed", new[]
            {
                typeof(Thing)
            });
            overAllowedGearCapacity = AccessTools.Method(workGiverType, "OverAllowedGearCapacity", new[]
            {
                typeof(Pawn)
            });
            isAllowedRace = settingsType == null
                ? null
                : AccessTools.Method(settingsType, "IsAllowedRace", new[]
                {
                    typeof(RaceProperties)
                });
            getHauledThings = compType == null
                ? null
                : AccessTools.Method(compType, "GetHashSet", Type.EmptyTypes);
            registerHauledItem = compType == null
                ? null
                : AccessTools.Method(compType, "RegisterHauledItem", new[]
                {
                    typeof(Thing)
                });

            if (capacityAt == null)
            {
                return;
            }

            harmony.Patch(capacityAt, postfix: new HarmonyMethod(
                typeof(PickUpAndHaulCompatibility), nameof(CapacityAtPostfix)));
            Log.Message("[Storage Group Quotas] Pick Up And Haul capacity compatibility enabled.");

            if (BatchApiAvailable)
            {
                Log.Message("[Storage Group Quotas] Pick Up And Haul quota-overflow batch hauling enabled.");
            }
        }

        private static bool BatchApiAvailable => !batchRuntimeDisabled
            && workGiverType != null
            && compType != null
            && capacityAt != null
            && isNotCorpseOrAllowed != null
            && overAllowedGearCapacity != null
            && isAllowedRace != null
            && getHauledThings != null
            && registerHauledItem != null;

        internal static bool CanUseBatchHauling(Pawn pawn, Thing thing)
        {
            if (!BatchApiAvailable
                || pawn?.inventory?.innerContainer == null
                || pawn.Faction != Faction.OfPlayerSilentFail
                || pawn.IsQuestLodger()
                || thing == null)
            {
                return false;
            }

            try
            {
                if (!(bool)isAllowedRace.Invoke(null, new object[] { pawn.RaceProps })
                    || (bool)overAllowedGearCapacity.Invoke(null, new object[] { pawn })
                    || !(bool)isNotCorpseOrAllowed.Invoke(null, new object[] { thing }))
                {
                    return false;
                }

                ThingComp comp = FindComp(pawn);
                return comp != null
                    && getHauledThings.Invoke(comp, null) is ICollection<Thing> hauledThings
                    && hauledThings.Count == 0
                    && CombatExtendedInventoryCompatibility.CanUseBatchHauling(pawn);
            }
            catch (Exception exception)
            {
                LogInvocationWarning(exception);
                return false;
            }
        }

        internal static bool AllowsThing(Thing thing)
        {
            if (!BatchApiAvailable || thing == null)
            {
                return false;
            }

            try
            {
                return (bool)isNotCorpseOrAllowed.Invoke(null, new object[] { thing });
            }
            catch (Exception exception)
            {
                LogInvocationWarning(exception);
                return false;
            }
        }

        internal static bool RegisterHauledItem(Pawn pawn, Thing thing)
        {
            try
            {
                ThingComp comp = FindComp(pawn);
                if (comp == null)
                {
                    DisableBatch("the pawn's hauled-inventory component disappeared before pickup");
                    return false;
                }

                registerHauledItem.Invoke(comp, new object[] { thing });
                bool registered = getHauledThings.Invoke(comp, null) is ICollection<Thing> hauledThings
                    && hauledThings.Contains(thing);
                if (!registered)
                {
                    DisableBatch("Pick Up And Haul did not retain the registered item");
                }

                return registered;
            }
            catch (Exception exception)
            {
                LogInvocationWarning(exception);
                return false;
            }
        }

        internal static void UnregisterHauledItem(Pawn pawn, Thing thing)
        {
            if (thing == null)
            {
                return;
            }

            try
            {
                ThingComp comp = FindComp(pawn);
                if (comp != null
                    && getHauledThings?.Invoke(comp, null) is ICollection<Thing> hauledThings)
                {
                    hauledThings.Remove(thing);
                }
            }
            catch (Exception exception)
            {
                LogInvocationWarning(exception);
            }
        }

        internal static int CapacityAt(Thing thing, IntVec3 storeCell, Map map)
        {
            if (capacityAt != null)
            {
                try
                {
                    return Math.Max(0, (int)capacityAt.Invoke(null, new object[] { thing, storeCell, map }));
                }
                catch (Exception exception)
                {
                    LogInvocationWarning(exception);
                }
            }

            return Math.Max(0, storeCell.GetItemStackSpaceLeftFor(map, thing.def));
        }

        private static ThingComp FindComp(Pawn pawn)
        {
            if (compType == null || pawn?.AllComps == null)
            {
                return null;
            }

            foreach (ThingComp comp in pawn.AllComps)
            {
                if (compType.IsInstanceOfType(comp))
                {
                    return comp;
                }
            }

            return null;
        }

        private static void LogInvocationWarning(Exception exception)
        {
            batchRuntimeDisabled = true;
            if (invocationWarningLogged)
            {
                return;
            }

            invocationWarningLogged = true;
            Log.Warning("[Storage Group Quotas] Pick Up And Haul batch compatibility failed at runtime; falling back to vanilla hauling. "
                + exception.GetBaseException().Message);
        }

        private static void DisableBatch(string reason)
        {
            batchRuntimeDisabled = true;
            if (invocationWarningLogged)
            {
                return;
            }

            invocationWarningLogged = true;
            Log.Warning("[Storage Group Quotas] Pick Up And Haul batch compatibility was disabled for this session; falling back to vanilla hauling because "
                + reason + ".");
        }

        private static void CapacityAtPostfix(Thing thing, IntVec3 storeCell, Map map, ref int __result)
        {
            int remaining = QuotaUtility.RemainingForDestination(thing, storeCell, map);
            if (remaining != int.MaxValue)
            {
                __result = Math.Min(__result, remaining);
            }
        }
    }

    internal static class CombatExtendedInventoryCompatibility
    {
        private static bool initialized;
        private static bool active;
        private static bool warningLogged;
        private static Type compInventoryType;
        private static MethodInfo canFitInInventory;
        private static MethodInfo updateInventory;

        internal static bool CanUseBatchHauling(Pawn pawn)
        {
            EnsureInitialized();
            return !active || (ApiAvailable && FindComp(pawn) != null);
        }

        internal static int LimitCount(Pawn pawn, Thing thing, int requested)
        {
            if (requested <= 0)
            {
                return 0;
            }

            EnsureInitialized();
            if (!active)
            {
                return Math.Min(requested, MassUtility.CountToPickUpUntilOverEncumbered(pawn, thing));
            }

            ThingComp comp = FindComp(pawn);
            if (!ApiAvailable || comp == null)
            {
                return 0;
            }

            try
            {
                updateInventory.Invoke(comp, null);
                object[] arguments = { thing, 0, false, false };
                bool anyFits = (bool)canFitInInventory.Invoke(comp, arguments);
                int fitCount = Math.Max(0, (int)arguments[1]);
                return anyFits ? Math.Min(requested, fitCount) : 0;
            }
            catch (Exception exception)
            {
                LogWarning(exception);
                return 0;
            }
        }

        internal static void NotifyInventoryChanged(Pawn pawn)
        {
            EnsureInitialized();
            if (!active)
            {
                return;
            }

            ThingComp comp = FindComp(pawn);
            if (!ApiAvailable || comp == null)
            {
                return;
            }

            try
            {
                updateInventory.Invoke(comp, null);
            }
            catch (Exception exception)
            {
                LogWarning(exception);
            }
        }

        private static bool ApiAvailable => compInventoryType != null
            && canFitInInventory != null
            && updateInventory != null;

        private static void EnsureInitialized()
        {
            if (initialized)
            {
                return;
            }

            initialized = true;
            active = ModsConfig.IsActive("CETeam.CombatExtended");
            compInventoryType = AccessTools.TypeByName("CombatExtended.CompInventory");
            active = active || compInventoryType != null;
            if (!active)
            {
                return;
            }

            if (compInventoryType != null)
            {
                canFitInInventory = AccessTools.Method(compInventoryType, "CanFitInInventory", new[]
                {
                    typeof(Thing), typeof(int).MakeByRefType(), typeof(bool), typeof(bool)
                });
                updateInventory = AccessTools.Method(compInventoryType, "UpdateInventory", Type.EmptyTypes);
            }

            if (ApiAvailable)
            {
                Log.Message("[Storage Group Quotas] Combat Extended weight/bulk limits enabled for quota-overflow batch hauling.");
            }
        }

        private static ThingComp FindComp(Pawn pawn)
        {
            if (compInventoryType == null || pawn?.AllComps == null)
            {
                return null;
            }

            foreach (ThingComp comp in pawn.AllComps)
            {
                if (compInventoryType.IsInstanceOfType(comp))
                {
                    return comp;
                }
            }

            return null;
        }

        private static void LogWarning(Exception exception)
        {
            if (warningLogged)
            {
                return;
            }

            warningLogged = true;
            Log.Warning("[Storage Group Quotas] Combat Extended inventory compatibility failed at runtime; batch hauling is disabled. "
                + exception.GetBaseException().Message);
        }
    }
}
