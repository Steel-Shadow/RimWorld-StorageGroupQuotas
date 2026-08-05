using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace StorageGroupQuotas
{
    internal enum InventoryHaulingBackend
    {
        None,
        HaulersDream,
        PickUpAndHaul
    }

    /// <summary>
    /// Selects exactly one optional inventory-hauling owner for an SGQ batch job. Hauler's Dream
    /// deliberately replaces Pick Up And Haul, so registering the same cargo with both mods would
    /// leave two unload systems competing over one pawn inventory.
    /// </summary>
    internal static class InventoryHaulingCompatibility
    {
        private static InventoryHaulingBackend backend;

        internal static void Apply(Harmony harmony)
        {
            PickUpAndHaulCompatibility.Apply(harmony);
            HaulersDreamCompatibility.Apply(harmony);

            if (HaulersDreamCompatibility.IsPresent)
            {
                backend = InventoryHaulingBackend.HaulersDream;
                Log.Message("[Storage Group Quotas] Hauler's Dream quota-aware inventory hauling enabled.");

                if (PickUpAndHaulCompatibility.IsPresent)
                {
                    Log.Warning("[Storage Group Quotas] Hauler's Dream and Pick Up And Haul are both active. "
                        + "Hauler's Dream officially replaces Pick Up And Haul, so SGQ will use only Hauler's "
                        + "Dream for inventory tracking. Disable Pick Up And Haul to avoid conflicts between "
                        + "those two hauling mods.");
                }
            }
            else if (PickUpAndHaulCompatibility.IsPresent)
            {
                backend = InventoryHaulingBackend.PickUpAndHaul;
                Log.Message("[Storage Group Quotas] Pick Up And Haul quota-aware inventory hauling enabled.");
            }
            else
            {
                backend = InventoryHaulingBackend.None;
            }
        }

        internal static bool CanUseBatchHauling(Pawn pawn, Thing thing)
        {
            switch (backend)
            {
                case InventoryHaulingBackend.HaulersDream:
                    return HaulersDreamCompatibility.CanUseBatchHauling(pawn, thing);
                case InventoryHaulingBackend.PickUpAndHaul:
                    return PickUpAndHaulCompatibility.CanUseBatchHauling(pawn, thing);
                default:
                    return false;
            }
        }

        internal static bool AllowsThing(Pawn pawn, Thing thing)
        {
            switch (backend)
            {
                case InventoryHaulingBackend.HaulersDream:
                    return HaulersDreamCompatibility.AllowsThing(pawn, thing);
                case InventoryHaulingBackend.PickUpAndHaul:
                    return PickUpAndHaulCompatibility.AllowsThing(thing);
                default:
                    return false;
            }
        }

        internal static int LimitCount(Pawn pawn, Thing thing, int requested)
        {
            switch (backend)
            {
                case InventoryHaulingBackend.HaulersDream:
                    // HD owns both smart overload and its Combat Extended weight/bulk calculation.
                    // Applying SGQ's CE clamp again would incorrectly force HD back to 100% capacity.
                    return HaulersDreamCompatibility.LimitCount(pawn, thing, requested);
                case InventoryHaulingBackend.PickUpAndHaul:
                    return CombatExtendedInventoryCompatibility.LimitCount(pawn, thing, requested);
                default:
                    return 0;
            }
        }

        internal static bool RegisterHauledItem(Pawn pawn, Thing thing)
        {
            switch (backend)
            {
                case InventoryHaulingBackend.HaulersDream:
                    return HaulersDreamCompatibility.RegisterHauledItem(pawn, thing);
                case InventoryHaulingBackend.PickUpAndHaul:
                    return PickUpAndHaulCompatibility.RegisterHauledItem(pawn, thing);
                default:
                    return false;
            }
        }

        internal static void UnregisterHauledItem(Pawn pawn, Thing thing)
        {
            switch (backend)
            {
                case InventoryHaulingBackend.HaulersDream:
                    HaulersDreamCompatibility.UnregisterHauledItem(pawn, thing);
                    break;
                case InventoryHaulingBackend.PickUpAndHaul:
                    PickUpAndHaulCompatibility.UnregisterHauledItem(pawn, thing);
                    break;
            }
        }

        internal static void NotifyJobFinished(Pawn pawn, IList<Thing> hauledThings)
        {
            if (pawn == null || hauledThings == null || hauledThings.Count == 0)
            {
                return;
            }

            bool cargoRemains = false;
            for (int i = 0; i < hauledThings.Count; i++)
            {
                Thing thing = hauledThings[i];
                if (thing != null
                    && !thing.Destroyed
                    && (pawn.inventory?.innerContainer?.Contains(thing) == true
                        || pawn.carryTracker?.innerContainer?.Contains(thing) == true))
                {
                    cargoRemains = true;
                    break;
                }
            }

            if (!cargoRemains)
            {
                return;
            }

            switch (backend)
            {
                case InventoryHaulingBackend.HaulersDream:
                    HaulersDreamCompatibility.RequestUnload(pawn);
                    break;
                case InventoryHaulingBackend.PickUpAndHaul:
                    PickUpAndHaulCompatibility.RequestUnload(pawn);
                    break;
            }
        }

        internal static int CapacityAt(Thing thing, IntVec3 storeCell, Map map)
        {
            // PUAH exposes a storage-mod-aware physical-capacity helper. It remains useful even when
            // HD owns inventory tracking in an unsupported both-mods-enabled setup.
            return PickUpAndHaulCompatibility.CapacityAt(thing, storeCell, map);
        }
    }

    /// <summary>
    /// Reflection-only integration with Hauler's Dream 1.23.x. No hard assembly dependency is added:
    /// renamed or removed APIs disable SGQ batching for the session and leave vanilla hauling available.
    /// </summary>
    internal static class HaulersDreamCompatibility
    {
        internal const string HarmonyId = "giwaffed.HaulersDream";

        private const string ModTypeName = "HaulersDream.HaulersDreamMod";
        private const string SettingsTypeName = "HaulersDream.HaulersDreamSettings";
        private const string YieldRouterTypeName = "HaulersDream.YieldRouter";
        private const string BulkHaulTypeName = "HaulersDream.BulkHaul";
        private const string CompTypeName = "HaulersDream.CompHauledToInventory";
        private const string UnloadCheckerTypeName = "HaulersDream.PawnUnloadChecker";
        private const string UnloadDriverTypeName = "HaulersDream.JobDriver_UnloadHauledInventory";

        private static Type compType;
        private static Type unloadDriverType;
        private static PropertyInfo settingsProperty;
        private static FieldInfo bulkHaulField;
        private static FieldInfo bulkHaulCorpsesField;
        private static FieldInfo countToDropField;
        private static MethodInfo isCandidate;
        private static MethodInfo massClampedTake;
        private static MethodInfo maySweepCorpse;
        private static MethodInfo peekHashSet;
        private static MethodInfo registerHauledItem;
        private static MethodInfo deregisterHauledItem;
        private static MethodInfo notifyYieldPicked;
        private static MethodInfo requestUnload;
        private static bool batchRuntimeDisabled;
        private static bool batchWarningLogged;
        private static bool unloadWarningLogged;

        internal static bool IsPresent { get; private set; }

        private static bool BatchApiAvailable => !batchRuntimeDisabled
            && IsPresent
            && compType != null
            && settingsProperty != null
            && bulkHaulField != null
            && isCandidate != null
            && massClampedTake != null
            && peekHashSet != null
            && registerHauledItem != null
            && deregisterHauledItem != null
            && notifyYieldPicked != null
            && requestUnload != null;

        internal static void Apply(Harmony harmony)
        {
            Type modType = AccessTools.TypeByName(ModTypeName);
            IsPresent = modType != null;
            if (!IsPresent)
            {
                return;
            }

            Type settingsType = AccessTools.TypeByName(SettingsTypeName);
            Type yieldRouterType = AccessTools.TypeByName(YieldRouterTypeName);
            Type bulkHaulType = AccessTools.TypeByName(BulkHaulTypeName);
            Type unloadCheckerType = AccessTools.TypeByName(UnloadCheckerTypeName);
            compType = AccessTools.TypeByName(CompTypeName);
            unloadDriverType = AccessTools.TypeByName(UnloadDriverTypeName);

            settingsProperty = AccessTools.Property(modType, "Settings");
            bulkHaulField = settingsType == null ? null : AccessTools.Field(settingsType, "bulkHaul");
            bulkHaulCorpsesField = settingsType == null
                ? null
                : AccessTools.Field(settingsType, "bulkHaulCorpses");
            isCandidate = yieldRouterType == null
                ? null
                : AccessTools.Method(yieldRouterType, "IsCandidate", new[]
                {
                    typeof(Pawn), typeof(bool)
                });
            massClampedTake = bulkHaulType == null || settingsType == null
                ? null
                : AccessTools.Method(bulkHaulType, "MassClampedTake", new[]
                {
                    typeof(Pawn), typeof(Thing), typeof(int), settingsType
                });
            maySweepCorpse = bulkHaulType == null || settingsType == null
                ? null
                : AccessTools.Method(bulkHaulType, "MaySweepCorpse", new[]
                {
                    typeof(Pawn), typeof(Corpse), settingsType
                });
            peekHashSet = compType == null
                ? null
                : AccessTools.Method(compType, "PeekHashSet", Type.EmptyTypes);
            registerHauledItem = compType == null
                ? null
                : AccessTools.Method(compType, "RegisterHauledItem", new[]
                {
                    typeof(Thing), typeof(int)
                });
            deregisterHauledItem = compType == null
                ? null
                : AccessTools.Method(compType, "Deregister", new[]
                {
                    typeof(Thing)
                });
            notifyYieldPicked = compType == null
                ? null
                : AccessTools.Method(compType, "NotifyYieldPicked", Type.EmptyTypes);
            requestUnload = unloadCheckerType == null
                ? null
                : AccessTools.Method(unloadCheckerType, "CheckIfShouldUnload", new[]
                {
                    typeof(Pawn), typeof(bool), typeof(bool), typeof(bool)
                });

            countToDropField = unloadDriverType == null
                ? null
                : AccessTools.Field(unloadDriverType, "countToDrop");
            MethodInfo findTargetOrDrop = unloadDriverType == null
                ? null
                : AccessTools.Method(unloadDriverType, "FindTargetOrDrop");
            if (findTargetOrDrop != null && countToDropField != null)
            {
                HarmonyMethod postfix = new HarmonyMethod(
                    typeof(HaulersDreamCompatibility),
                    nameof(FindTargetOrDropPostfix))
                {
                    priority = Priority.Last,
                    after = new[] { HarmonyId }
                };
                harmony.Patch(findTargetOrDrop, postfix: postfix);
            }
            else
            {
                Log.Warning("[Storage Group Quotas] Hauler's Dream unload planning API was not found. "
                    + "The arrival-time quota guard remains active, but SGQ could not reduce inventory transfer "
                    + "before the pawn reaches storage.");
            }

            if (!BatchApiAvailable)
            {
                DisableBatch("one or more Hauler's Dream 1.23 inventory APIs were not found");
            }
        }

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
                object settings = settingsProperty.GetValue(null, null);
                if (settings == null
                    || !(bool)bulkHaulField.GetValue(settings)
                    || !(bool)isCandidate.Invoke(null, new object[] { pawn, false }))
                {
                    return false;
                }

                ThingComp comp = FindComp(pawn);
                if (comp == null
                    || !(peekHashSet.Invoke(comp, null) is ICollection<Thing> hauledThings)
                    || hauledThings.Count != 0
                    || !AllowsThing(pawn, thing))
                {
                    return false;
                }

                // Preserve HD's CE issue-#115 behavior. CE can allow fewer units in inventory
                // than the pawn can carry in its hands (notably very bulky shells); converting
                // that job into a backpack batch would make hauling slower, sometimes one round
                // per trip. HD deliberately leaves that case to vanilla hand hauling.
                int requested = Math.Min(QuotaUtility.OverflowCount(thing), thing.stackCount);
                int inventoryTake = LimitCount(pawn, thing, requested);
                int handTake = Math.Min(
                    requested,
                    pawn.carryTracker?.MaxStackSpaceEver(thing.def) ?? thing.def.stackLimit);
                return inventoryTake > 0
                    && (!CombatExtendedInventoryCompatibility.IsActive
                        || inventoryTake >= handTake);
            }
            catch (Exception exception)
            {
                LogBatchWarning(exception);
                return false;
            }
        }

        internal static bool AllowsThing(Pawn pawn, Thing thing)
        {
            if (!BatchApiAvailable || thing == null)
            {
                return false;
            }

            if (!(thing is Corpse corpse))
            {
                return true;
            }

            try
            {
                object settings = settingsProperty.GetValue(null, null);
                return settings != null
                    && bulkHaulCorpsesField != null
                    && (bool)bulkHaulCorpsesField.GetValue(settings)
                    && maySweepCorpse != null
                    && (bool)maySweepCorpse.Invoke(null, new object[] { pawn, corpse, settings });
            }
            catch (Exception exception)
            {
                LogBatchWarning(exception);
                return false;
            }
        }

        internal static int LimitCount(Pawn pawn, Thing thing, int requested)
        {
            if (!BatchApiAvailable || pawn == null || thing == null || requested <= 0)
            {
                return 0;
            }

            try
            {
                object settings = settingsProperty.GetValue(null, null);
                if (settings == null || !(bool)bulkHaulField.GetValue(settings))
                {
                    return 0;
                }

                int allowed = (int)massClampedTake.Invoke(
                    null,
                    new[] { (object)pawn, thing, requested, settings });
                return Math.Max(0, Math.Min(requested, allowed));
            }
            catch (Exception exception)
            {
                LogBatchWarning(exception);
                return 0;
            }
        }

        internal static bool RegisterHauledItem(Pawn pawn, Thing thing)
        {
            if (thing == null)
            {
                return false;
            }

            try
            {
                ThingComp comp = FindComp(pawn);
                if (comp == null || registerHauledItem == null || peekHashSet == null)
                {
                    DisableBatch("the pawn's Hauler's Dream inventory component disappeared before pickup");
                    return false;
                }

                registerHauledItem.Invoke(comp, new object[] { thing, 0 });
                bool registered = peekHashSet.Invoke(comp, null) is ICollection<Thing> hauledThings
                    && hauledThings.Contains(thing);
                if (!registered)
                {
                    DisableBatch("Hauler's Dream did not retain the registered item");
                    return false;
                }

                notifyYieldPicked?.Invoke(comp, null);
                return true;
            }
            catch (Exception exception)
            {
                LogBatchWarning(exception);
                return false;
            }
        }

        internal static void UnregisterHauledItem(Pawn pawn, Thing thing)
        {
            if (thing == null || deregisterHauledItem == null)
            {
                return;
            }

            try
            {
                ThingComp comp = FindComp(pawn);
                if (comp != null)
                {
                    deregisterHauledItem.Invoke(comp, new object[] { thing });
                }
            }
            catch (Exception exception)
            {
                LogBatchWarning(exception);
            }
        }

        internal static void RequestUnload(Pawn pawn)
        {
            if (pawn == null || requestUnload == null)
            {
                return;
            }

            try
            {
                requestUnload.Invoke(null, new object[] { pawn, true, true, false });
            }
            catch (Exception exception)
            {
                LogBatchWarning(exception);
                if (pawn.inventory != null)
                {
                    pawn.inventory.UnloadEverything = true;
                }
            }
        }

        internal static bool IsUnloadDriver(object driver)
        {
            return driver != null
                && unloadDriverType != null
                && unloadDriverType.IsInstanceOfType(driver);
        }

        /// <summary>
        /// Last-moment guard around HD's PlaceHauledThingInCell toil. HD removes a tracked item from
        /// its set when it moves the item into the pawn's hands, so an excess split is returned to the
        /// inventory without merging and formally re-registered before vanilla places the allowed part.
        /// </summary>
        internal static bool PrepareQuotaPlacement(Pawn pawn)
        {
            if (pawn == null || !IsUnloadDriver(pawn.jobs?.curDriver))
            {
                return true;
            }

            try
            {
                Job job = pawn.CurJob;
                Thing carried = pawn.carryTracker?.CarriedThing;
                if (job == null
                    || carried == null
                    || !job.targetB.IsValid
                    || job.targetB.HasThing
                    || !job.targetB.Cell.InBounds(pawn.Map))
                {
                    return true;
                }

                int remaining = QuotaUtility.RemainingForDestination(
                    carried,
                    job.targetB.Cell,
                    pawn.Map);
                if (remaining == int.MaxValue || remaining >= carried.stackCount)
                {
                    return true;
                }

                int allowed = Math.Max(0, remaining);
                int excess = carried.stackCount - allowed;
                Thing returned = null;
                pawn.carryTracker.innerContainer.TryTransferToContainer(
                    carried,
                    pawn.inventory.innerContainer,
                    excess,
                    out returned,
                    canMergeWithExistingStacks: false);

                if (returned == null || !pawn.inventory.innerContainer.Contains(returned))
                {
                    LogUnloadWarning("could not return quota excess from the pawn's hands to inventory");
                    pawn.inventory.UnloadEverything = true;
                    EndUnloadJob(pawn);
                    return false;
                }

                if (!RegisterHauledItem(pawn, returned))
                {
                    // Vanilla's unload flag is the no-loss fallback if an HD update breaks registration.
                    pawn.inventory.UnloadEverything = true;
                }

                if (allowed <= 0)
                {
                    EndUnloadJob(pawn);
                    return false;
                }

                job.count = allowed;
                return true;
            }
            catch (Exception exception)
            {
                LogUnloadWarning(exception.GetBaseException().Message);
                if (pawn.inventory != null)
                {
                    pawn.inventory.UnloadEverything = true;
                }
                EndUnloadJob(pawn);
                return false;
            }
        }

        private static void FindTargetOrDropPostfix(object __instance, ref Toil __result)
        {
            if (__result?.initAction == null)
            {
                return;
            }

            Toil toil = __result;
            Action original = toil.initAction;
            toil.initAction = delegate
            {
                original();

                Pawn pawn = toil.actor;
                if (pawn == null
                    || !ReferenceEquals(pawn.jobs?.curDriver, __instance)
                    || pawn.CurJob == null)
                {
                    return;
                }

                try
                {
                    Job job = pawn.CurJob;
                    Thing thing = job.GetTarget(TargetIndex.A).Thing;
                    if (thing == null
                        || !job.targetB.IsValid
                        || job.targetB.HasThing
                        || !job.targetB.Cell.InBounds(pawn.Map))
                    {
                        return;
                    }

                    int planned = (int)countToDropField.GetValue(__instance);
                    int remaining = QuotaUtility.RemainingForDestination(
                        thing,
                        job.targetB.Cell,
                        pawn.Map);
                    if (remaining == int.MaxValue || planned <= remaining)
                    {
                        return;
                    }

                    if (remaining <= 0)
                    {
                        EndUnloadJob(pawn);
                        return;
                    }

                    countToDropField.SetValue(__instance, Math.Min(planned, remaining));
                }
                catch (Exception exception)
                {
                    // The arrival-time wrapper is the authoritative safety check, so a planning-only
                    // reflection failure can safely leave HD's original count in place.
                    LogUnloadWarning("could not clamp Hauler's Dream unload planning: "
                        + exception.GetBaseException().Message);
                }
            };
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

        private static void EndUnloadJob(Pawn pawn)
        {
            if (pawn?.Map != null && pawn.CurJob != null)
            {
                ReservationManager reservations = pawn.Map.reservationManager;
                if (pawn.CurJob.targetB.IsValid
                    && reservations.ReservedBy(pawn.CurJob.targetB, pawn, pawn.CurJob))
                {
                    reservations.Release(pawn.CurJob.targetB, pawn, pawn.CurJob);
                }
            }

            if (pawn?.jobs?.curDriver != null)
            {
                pawn.jobs.EndCurrentJob(JobCondition.Incompletable);
            }
        }

        private static void DisableBatch(string reason)
        {
            batchRuntimeDisabled = true;
            if (batchWarningLogged)
            {
                return;
            }

            batchWarningLogged = true;
            Log.Warning("[Storage Group Quotas] Hauler's Dream batch compatibility was disabled for this "
                + "session; SGQ will fall back to vanilla hauling because " + reason + ".");
        }

        private static void LogBatchWarning(Exception exception)
        {
            batchRuntimeDisabled = true;
            if (batchWarningLogged)
            {
                return;
            }

            batchWarningLogged = true;
            Log.Warning("[Storage Group Quotas] Hauler's Dream batch compatibility failed at runtime; "
                + "SGQ will fall back to vanilla hauling. "
                + exception.GetBaseException().Message);
        }

        private static void LogUnloadWarning(string reason)
        {
            if (unloadWarningLogged)
            {
                return;
            }

            unloadWarningLogged = true;
            Log.Warning("[Storage Group Quotas] Hauler's Dream quota-aware unloading encountered an error; "
                + "the current unload was stopped rather than overfilling quota storage. " + reason);
        }
    }

    /// <summary>
    /// HD's optional Haul to Stack feature omits destination-cell reservations for stackable storage
    /// jobs. That is useful for ordinary shelves, but a quota makes the remaining cell/group capacity
    /// a shared scarce resource. Restore the vanilla reservation only for quota-managed destinations.
    /// </summary>
    [HarmonyPatch(typeof(JobDriver_HaulToCell), nameof(JobDriver_HaulToCell.TryMakePreToilReservations))]
    [HarmonyAfter(HaulersDreamCompatibility.HarmonyId)]
    [HarmonyPriority(Priority.Last)]
    internal static class Patch_JobDriver_HaulToCell_RestoreQuotaReservation
    {
        private static void Postfix(
            JobDriver_HaulToCell __instance,
            bool errorOnFailed,
            ref bool __result)
        {
            if (!__result || __instance?.pawn?.Map == null)
            {
                return;
            }

            Job job = __instance.job;
            Pawn pawn = __instance.pawn;
            Thing hauled = job?.GetTarget(TargetIndex.A).Thing;
            if (job == null
                || job.haulMode != HaulMode.ToCellStorage
                || hauled == null
                || !job.targetB.IsValid
                || job.targetB.HasThing
                || !job.targetB.Cell.InBounds(pawn.Map))
            {
                return;
            }

            int remaining = QuotaUtility.RemainingForDestination(
                hauled,
                job.targetB.Cell,
                pawn.Map);
            if (remaining == int.MaxValue)
            {
                return;
            }

            ReservationManager reservations = pawn.Map.reservationManager;
            if (remaining > 0 && reservations.ReservedBy(job.targetB, pawn, job))
            {
                return;
            }

            bool reserved = remaining > 0
                && pawn.Reserve(job.targetB, job, 1, -1, null, errorOnFailed);
            if (reserved)
            {
                return;
            }

            if (reservations.ReservedBy(job.targetB, pawn, job))
            {
                reservations.Release(job.targetB, pawn, job);
            }

            if (job.targetA.IsValid && reservations.ReservedBy(job.targetA, pawn, job))
            {
                reservations.Release(job.targetA, pawn, job);
            }

            __result = false;
        }
    }

    /// <summary>
    /// Recheck the live quota at the final placement seam used by HD's consolidated inventory unload.
    /// This handles another pawn filling the destination while the current pawn is walking there.
    /// </summary>
    [HarmonyPatch(
        typeof(Toils_Haul),
        nameof(Toils_Haul.PlaceHauledThingInCell),
        new[] { typeof(TargetIndex), typeof(Toil), typeof(bool), typeof(bool) })]
    [HarmonyAfter(HaulersDreamCompatibility.HarmonyId)]
    [HarmonyPriority(Priority.Last)]
    internal static class Patch_Toils_Haul_PlaceHauledThingInCell_HaulersDreamQuota
    {
        private static void Postfix(Toil __result)
        {
            if (__result?.initAction == null)
            {
                return;
            }

            Toil toil = __result;
            Action original = toil.initAction;
            toil.initAction = delegate
            {
                Pawn pawn = toil.actor;
                if (!HaulersDreamCompatibility.IsUnloadDriver(pawn?.jobs?.curDriver)
                    || HaulersDreamCompatibility.PrepareQuotaPlacement(pawn))
                {
                    original();
                }
            };
        }
    }
}
