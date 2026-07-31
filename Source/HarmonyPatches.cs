using System;
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
            if (Scribe.mode == LoadSaveMode.Saving && data != null && !data.Active)
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
        internal static void Apply(Harmony harmony)
        {
            Type type = AccessTools.TypeByName("PickUpAndHaul.WorkGiver_HaulToInventory");
            MethodInfo capacityAt = AccessTools.Method(type, "CapacityAt", new[]
            {
                typeof(Thing), typeof(IntVec3), typeof(Map)
            });
            if (capacityAt == null)
            {
                return;
            }

            harmony.Patch(capacityAt, postfix: new HarmonyMethod(
                typeof(PickUpAndHaulCompatibility), nameof(CapacityAtPostfix)));
            Log.Message("[Storage Group Quotas] Pick Up And Haul capacity compatibility enabled.");
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
}
