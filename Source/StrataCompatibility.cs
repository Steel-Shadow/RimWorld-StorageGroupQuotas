using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace StorageGroupQuotas
{
    /// <summary>
    /// Optional integration with Strata 3.10+. Strata models every floor as a separate Map and
    /// performs the first leg of a cross-floor haul outside vanilla's HaulToCell job. Keep this
    /// reflection-only so SGQ never acquires a hard dependency on Strata.
    /// </summary>
    internal static class StrataCompatibility
    {
        internal const string PackageId = "AzraelGodKing.Strata";

        private const string WorkGiverTypeName = "Strata.WorkGiver_HaulAcrossLevels";
        private const string HaulTargetsTypeName = "Strata.HaulToLevelTargets";
        private const string WorkSignalsTypeName = "Strata.WorkRelaySignals";
        private const string StorageCompatTypeName = "Strata.StrataStorageSoftCompat";
        private const string InventoryCompatTypeName = "Strata.StrataPuahSoftCompat";
        private const string PortalRelayTypeName = "Strata.PortalRelayChain";
        private const string PortalUtilityTypeName = "Strata.StrataPortalUtility";
        private const string HaulDriverTypeName = "Strata.JobDriver_HaulToLevel";
        private const string RecoveryHaulJobDefName = "SGQ_HaulRejectedCargoToSafeCell";
        private const string RecoveryWaitJobDefName = "SGQ_WaitWithRejectedQuotaCargo";
        private const int ReservationLifetimeTicks = 15000;

        private struct QuotaKey : IEquatable<QuotaKey>
        {
            internal ISlotGroup Scope;
            internal ThingDef Def;

            public bool Equals(QuotaKey other)
            {
                return ReferenceEquals(Scope, other.Scope) && Def == other.Def;
            }

            public override bool Equals(object obj)
            {
                return obj is QuotaKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((Scope == null ? 0 : RuntimeHelpers.GetHashCode(Scope)) * 397)
                        ^ (Def?.shortHash ?? 0);
                }
            }
        }

        private struct CellKey : IEquatable<CellKey>
        {
            internal QuotaKey Quota;
            internal int MapId;
            internal IntVec3 Cell;

            public bool Equals(CellKey other)
            {
                return Quota.Equals(other.Quota) && MapId == other.MapId && Cell == other.Cell;
            }

            public override bool Equals(object obj)
            {
                return obj is CellKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((Quota.GetHashCode() * 397) ^ MapId) * 397 ^ Cell.GetHashCode();
                }
            }
        }

        private sealed class TargetPlan
        {
            internal int ThingId;
            internal Map Map;
            internal IntVec3 Cell;
            internal QuotaKey Quota;
            internal CellKey CellKey;
        }

        private sealed class TransferReservation
        {
            internal int PawnId;
            internal int ThingId;
            internal Thing Prototype;
            internal QuotaKey Quota;
            internal CellKey CellKey;
            internal int Count;
            internal bool ReservesNewStack;
            internal bool QuotaManagedDestination;
            internal int StartedTick;
            internal bool Arrived;
            internal bool QuotaRejectedFallback;
            internal bool FinishActionAttached;
            internal JobDriver FinishDriver;
            internal Job RecoveryJob;
            internal int NextRecoveryTick;
        }

        private struct StorageJobPatchState
        {
            internal int PreviousPawnId;
            internal int PreviousStorageJobPawnId;
            internal bool PreviousQuotaRejected;
        }

        private static readonly Dictionary<int, TargetPlan> PlansByPawn =
            new Dictionary<int, TargetPlan>();
        private static ConditionalWeakTable<Job, TargetPlan> PlansByJob =
            new ConditionalWeakTable<Job, TargetPlan>();
        private static readonly Dictionary<int, TransferReservation> ReservationsByPawn =
            new Dictionary<int, TransferReservation>();
        private static readonly Dictionary<int, int> LastContentSignalTickByMap =
            new Dictionary<int, int>();
        private static readonly HashSet<int> PendingRecoveryCallbacks =
            new HashSet<int>();

        private static Type haulDriverType;
        private static MethodInfo notifyWorkChanged;
        private static MethodInfo notifyHaulArrival;
        private static MethodInfo portalHasIntent;
        private static MethodInfo takeHaulTarget;
        private static Delegate registeredWorkProbe;
        private static bool applied;
        private static bool runtimeWarningLogged;
        [ThreadStatic]
        private static int currentCellCheckPawnId = -1;

        [ThreadStatic]
        private static bool capturingHaulTarget;

        [ThreadStatic]
        private static int capturingPawnId;

        [ThreadStatic]
        private static int currentStorageJobPawnId = -1;

        [ThreadStatic]
        private static bool quotaRejectedDuringStorageJob;

        [ThreadStatic]
        private static int startingRecoveryPawnId = -1;

        internal static void Apply(Harmony harmony)
        {
            if (applied)
            {
                return;
            }

            Type workGiverType = AccessTools.TypeByName(WorkGiverTypeName);
            Type haulTargetsType = AccessTools.TypeByName(HaulTargetsTypeName);
            Type workSignalsType = AccessTools.TypeByName(WorkSignalsTypeName);
            Type storageCompatType = AccessTools.TypeByName(StorageCompatTypeName);
            if (workGiverType == null
                || haulTargetsType == null
                || workSignalsType == null
                || storageCompatType == null)
            {
                return;
            }

            applied = true;
            try
            {
                MethodInfo jobOnThing = AccessTools.Method(
                    workGiverType,
                    "JobOnThing",
                    new[] { typeof(Pawn), typeof(Thing), typeof(bool) });
                MethodInfo rememberTarget = AccessTools.Method(
                    haulTargetsType,
                    "Remember",
                    new[]
                    {
                        typeof(Pawn),
                        typeof(Map),
                        typeof(Map),
                        typeof(Thing),
                        typeof(IntVec3)
                    });
                takeHaulTarget = AccessTools.Method(haulTargetsType, "TryTake");
                MethodInfo cellIsGoodStore = AccessTools.Method(storageCompatType, "CellIsGoodStore");
                if (jobOnThing == null
                    || rememberTarget == null
                    || takeHaulTarget == null
                    || cellIsGoodStore == null)
                {
                    throw new MissingMethodException("Strata cross-floor hauling API was not found");
                }

                harmony.Patch(
                    jobOnThing,
                    prefix: new HarmonyMethod(
                        typeof(StrataCompatibility),
                        nameof(JobOnThingPrefix)),
                    postfix: new HarmonyMethod(
                        typeof(StrataCompatibility),
                        nameof(JobOnThingPostfix))
                    {
                        priority = Priority.Last
                    },
                    finalizer: new HarmonyMethod(
                        typeof(StrataCompatibility),
                        nameof(JobOnThingFinalizer)));
                harmony.Patch(
                    rememberTarget,
                    prefix: new HarmonyMethod(
                        typeof(StrataCompatibility),
                        nameof(RememberTargetPrefix)));
                harmony.Patch(
                    cellIsGoodStore,
                    prefix: new HarmonyMethod(
                        typeof(StrataCompatibility),
                        nameof(CellIsGoodStorePrefix)),
                    finalizer: new HarmonyMethod(
                        typeof(StrataCompatibility),
                        nameof(CellIsGoodStoreFinalizer)));

                PatchArrivalLifecycle(harmony);
                PatchInventoryDelivery(harmony);
                PatchStorageJob(harmony, storageCompatType);
                RegisterWorkProbe(workSignalsType);

                Log.Message("[Storage Group Quotas] Strata cross-floor quota compatibility enabled.");
            }
            catch (Exception exception)
            {
                LogRuntimeWarning("initialization failed: " + exception.GetBaseException().Message);
            }
        }

        internal static int LimitRemainingForReservations(
            Thing thing,
            IntVec3 cell,
            Map map,
            int unreservedRemaining,
            int excludePawnId = -1)
        {
            if (!applied || unreservedRemaining <= 0 || unreservedRemaining == int.MaxValue)
            {
                return unreservedRemaining;
            }

            int effectiveExclusion = excludePawnId >= 0
                ? excludePawnId
                : currentCellCheckPawnId;
            int reservationAware = AvailableAt(
                thing,
                cell,
                map,
                effectiveExclusion,
                out _,
                out _,
                out _);
            return Math.Min(unreservedRemaining, reservationAware);
        }

        internal static void ResetSession()
        {
            PlansByPawn.Clear();
            PlansByJob = new ConditionalWeakTable<Job, TargetPlan>();
            ReservationsByPawn.Clear();
            LastContentSignalTickByMap.Clear();
            PendingRecoveryCallbacks.Clear();
            currentCellCheckPawnId = -1;
            currentStorageJobPawnId = -1;
            quotaRejectedDuringStorageJob = false;
            startingRecoveryPawnId = -1;
            QuotaUtility.ResetSession();
            RebuildRecoveryReservations();
        }

        private static void RebuildRecoveryReservations()
        {
            if (!applied || Find.Maps == null)
            {
                return;
            }

            foreach (Map map in Find.Maps)
            {
                IReadOnlyList<Pawn> pawns = map?.mapPawns?.AllPawnsSpawned;
                if (pawns == null)
                {
                    continue;
                }

                for (int i = 0; i < pawns.Count; i++)
                {
                    Pawn pawn = pawns[i];
                    Job job = pawn?.CurJob;
                    Thing carried = pawn?.carryTracker?.CarriedThing;
                    Job queuedRecovery = pawn?.jobs?.jobQueue?
                        .FirstOrDefault(queued =>
                            IsRejectedCargoRecoveryJob(queued?.job))?
                        .job;
                    if (carried == null
                        || (!IsRejectedCargoRecoveryJob(job)
                            && queuedRecovery == null))
                    {
                        continue;
                    }

                    TransferReservation reservation = EnsureArrivalReservation(pawn, carried);
                    reservation.QuotaRejectedFallback = true;
                    reservation.RecoveryJob = IsRejectedCargoRecoveryJob(job)
                        ? job
                        : queuedRecovery;
                    reservation.NextRecoveryTick = 0;
                    if (!IsRejectedCargoRecoveryHaul(job))
                    {
                        QueueRejectedCargoRecovery(
                            pawn,
                            reservation,
                            holdInWait: false,
                            notifyStrata: false);
                    }
                }
            }
        }

        internal static void NoteQuotaRejectedDuringStorageSearch()
        {
            if (currentStorageJobPawnId >= 0
                && currentCellCheckPawnId == currentStorageJobPawnId)
            {
                quotaRejectedDuringStorageJob = true;
            }
        }

        internal static void NotifyStoredContentsChanged(Thing thing)
        {
            if (!applied
                || notifyWorkChanged == null
                || thing?.def == null
                || (!thing.def.EverHaulable && !thing.def.alwaysHaulable)
                || !thing.Spawned
                || thing.Map == null)
            {
                return;
            }

            ISlotGroup scope = QuotaUtility.ScopeForThing(thing);
            if (scope == null || !QuotaDataStore.Get(scope.Settings).Active)
            {
                return;
            }

            Map map = thing.Map;
            QuotaUtility.InvalidateOverflowSnapshot(map);
            int tick = Find.TickManager?.TicksGame ?? 0;
            if (LastContentSignalTickByMap.TryGetValue(map.uniqueID, out int lastTick)
                && tick >= lastTick
                && tick - lastTick < 30)
            {
                return;
            }

            LastContentSignalTickByMap[map.uniqueID] = tick;
            try
            {
                notifyWorkChanged.Invoke(null, new object[] { map });
            }
            catch (Exception exception)
            {
                LogRuntimeWarning("could not notify Strata about changed storage contents: "
                    + exception.GetBaseException().Message);
            }
        }

        internal static void NotifySettingsChanged(StorageSettings settings)
        {
            if (!applied || notifyWorkChanged == null || settings == null)
            {
                return;
            }

            Map map = FindMapForSettings(settings);
            if (map == null)
            {
                return;
            }

            try
            {
                notifyWorkChanged.Invoke(null, new object[] { map });
            }
            catch (Exception exception)
            {
                LogRuntimeWarning("could not notify Strata about changed quota work: "
                    + exception.GetBaseException().Message);
            }
        }

        internal static void NotifyCellPlacementFinished(Pawn pawn)
        {
            if (pawn != null
                && ReservationsByPawn.TryGetValue(pawn.thingIDNumber, out TransferReservation reservation)
                && reservation.Arrived
                && pawn.carryTracker?.CarriedThing == null)
            {
                ReservationsByPawn.Remove(pawn.thingIDNumber);
                RemoveQueuedRecoveryWaits(pawn);
            }
        }

        internal static bool PrepareFinalCellPlacement(Pawn pawn)
        {
            if (!applied || pawn?.Map == null)
            {
                return true;
            }

            PruneReservations();
            Thing carried = pawn.carryTracker?.CarriedThing;
            Job job = pawn.CurJob;
            if (carried == null || job == null || !job.targetB.IsValid)
            {
                return true;
            }

            IntVec3 cell = job.targetB.HasThing
                ? job.targetB.Thing.Position
                : job.targetB.Cell;
            if (!cell.IsValid || !cell.InBounds(pawn.Map))
            {
                return true;
            }

            if (IsRejectedCargoRecoveryJob(job))
            {
                if (IsSafeNonStorageCell(pawn, carried, cell))
                {
                    return true;
                }

                TransferReservation recovery = EnsureArrivalReservation(pawn, carried);
                recovery.QuotaRejectedFallback = true;
                recovery.RecoveryJob = job;
                bool recovered = TryMoveRejectedCargoOutsideStorage(pawn, carried);
                if (!recovered)
                {
                    QueueRejectedCargoRecovery(pawn, recovery, holdInWait: true);
                }

                return false;
            }

            if (!IsQuotaStoragePlacement(job, cell, pawn.Map))
            {
                return true;
            }

            int remaining = QuotaUtility.RemainingForDestination(
                carried,
                cell,
                pawn.Map,
                carried.stackCount);
            remaining = LimitRemainingForReservations(
                carried,
                cell,
                pawn.Map,
                remaining,
                pawn.thingIDNumber);
            if (remaining == int.MaxValue)
            {
                return true;
            }

            int physical = PhysicalCapacityAtExcluding(
                carried,
                cell,
                pawn.Map,
                pawn.thingIDNumber);
            if (Math.Min(remaining, physical) >= carried.stackCount)
            {
                return true;
            }

            // The placement toils used by both HaulToCell and HaulToContainer deposit the
            // entire carried stack. If the live budget shrank after the local job was made,
            // move the whole stack to safe non-storage ground rather than overfill the quota.
            // The normal quota work giver can redistribute it on a later scan.
            MarkQuotaRejectedFallback(pawn, carried, cell);
            bool handledSafely = TryMoveRejectedCargoOutsideStorage(pawn, carried);

            if (!handledSafely)
            {
                // Keep control of the carried stack. A plain EndCurrentJob would invoke vanilla's
                // Near cleanup and could then let the pawn start unrelated work while still
                // carrying. Strata's public arrival queue retries this protected recovery path.
                QueueRejectedCargoRecovery(pawn, reservation: null, holdInWait: true);
            }

            return false;
        }

        private static void MarkQuotaRejectedFallback(
            Pawn pawn,
            Thing thing,
            IntVec3 cell)
        {
            if (pawn?.Map == null || thing?.def == null)
            {
                return;
            }

            TransferReservation reservation = EnsureArrivalReservation(pawn, thing);
            AvailableAt(
                thing,
                cell,
                pawn.Map,
                pawn.thingIDNumber,
                out QuotaKey quota,
                out _,
                out bool reservesNewStack);
            RebindArrivingReservation(
                pawn,
                thing,
                cell,
                quota,
                reservesNewStack,
                reservation);
            reservation.QuotaRejectedFallback = true;
        }

        private static bool IsQuotaStoragePlacement(Job job, IntVec3 cell, Map map)
        {
            ISlotGroup scope = QuotaUtility.ScopeAt(cell, map);
            if (scope == null || !QuotaDataStore.Get(scope.Settings).Active)
            {
                return false;
            }

            if (job.haulMode == HaulMode.ToCellStorage)
            {
                return true;
            }

            if (job.haulMode != HaulMode.ToContainer || !job.targetB.HasThing)
            {
                return false;
            }

            return job.targetB.Thing is IHaulDestination destination
                && destination.HaulDestinationEnabled;
        }

        private static bool TryMoveRejectedCargoOutsideStorage(Pawn pawn, Thing carried)
        {
            if (pawn?.Map == null || carried == null || pawn.carryTracker?.CarriedThing != carried)
            {
                return false;
            }

            if (TryFindAdjacentSafeNonStorageCell(pawn, carried, out IntVec3 immediateCell)
                && pawn.carryTracker.TryDropCarriedThing(
                    immediateCell,
                    ThingPlaceMode.Direct,
                    out Thing _))
            {
                return true;
            }

            // Strata intentionally refuses to start delivery jobs for pawns who cannot accept
            // normal work. Preserve that rule: a drafted combat order or a downed pawn must not
            // be replaced by SGQ's fallback haul. The durable recovery marker will resume later.
            if (!pawn.Spawned || pawn.Dead || pawn.Downed || pawn.Drafted)
            {
                return false;
            }

            // A pawn can arrive in the middle of a large shelf/stockpile footprint. Do not
            // teleport cargo across it, and do not fall back to Strata's Near placement (which
            // may choose another quota cell). Instead, walk the already-carried stack to the
            // nearest reachable non-storage cell using a normal non-storage haul job.
            int fallbackCells = GenRadial.NumCellsInRadius(40f);
            for (int i = 0; i < fallbackCells; i++)
            {
                IntVec3 candidate = pawn.Position + GenRadial.RadialPattern[i];
                if (!IsSafeNonStorageCell(pawn, carried, candidate)
                    || !pawn.CanReserveAndReach(
                        candidate,
                        PathEndMode.OnCell,
                        pawn.NormalMaxDanger()))
                {
                    continue;
                }

                if (TryStartNonStorageDelivery(pawn, carried, candidate))
                {
                    return true;
                }
            }

            // Radius 40 is only the fast, distance-ordered pass. Very large storage zones or
            // divided bases can put the first safe ground farther away, so make one region-based
            // whole-map search before retaining the cargo. This prevents a reachable pawn from
            // being permanently stuck merely because the local search radius was exhausted.
            TraverseParms traverse = TraverseParms.For(pawn, pawn.NormalMaxDanger());
            if (CellFinder.TryFindRandomReachableNearbyCell(
                    pawn.Position,
                    pawn.Map,
                    9999f,
                    traverse,
                    candidate => IsSafeNonStorageCell(pawn, carried, candidate)
                        && pawn.CanReserve(candidate),
                    null,
                    out IntVec3 globalCell)
                && TryStartNonStorageDelivery(pawn, carried, globalCell))
            {
                return true;
            }

            return false;
        }

        private static bool TryStartNonStorageDelivery(
            Pawn pawn,
            Thing carried,
            IntVec3 cell)
        {
            JobDef recoveryDef = DefDatabase<JobDef>.GetNamedSilentFail(
                RecoveryHaulJobDefName) ?? JobDefOf.HaulToCell;
            Job job = JobMaker.MakeJob(recoveryDef, carried, cell);
            job.count = carried.stackCount;
            job.haulMode = HaulMode.ToCellNonStorage;
            job.haulOpportunisticDuplicates = false;
            job.ignoreDesignations = true;
            if (!job.TryMakePreToilReservations(pawn, errorOnFailed: false))
            {
                pawn.ClearReservationsForJob(job);
                return false;
            }

            int previousStartingPawn = startingRecoveryPawnId;
            startingRecoveryPawnId = pawn.thingIDNumber;
            try
            {
                pawn.jobs.StartJob(
                    job,
                    JobCondition.InterruptForced,
                    keepCarryingThingOverride: true);
            }
            finally
            {
                startingRecoveryPawnId = previousStartingPawn;
            }
            if (!ReferenceEquals(pawn.CurJob, job))
            {
                pawn.ClearReservationsForJob(job);
                return false;
            }

            if (ReservationsByPawn.TryGetValue(
                pawn.thingIDNumber,
                out TransferReservation reservation))
            {
                reservation.RecoveryJob = job;
                reservation.NextRecoveryTick = 0;
            }
            RemoveQueuedRecoveryWaits(pawn);
            return true;
        }

        private static bool IsRejectedCargoRecoveryJob(Job job)
        {
            string defName = job?.def?.defName;
            return defName == RecoveryHaulJobDefName
                || defName == RecoveryWaitJobDefName;
        }

        private static bool IsRejectedCargoRecoveryHaul(Job job)
        {
            return job?.def?.defName == RecoveryHaulJobDefName;
        }

        private static bool IsRejectedCargoRecoveryWait(Job job)
        {
            return job?.def?.defName == RecoveryWaitJobDefName;
        }

        private static bool TryFindAdjacentSafeNonStorageCell(
            Pawn pawn,
            Thing carried,
            out IntVec3 cell)
        {
            cell = IntVec3.Invalid;
            if (pawn?.Map == null || carried == null)
            {
                return false;
            }

            // Direct placement must never teleport rejected cargo or cut across a blocked
            // diagonal. Try the pawn's own cell, then only the four edge-adjacent cells;
            // anything else uses a real pathing job.
            if (IsSafeNonStorageCell(pawn, carried, pawn.Position))
            {
                cell = pawn.Position;
                return true;
            }

            for (int i = 0; i < GenAdj.CardinalDirections.Length; i++)
            {
                IntVec3 candidate = pawn.Position + GenAdj.CardinalDirections[i];
                if (IsSafeNonStorageCell(pawn, carried, candidate))
                {
                    cell = candidate;
                    return true;
                }
            }

            return false;
        }

        private static bool RejectedCargoDropPrefix(
            Pawn_CarryTracker __instance,
            ref IntVec3 __0,
            ref ThingPlaceMode __1,
            ref bool __result)
        {
            Pawn pawn = __instance?.pawn;
            Thing carried = __instance?.CarriedThing;
            if (pawn == null
                || carried == null
                || __1 != ThingPlaceMode.Near
                || !ReservationsByPawn.TryGetValue(
                    pawn.thingIDNumber,
                    out TransferReservation reservation)
                || !reservation.Arrived
                || !ReservationNeedsProtectedDrop(reservation))
            {
                return true;
            }

            reservation.QuotaRejectedFallback = true;

            // Pawn_JobTracker drops carried things with Near when a haul is interrupted or cannot
            // start. Near may redirect right back onto the full quota shelf. Convert that implicit
            // cleanup to an adjacent verified non-storage Direct drop, or keep carrying when no
            // such cell exists. The normal fallback delivery can then retry safely.
            if (TryFindAdjacentSafeNonStorageCell(pawn, carried, out IntVec3 safeCell))
            {
                __0 = safeCell;
                __1 = ThingPlaceMode.Direct;
                return true;
            }

            QueueRejectedCargoRecovery(
                pawn,
                reservation,
                holdInWait: false,
                notifyStrata: false,
                enqueueAfterExistingJobs: true);
            __result = false;
            return false;
        }

        private static void RejectedCargoDropPostfix(
            Pawn_CarryTracker __instance,
            bool __result)
        {
            Pawn pawn = __instance?.pawn;
            if (__result
                && pawn != null
                && __instance.CarriedThing == null
                && ReservationsByPawn.TryGetValue(
                    pawn.thingIDNumber,
                    out TransferReservation reservation)
                && reservation.Arrived
                && reservation.QuotaRejectedFallback)
            {
                ReservationsByPawn.Remove(pawn.thingIDNumber);
                RemoveQueuedRecoveryWaits(pawn);
            }
        }

        private static void RemoveQueuedRecoveryWaits(Pawn pawn)
        {
            pawn?.jobs?.jobQueue?.RemoveAll(
                pawn,
                job => IsRejectedCargoRecoveryWait(job));
        }

        private static void QueueRejectedCargoRecovery(
            Pawn pawn,
            TransferReservation reservation,
            bool holdInWait,
            bool currentJobEnding = false,
            bool notifyStrata = true,
            bool enqueueAfterExistingJobs = false)
        {
            if (pawn?.carryTracker?.CarriedThing == null)
            {
                if (pawn != null)
                {
                    ReservationsByPawn.Remove(pawn.thingIDNumber);
                }
                return;
            }

            reservation ??= EnsureArrivalReservation(
                pawn,
                pawn.carryTracker.CarriedThing);
            reservation.Arrived = true;
            reservation.QuotaRejectedFallback = true;
            int tick = Find.TickManager?.TicksGame ?? 0;
            reservation.StartedTick = tick;
            if (reservation.NextRecoveryTick <= tick)
            {
                // A finishing wait is the timer firing: let the callback queued below make one
                // real safe-ground attempt. A newly-installed wait starts a fresh cooldown.
                reservation.NextRecoveryTick = currentJobEnding ? tick : tick + 60;
            }

            JobDef waitDef = DefDatabase<JobDef>.GetNamedSilentFail(
                RecoveryWaitJobDefName);
            bool currentHasDurableMarker = !currentJobEnding
                && IsRejectedCargoRecoveryJob(pawn.CurJob);
            bool queuedHasDurableMarker = pawn.jobs?.jobQueue != null
                && pawn.jobs.jobQueue.Any(queued =>
                    IsRejectedCargoRecoveryJob(queued?.job));

            if (waitDef != null
                && holdInWait
                && pawn.Spawned
                && !pawn.Dead
                && !pawn.Downed
                && !pawn.Drafted
                && !IsRejectedCargoRecoveryWait(pawn.CurJob))
            {
                Job wait = JobMaker.MakeJob(waitDef, 60);
                int previousStartingPawn = startingRecoveryPawnId;
                startingRecoveryPawnId = pawn.thingIDNumber;
                try
                {
                    pawn.jobs.StartJob(
                        wait,
                        JobCondition.InterruptForced,
                        keepCarryingThingOverride: true);
                }
                finally
                {
                    startingRecoveryPawnId = previousStartingPawn;
                }
                currentHasDurableMarker = ReferenceEquals(pawn.CurJob, wait);
                queuedHasDurableMarker = queuedHasDurableMarker
                    || (pawn.jobs?.jobQueue != null
                        && pawn.jobs.jobQueue.Any(queued =>
                            IsRejectedCargoRecoveryJob(queued?.job)));
            }

            if (waitDef != null
                && pawn.jobs?.jobQueue != null
                && !currentHasDurableMarker
                && !queuedHasDurableMarker)
            {
                // Queueing is safe even when this method is called from JobDriver.Cleanup, where
                // recursively starting another job would not be. The queue is serialized, so the
                // one-tick window before Strata retries also survives save/load.
                Job queuedWait = JobMaker.MakeJob(waitDef, 60);
                if (enqueueAfterExistingJobs)
                {
                    pawn.jobs.jobQueue.EnqueueLast(queuedWait);
                }
                else
                {
                    pawn.jobs.jobQueue.EnqueueFirst(queuedWait);
                }
            }

            if (!notifyStrata || notifyHaulArrival == null)
            {
                return;
            }

            // A durable wait is already the timer. Do not also enqueue an immediate Strata
            // callback: that would bypass the cooldown and could race a player interruption.
            if (holdInWait && (currentHasDurableMarker || queuedHasDurableMarker))
            {
                return;
            }

            try
            {
                PendingRecoveryCallbacks.Add(pawn.thingIDNumber);
                notifyHaulArrival.Invoke(null, new object[] { pawn });
            }
            catch (Exception exception)
            {
                PendingRecoveryCallbacks.Remove(pawn.thingIDNumber);
                LogRuntimeWarning("could not queue Strata's rejected-cargo recovery: "
                    + exception.GetBaseException().Message);
            }
        }

        private static bool IsSafeNonStorageCell(
            Pawn pawn,
            Thing carried,
            IntVec3 candidate)
        {
            Map map = pawn.Map;
            if (!candidate.InBounds(map)
                || candidate.GetSlotGroup(map) != null
                || candidate.IsForbidden(pawn)
                || !candidate.Standable(map)
                || candidate.ContainsStaticFire(map)
                || GenPlace.HaulPlaceBlockerIn(carried, candidate, map, true) != null)
            {
                return false;
            }

            if (carried.def.BlocksPlanting()
                && map.zoneManager.ZoneAt(candidate) is Zone_Growing)
            {
                return false;
            }

            return !candidate.GetThingList(map).Any(thing =>
                thing is IHaulDestination destination
                && destination.HaulDestinationEnabled);
        }

        private static int PhysicalCapacityAtExcluding(
            Thing thing,
            IntVec3 cell,
            Map map,
            int pawnId)
        {
            int previous = currentCellCheckPawnId;
            currentCellCheckPawnId = pawnId;
            try
            {
                return ExternalStorageUtility.PhysicalCapacityAt(thing, cell, map);
            }
            finally
            {
                currentCellCheckPawnId = previous;
            }
        }

        private static void PatchArrivalLifecycle(Harmony harmony)
        {
            Type portalRelayType = AccessTools.TypeByName(PortalRelayTypeName);
            Type portalUtilityType = AccessTools.TypeByName(PortalUtilityTypeName);
            haulDriverType = AccessTools.TypeByName(HaulDriverTypeName);
            notifyHaulArrival = portalUtilityType == null
                ? null
                : AccessTools.Method(
                    portalUtilityType,
                    "NotifyHaulArrival",
                    new[] { typeof(Pawn) });
            if (notifyHaulArrival != null)
            {
                harmony.Patch(
                    notifyHaulArrival,
                    prefix: new HarmonyMethod(
                        typeof(StrataCompatibility),
                        nameof(HaulArrivalNotifiedPrefix)));
            }

            MethodInfo preToilReservations = haulDriverType == null
                ? null
                : AccessTools.Method(
                    haulDriverType,
                    nameof(JobDriver.TryMakePreToilReservations),
                    new[] { typeof(bool) });
            if (preToilReservations != null)
            {
                harmony.Patch(
                    preToilReservations,
                    postfix: new HarmonyMethod(
                        typeof(StrataCompatibility),
                        nameof(HaulPreToilReservationsPostfix))
                    {
                        priority = Priority.Last
                    });
            }

            MethodInfo finishHaul = portalRelayType == null
                ? null
                : AccessTools.Method(portalRelayType, "FinishHaul");
            portalHasIntent = portalRelayType == null
                ? null
                : AccessTools.Method(
                    portalRelayType,
                    "HasIntent",
                    new[] { typeof(Pawn) });
            if (finishHaul != null)
            {
                harmony.Patch(
                    finishHaul,
                    prefix: new HarmonyMethod(
                        typeof(StrataCompatibility),
                        nameof(FinishHaulPrefix)),
                    postfix: new HarmonyMethod(
                        typeof(StrataCompatibility),
                        nameof(FinishHaulPostfix)),
                    finalizer: new HarmonyMethod(
                        typeof(StrataCompatibility),
                        nameof(FinishHaulFinalizer)));
            }

            MethodInfo continueRelay = portalRelayType == null
                ? null
                : AccessTools.Method(
                    portalRelayType,
                    "Continue",
                    new[] { typeof(int) });
            if (continueRelay != null && portalHasIntent != null)
            {
                harmony.Patch(
                    continueRelay,
                    postfix: new HarmonyMethod(
                        typeof(StrataCompatibility),
                        nameof(ContinueRelayPostfix)));
            }

            MethodInfo forceDrop = portalUtilityType == null
                ? null
                : AccessTools.Method(portalUtilityType, "TryForceDropCarriedCargo");
            if (forceDrop != null)
            {
                harmony.Patch(
                    forceDrop,
                    prefix: new HarmonyMethod(
                        typeof(StrataCompatibility),
                        nameof(ForceDropPrefix)),
                    postfix: new HarmonyMethod(
                        typeof(StrataCompatibility),
                        nameof(ForceDropPostfix)));
            }

            MethodInfo startHaulDelivery = portalUtilityType == null
                ? null
                : AccessTools.Method(
                    portalUtilityType,
                    "TryStartHaulDelivery",
                    new[] { typeof(Pawn), typeof(int), typeof(IntVec3) });
            if (startHaulDelivery != null)
            {
                harmony.Patch(
                    startHaulDelivery,
                    prefix: new HarmonyMethod(
                        typeof(StrataCompatibility),
                        nameof(TryStartHaulDeliveryPrefix)),
                    finalizer: new HarmonyMethod(
                        typeof(StrataCompatibility),
                        nameof(TryStartHaulDeliveryFinalizer)));
            }

            MethodInfo deliverCarriedOrDrop = portalUtilityType == null
                ? null
                : AccessTools.Method(
                    portalUtilityType,
                    "DeliverCarriedOrDrop",
                    new[] { typeof(Pawn) });
            if (deliverCarriedOrDrop != null)
            {
                harmony.Patch(
                    deliverCarriedOrDrop,
                    prefix: new HarmonyMethod(
                        typeof(StrataCompatibility),
                        nameof(DeliverCarriedOrDropPrefix)));
            }

            MethodInfo dropCarriedThing = AccessTools.Method(
                typeof(Pawn_CarryTracker),
                nameof(Pawn_CarryTracker.TryDropCarriedThing),
                new[]
                {
                    typeof(IntVec3),
                    typeof(ThingPlaceMode),
                    typeof(Thing).MakeByRefType(),
                    typeof(Action<Thing, int>)
                });
            if (dropCarriedThing != null)
            {
                harmony.Patch(
                    dropCarriedThing,
                    prefix: new HarmonyMethod(
                        typeof(StrataCompatibility),
                        nameof(RejectedCargoDropPrefix))
                    {
                        priority = Priority.First,
                        before = new[] { "azraelgodking.strata" }
                    },
                    postfix: new HarmonyMethod(
                        typeof(StrataCompatibility),
                        nameof(RejectedCargoDropPostfix)));
            }

            MethodInfo cleanup = AccessTools.Method(
                typeof(JobDriver),
                nameof(JobDriver.Cleanup),
                new[] { typeof(JobCondition) });
            if (haulDriverType != null && cleanup != null)
            {
                harmony.Patch(
                    cleanup,
                    postfix: new HarmonyMethod(
                        typeof(StrataCompatibility),
                        nameof(JobDriverCleanupPostfix)));
            }

            MethodInfo stopAll = AccessTools.Method(
                typeof(Pawn_JobTracker),
                nameof(Pawn_JobTracker.StopAll),
                new[] { typeof(bool), typeof(bool) });
            if (stopAll != null)
            {
                harmony.Patch(
                    stopAll,
                    postfix: new HarmonyMethod(
                        typeof(StrataCompatibility),
                        nameof(StopAllPostfix)));
            }
        }

        private static void PatchInventoryDelivery(Harmony harmony)
        {
            Type inventoryCompatType = AccessTools.TypeByName(InventoryCompatTypeName);
            MethodInfo deliverInventory = inventoryCompatType == null
                ? null
                : AccessTools.Method(
                    inventoryCompatType,
                    "TryDeliverInventory",
                    new[] { typeof(Pawn) });
            if (deliverInventory != null)
            {
                harmony.Patch(
                    deliverInventory,
                    prefix: new HarmonyMethod(
                        typeof(StrataCompatibility),
                        nameof(TryDeliverInventoryPrefix))
                    {
                        priority = Priority.First
                    });
            }
        }

        private static void PatchStorageJob(Harmony harmony, Type storageCompatType)
        {
            MethodInfo makeStorageJob = AccessTools.Method(
                storageCompatType,
                "TryMakeStorageJob",
                new[] { typeof(Pawn), typeof(Thing), typeof(IntVec3) });
            if (makeStorageJob != null)
            {
                harmony.Patch(
                    makeStorageJob,
                    prefix: new HarmonyMethod(
                        typeof(StrataCompatibility),
                        nameof(TryMakeStorageJobPrefix)),
                    postfix: new HarmonyMethod(
                        typeof(StrataCompatibility),
                        nameof(TryMakeStorageJobPostfix))
                    {
                        priority = Priority.Last
                    },
                    finalizer: new HarmonyMethod(
                        typeof(StrataCompatibility),
                        nameof(TryMakeStorageJobFinalizer)));
            }
        }

        private static void RegisterWorkProbe(Type workSignalsType)
        {
            Type probeType = AccessTools.TypeByName(WorkSignalsTypeName + "+WorkProbe");
            MethodInfo register = AccessTools.Method(workSignalsType, "RegisterWorkProbe");
            notifyWorkChanged = AccessTools.Method(
                workSignalsType,
                "Notify_WorkChanged",
                new[] { typeof(Map) });
            MethodInfo probeMethod = AccessTools.Method(
                typeof(StrataCompatibility),
                nameof(HasQuotaWork));
            if (probeType == null || register == null || notifyWorkChanged == null || probeMethod == null)
            {
                throw new MissingMethodException("Strata work relay registration API was not found");
            }

            registeredWorkProbe = Delegate.CreateDelegate(probeType, probeMethod);
            register.Invoke(null, new object[] { registeredWorkProbe });
        }

        private static bool HasQuotaWork(Pawn pawn, Map map)
        {
            if (pawn == null || map == null || pawn.Dead || pawn.Downed)
            {
                return false;
            }

            Pawn_WorkSettings work = pawn.workSettings;
            if (work == null || !work.EverWork || !work.WorkIsActive(WorkTypeDefOf.Hauling))
            {
                return false;
            }

            return QuotaUtility.OverflowThings(map).Any(thing => thing != null && !thing.Destroyed);
        }

        private static void JobOnThingPrefix(Pawn __0)
        {
            capturingHaulTarget = __0 != null;
            capturingPawnId = __0?.thingIDNumber ?? -1;
            if (__0 != null)
            {
                PlansByPawn.Remove(__0.thingIDNumber);
            }
        }

        private static void JobOnThingPostfix(Pawn __0, Thing __1, ref Job __result)
        {
            if (__0 == null)
            {
                return;
            }

            int pawnId = __0.thingIDNumber;
            if (!PlansByPawn.TryGetValue(pawnId, out TargetPlan plan))
            {
                return;
            }

            PlansByPawn.Remove(pawnId);
            capturingHaulTarget = false;
            capturingPawnId = -1;
            if (__result == null || __1 == null)
            {
                ClearPendingHaulTarget(__0);
                return;
            }

            plan.ThingId = __1.thingIDNumber;

            int available = AvailableAt(
                __1,
                plan.Cell,
                plan.Map,
                -1,
                out QuotaKey quota,
                out CellKey cellKey,
                out _);
            if (available == int.MaxValue)
            {
                return;
            }

            int physical = ExternalStorageUtility.PhysicalCapacityAt(__1, plan.Cell, plan.Map);
            int count = Math.Min(Math.Min(__result.count, __1.stackCount), available);
            count = Math.Min(count, physical);
            if (count <= 0)
            {
                ClearPendingHaulTarget(__0);
                __result = null;
                return;
            }

            __result.count = count;
            __result.haulOpportunisticDuplicates = false;
            plan.Quota = quota;
            plan.CellKey = cellKey;
            PlansByJob.Remove(__result);
            PlansByJob.Add(__result, plan);
        }

        private static Exception JobOnThingFinalizer(Pawn __0, Exception __exception)
        {
            if (__0 != null)
            {
                PlansByPawn.Remove(__0.thingIDNumber);
            }

            capturingHaulTarget = false;
            capturingPawnId = -1;
            if (__exception != null)
            {
                ClearPendingHaulTarget(__0);
            }
            return __exception;
        }

        private static void RememberTargetPrefix(Pawn __0, Map __1, IntVec3 __4)
        {
            if (!capturingHaulTarget
                || __0 == null
                || __0.thingIDNumber != capturingPawnId
                || __1 == null
                || !__4.IsValid)
            {
                return;
            }

            PlansByPawn[__0.thingIDNumber] = new TargetPlan
            {
                Map = __1,
                Cell = __4
            };
        }

        private static void CellIsGoodStorePrefix(Pawn __3, out int __state)
        {
            __state = currentCellCheckPawnId;
            currentCellCheckPawnId = __3?.thingIDNumber ?? -1;
        }

        private static Exception CellIsGoodStoreFinalizer(Exception __exception, int __state)
        {
            currentCellCheckPawnId = __state;
            return __exception;
        }

        private static void FinishHaulPrefix(Pawn __0)
        {
            if (__0 != null
                && ReservationsByPawn.TryGetValue(__0.thingIDNumber, out TransferReservation reservation))
            {
                reservation.Arrived = true;
                reservation.StartedTick = Find.TickManager?.TicksGame ?? 0;
            }
        }

        private static void HaulArrivalNotifiedPrefix(Pawn __0)
        {
            if (__0 == null
                || !ReservationsByPawn.TryGetValue(
                    __0.thingIDNumber,
                    out TransferReservation reservation)
                || !ReservationMatchesCarriedCargo(
                    reservation,
                    __0.carryTracker?.CarriedThing))
            {
                return;
            }

            // Stairs notify Strata as soon as the pawn enters each floor, one tick before the
            // relay continuation is evaluated. Start drop protection here so drafting/downing
            // the pawn in that paused arrival window cannot release an unprotected Near drop.
            reservation.Arrived = true;
            reservation.StartedTick = Find.TickManager?.TicksGame ?? 0;
        }

        private static void TryStartHaulDeliveryPrefix(Pawn __0, out int __state)
        {
            __state = startingRecoveryPawnId;
            startingRecoveryPawnId = __0?.thingIDNumber ?? -1;
        }

        private static Exception TryStartHaulDeliveryFinalizer(
            Pawn __0,
            bool __result,
            Exception __exception,
            int __state)
        {
            startingRecoveryPawnId = __state;
            if (__exception == null && __result)
            {
                RemoveQueuedRecoveryWaits(__0);
            }
            return __exception;
        }

        private static bool DeliverCarriedOrDropPrefix(Pawn __0)
        {
            if (__0 == null || !PendingRecoveryCallbacks.Remove(__0.thingIDNumber))
            {
                return true;
            }

            Thing carried = __0.carryTracker?.CarriedThing;
            if (carried == null || carried is Pawn)
            {
                // The cargo may have been safely dropped while this one-tick callback was
                // pending. Do not let Strata reinterpret the stale callback as a request to
                // pick up an unrelated nearby item.
                return false;
            }

            Job current = __0.CurJob;
            JobDef currentDef = current?.def;
            return current == null
                || IsRejectedCargoRecoveryWait(current)
                || currentDef == JobDefOf.Wait
                || currentDef == JobDefOf.Wait_MaintainPosture
                || currentDef == JobDefOf.Wait_Combat;
        }

        private static void FinishHaulPostfix(Pawn __0)
        {
            if (__0 == null
                || !ReservationsByPawn.TryGetValue(
                    __0.thingIDNumber,
                    out TransferReservation reservation))
            {
                return;
            }

            if (__0.carryTracker?.CarriedThing == null)
            {
                ReservationsByPawn.Remove(__0.thingIDNumber);
                return;
            }

            JobDriver driver = __0.jobs?.curDriver;
            if (driver == null || reservation.FinishActionAttached)
            {
                return;
            }

            reservation.FinishActionAttached = true;
            reservation.FinishDriver = driver;
            int pawnId = __0.thingIDNumber;
            TransferReservation captured = reservation;
            driver.AddFinishAction(_ =>
            {
                if (ReservationsByPawn.TryGetValue(pawnId, out TransferReservation current)
                    && ReferenceEquals(current, captured)
                    && __0.carryTracker?.CarriedThing == null)
                {
                    ReservationsByPawn.Remove(pawnId);
                }
            });
        }

        private static Exception FinishHaulFinalizer(
            Pawn __0,
            Exception __exception)
        {
            if (__exception != null)
            {
                // If Strata managed to start the local delivery before another part of FinishHaul
                // failed, keep the reservation until that driver ends. Otherwise retain it for
                // Strata's carried-cargo recovery or the bounded expiry cleanup.
                FinishHaulPostfix(__0);
            }

            return __exception;
        }

        private static void ContinueRelayPostfix(int __0)
        {
            if (!ReservationsByPawn.TryGetValue(__0, out TransferReservation reservation))
            {
                return;
            }

            Pawn pawn = FindPawn(__0);
            // EnterPortal briefly despawns the pawn. Keep the reservation until the pawn is
            // visible again; the age limit remains the last-resort cleanup for a lost relay.
            if (pawn == null || portalHasIntent == null)
            {
                return;
            }

            bool stillRelaying;
            try
            {
                stillRelaying = (bool)portalHasIntent.Invoke(null, new object[] { pawn });
            }
            catch (Exception exception)
            {
                LogRuntimeWarning("could not inspect Strata's active relay: "
                    + exception.GetBaseException().Message);
                return;
            }

            if (!stillRelaying)
            {
                Thing carried = pawn.carryTracker?.CarriedThing;
                if (ReservationMatchesCarriedCargo(reservation, carried))
                {
                    // On the final floor FinishHaul may already have started a valid local
                    // delivery (or SGQ recovery) driver. Its finish action owns the reservation;
                    // only a relay that lost intent without such a driver needs a new marker.
                    JobDriver driver = pawn.jobs?.curDriver;
                    if (reservation.FinishActionAttached
                        && reservation.FinishDriver != null
                        && ReferenceEquals(driver, reservation.FinishDriver))
                    {
                        reservation.StartedTick = Find.TickManager?.TicksGame ?? 0;
                        return;
                    }

                    reservation.Arrived = true;
                    reservation.QuotaRejectedFallback = true;
                    QueueRejectedCargoRecovery(
                        pawn,
                        reservation,
                        holdInWait: false,
                        notifyStrata: false,
                        enqueueAfterExistingJobs: true);
                }
                else
                {
                    ReservationsByPawn.Remove(__0);
                }
            }
            else
            {
                reservation.StartedTick = Find.TickManager?.TicksGame ?? 0;
            }
        }

        private static bool ForceDropPrefix(Pawn __0, ref bool __result)
        {
            if (__0 == null
                || !ReservationsByPawn.TryGetValue(
                    __0.thingIDNumber,
                    out TransferReservation reservation)
                || !reservation.Arrived
                || !ReservationNeedsProtectedDrop(reservation))
            {
                return true;
            }

            Thing carried = __0.carryTracker?.CarriedThing;
            if (carried == null || carried is Pawn)
            {
                return true;
            }

            // Strata's fallback uses Near, which may redirect onto an adjacent quota shelf.
            // Keep rejected cross-floor cargo strictly on non-storage ground instead.
            reservation.QuotaRejectedFallback = true;
            int tick = Find.TickManager?.TicksGame ?? 0;
            if (tick >= 0
                && reservation.NextRecoveryTick > tick)
            {
                // QueueRejectedCargoRecovery already installed a durable 60-tick wait marker.
                // Let that job's Cleanup schedule the next arrival retry instead of re-adding
                // this pawn to Strata's pending list on every map tick during the cooldown.
                __result = false;
                return false;
            }

            __result = TryMoveRejectedCargoOutsideStorage(__0, carried);
            if (!__result)
            {
                QueueRejectedCargoRecovery(__0, reservation, holdInWait: true);
            }
            return false;
        }

        private static bool ReservationNeedsProtectedDrop(TransferReservation reservation)
        {
            return reservation != null
                && (reservation.QuotaRejectedFallback
                    || reservation.QuotaManagedDestination
                    || ReservationTargetsActiveQuota(reservation));
        }

        private static bool ReservationTargetsActiveQuota(TransferReservation reservation)
        {
            if (reservation == null
                || !reservation.CellKey.Cell.IsValid)
            {
                return false;
            }

            Map map = FindMap(reservation.CellKey.MapId);
            if (map == null || !reservation.CellKey.Cell.InBounds(map))
            {
                return false;
            }

            ISlotGroup scope = QuotaUtility.ScopeAt(reservation.CellKey.Cell, map);
            return scope != null && QuotaDataStore.Get(scope.Settings).Active;
        }

        private static void ForceDropPostfix(Pawn __0, bool __result)
        {
            if (__result
                && __0 != null
                && __0.carryTracker?.CarriedThing == null)
            {
                ReservationsByPawn.Remove(__0.thingIDNumber);
                RemoveQueuedRecoveryWaits(__0);
            }
        }

        private static void JobDriverCleanupPostfix(JobDriver __instance, JobCondition __0)
        {
            if (__instance?.pawn == null)
            {
                return;
            }

            Pawn pawn = __instance.pawn;
            Job job = __instance.job;
            Thing carried = pawn.carryTracker?.CarriedThing;
            if (startingRecoveryPawnId == pawn.thingIDNumber)
            {
                // TryStartNonStorageDelivery is deliberately replacing this driver with the
                // durable recovery haul. Do not enqueue Strata's arrival callback for the driver
                // being replaced, or it would restart the new haul on the next tick.
                return;
            }

            bool recoveryJob = IsRejectedCargoRecoveryJob(job);
            if (recoveryJob)
            {
                if (carried == null)
                {
                    ReservationsByPawn.Remove(pawn.thingIDNumber);
                    RemoveQueuedRecoveryWaits(pawn);
                }
                else
                {
                    TransferReservation recovery = EnsureArrivalReservation(pawn, carried);
                    recovery.QuotaRejectedFallback = true;
                    recovery.RecoveryJob = null;
                    QueueRejectedCargoRecovery(
                        pawn,
                        recovery,
                        holdInWait: false,
                        currentJobEnding: true,
                        notifyStrata: __0 == JobCondition.Succeeded,
                        enqueueAfterExistingJobs: __0 != JobCondition.Succeeded);
                }
                return;
            }

            // Static reservations are not serialized. Reconstruct protection if a saved or
            // otherwise untracked quota-storage job is interrupted while the pawn still carries
            // its whole stack; Pawn_JobTracker would otherwise perform an unchecked Near drop.
            if (carried != null
                && pawn.Map != null
                && job != null
                && job.targetB.IsValid)
            {
                IntVec3 targetCell = job.targetB.HasThing
                    ? job.targetB.Thing.Position
                    : job.targetB.Cell;
                if (targetCell.IsValid
                    && targetCell.InBounds(pawn.Map)
                    && IsQuotaStoragePlacement(job, targetCell, pawn.Map))
                {
                    MarkQuotaRejectedFallback(pawn, carried, targetCell);
                    if (ReservationsByPawn.TryGetValue(
                        pawn.thingIDNumber,
                        out TransferReservation interrupted))
                    {
                        QueueRejectedCargoRecovery(
                            pawn,
                            interrupted,
                            holdInWait: false,
                            currentJobEnding: true,
                            notifyStrata: false,
                            enqueueAfterExistingJobs: __0 != JobCondition.Succeeded);
                    }
                    return;
                }
            }

            if ((haulDriverType == null || !haulDriverType.IsInstanceOfType(__instance))
                && !(__instance is JobDriver_EnterPortal))
            {
                return;
            }

            bool release = __0 != JobCondition.Succeeded;
            if (release
                && haulDriverType != null
                && haulDriverType.IsInstanceOfType(__instance))
            {
                ClearPendingHaulTarget(pawn);
            }

            if (!release && portalHasIntent != null)
            {
                try
                {
                    release = !(bool)portalHasIntent.Invoke(
                        null,
                        new object[] { pawn });
                }
                catch (Exception exception)
                {
                    LogRuntimeWarning("could not verify Strata's relay during job cleanup: "
                        + exception.GetBaseException().Message);
                }
            }

            if (release)
            {
                if (ReservationsByPawn.TryGetValue(
                        pawn.thingIDNumber,
                        out TransferReservation interruptedArrival)
                    && interruptedArrival.Arrived
                    && ReservationMatchesCarriedCargo(
                        interruptedArrival,
                        pawn.carryTracker?.CarriedThing))
                {
                    interruptedArrival.QuotaRejectedFallback = true;
                    QueueRejectedCargoRecovery(
                        pawn,
                        interruptedArrival,
                        holdInWait: false,
                        currentJobEnding: true,
                        notifyStrata: false,
                        enqueueAfterExistingJobs: __0 != JobCondition.Succeeded);
                }
                else
                {
                    ReservationsByPawn.Remove(pawn.thingIDNumber);
                }
            }
            else if (ReservationsByPawn.TryGetValue(
                pawn.thingIDNumber,
                out TransferReservation reservation))
            {
                reservation.StartedTick = Find.TickManager?.TicksGame ?? 0;
            }
        }

        private static void StopAllPostfix(Pawn ___pawn)
        {
            Thing carried = ___pawn?.carryTracker?.CarriedThing;
            if (___pawn == null
                || !ReservationsByPawn.TryGetValue(
                    ___pawn.thingIDNumber,
                    out TransferReservation reservation)
                || !reservation.Arrived
                || !ReservationMatchesCarriedCargo(reservation, carried))
            {
                return;
            }

            // StopAll clears the queue after JobDriver.Cleanup. Reinstall the serialized marker
            // here so drafting, downing, or another hard stop cannot erase recovery protection.
            reservation.QuotaRejectedFallback = true;
            QueueRejectedCargoRecovery(
                ___pawn,
                reservation,
                holdInWait: false,
                notifyStrata: false,
                enqueueAfterExistingJobs: true);
        }

        private static void HaulPreToilReservationsPostfix(
            JobDriver __instance,
            ref bool __result)
        {
            if (__instance?.pawn == null
                || __instance.job == null
                || !PlansByJob.TryGetValue(__instance.job, out TargetPlan plan))
            {
                return;
            }

            PlansByJob.Remove(__instance.job);
            if (!__result)
            {
                ClearPendingHaulTarget(__instance.pawn);
                return;
            }

            Thing thing = __instance.job.GetTarget(TargetIndex.A).Thing;
            if (thing == null)
            {
                ClearPendingHaulTarget(__instance.pawn);
                __instance.pawn.ClearReservationsForJob(__instance.job);
                __result = false;
                return;
            }

            int pawnId = __instance.pawn.thingIDNumber;
            int available = AvailableAt(
                thing,
                plan.Cell,
                plan.Map,
                pawnId,
                out QuotaKey quota,
                out CellKey cellKey,
                out bool reservesNewStack);
            int physical = ExternalStorageUtility.PhysicalCapacityAt(thing, plan.Cell, plan.Map);
            int count = Math.Min(Math.Min(__instance.job.count, thing.stackCount), available);
            count = Math.Min(count, physical);
            if (count <= 0)
            {
                ClearPendingHaulTarget(__instance.pawn);
                __instance.pawn.ClearReservationsForJob(__instance.job);
                __result = false;
                return;
            }

            __instance.job.count = count;
            __instance.job.haulOpportunisticDuplicates = false;
            ReservationsByPawn[pawnId] = new TransferReservation
            {
                PawnId = pawnId,
                ThingId = thing.thingIDNumber,
                Prototype = thing,
                Quota = quota,
                CellKey = cellKey,
                Count = count,
                ReservesNewStack = reservesNewStack,
                QuotaManagedDestination = quota.Scope != null
                    && QuotaDataStore.Get(quota.Scope.Settings).Active,
                StartedTick = Find.TickManager?.TicksGame ?? 0,
                Arrived = false,
                FinishActionAttached = false,
                FinishDriver = null
            };
        }

        private static void ClearPendingHaulTarget(Pawn pawn)
        {
            if (pawn == null || takeHaulTarget == null)
            {
                return;
            }

            try
            {
                object[] args =
                {
                    pawn,
                    null,
                    null,
                    0,
                    IntVec3.Invalid
                };
                takeHaulTarget.Invoke(null, args);
            }
            catch (Exception exception)
            {
                LogRuntimeWarning("could not clear a cancelled Strata haul target: "
                    + exception.GetBaseException().Message);
            }
        }

        private static bool TryDeliverInventoryPrefix(Pawn __0)
        {
            Pawn pawn = __0;
            if (!InventoryHaulingCompatibility.IsPresent
                || pawn?.inventory?.innerContainer == null
                || pawn.Map == null
                || pawn.Dead
                || pawn.Downed)
            {
                return true;
            }

            try
            {
                DeliverInventoryQuotaAware(pawn);
                return false;
            }
            catch (Exception exception)
            {
                LogRuntimeWarning("quota-aware Strata inventory delivery failed; remaining cargo "
                    + "was kept in inventory instead of risking an over-quota drop. "
                    + exception.GetBaseException().Message);
                pawn.inventory.UnloadEverything = true;
                return false;
            }
        }

        private static void TryMakeStorageJobPrefix(
            Pawn __0,
            out StorageJobPatchState __state)
        {
            __state = new StorageJobPatchState
            {
                PreviousPawnId = currentCellCheckPawnId,
                PreviousStorageJobPawnId = currentStorageJobPawnId,
                PreviousQuotaRejected = quotaRejectedDuringStorageJob
            };
            currentCellCheckPawnId = __0?.thingIDNumber ?? -1;
            currentStorageJobPawnId = __0?.thingIDNumber ?? -1;
            quotaRejectedDuringStorageJob = false;
        }

        private static Exception TryMakeStorageJobFinalizer(
            Exception __exception,
            StorageJobPatchState __state)
        {
            currentCellCheckPawnId = __state.PreviousPawnId;
            currentStorageJobPawnId = __state.PreviousStorageJobPawnId;
            quotaRejectedDuringStorageJob = __state.PreviousQuotaRejected;
            return __exception;
        }

        private static void TryMakeStorageJobPostfix(
            Pawn __0,
            Thing __1,
            ref Job __result)
        {
            if (__0?.Map == null || __1 == null)
            {
                return;
            }

            bool carried = ReferenceEquals(__0.carryTracker?.CarriedThing, __1);
            bool strataStorageForCarried = carried
                && currentStorageJobPawnId == __0.thingIDNumber;
            bool arrivingWithReservation = ReservationsByPawn.TryGetValue(
                    __0.thingIDNumber,
                    out TransferReservation arrivingReservation)
                && arrivingReservation.Arrived;
            if (__result == null)
            {
                if (strataStorageForCarried && quotaRejectedDuringStorageJob)
                {
                    arrivingReservation = EnsureArrivalReservation(__0, __1);
                    arrivingReservation.QuotaRejectedFallback = true;
                }
                return;
            }

            if (!arrivingWithReservation && strataStorageForCarried)
            {
                // Static reservations are intentionally not serialized. Reconstruct an arrival
                // marker here after save/load, and also for hauls whose target was unlimited when
                // the source job began, so the final local walk remains protected.
                arrivingReservation = EnsureArrivalReservation(__0, __1);
                arrivingWithReservation = true;
            }

            LocalTargetInfo destination = __result.GetTarget(TargetIndex.B);
            IntVec3 cell = destination.HasThing
                ? destination.Thing.Position
                : destination.Cell;
            if (!cell.IsValid || !cell.InBounds(__0.Map))
            {
                return;
            }

            int remaining = QuotaUtility.RemainingForDestination(
                __1,
                cell,
                __0.Map,
                __result.count);
            int reservationAware = AvailableAt(
                __1,
                cell,
                __0.Map,
                __0.thingIDNumber,
                out QuotaKey quota,
                out _,
                out bool reservesNewStack);
            remaining = Math.Min(remaining, reservationAware);
            if (remaining == int.MaxValue)
            {
                if (carried && arrivingWithReservation)
                {
                    // Keep a destination marker even while this target is unmanaged or unlimited.
                    // The player can enable or tighten its quota during the final local walk, and
                    // the placement seam must still recheck that newly-active limit.
                    RebindArrivingReservation(
                        __0,
                        __1,
                        cell,
                        quota,
                        reservesNewStack: false,
                        arrivingReservation);
                }
                return;
            }

            int physical = ExternalStorageUtility.PhysicalCapacityAt(__1, cell, __0.Map);
            int allowed = Math.Min(Math.Min(__result.count, remaining), physical);
            if (allowed <= 0)
            {
                if (carried && (arrivingWithReservation || strataStorageForCarried))
                {
                    arrivingReservation ??= EnsureArrivalReservation(__0, __1);
                    RebindArrivingReservation(
                        __0,
                        __1,
                        cell,
                        quota,
                        reservesNewStack,
                        arrivingReservation);
                    arrivingReservation.QuotaRejectedFallback = true;
                }
                __result = null;
                return;
            }

            // At the end of a relay the cargo is already one stack in the pawn's hands.
            // Vanilla's placement toils drop that whole carried stack even when job.count is
            // smaller. If another hauler filled part of the quota while this pawn was en route,
            // refuse this destination and let Strata use its safe fallback instead of silently
            // exceeding the live quota.
            if (carried && allowed < __1.stackCount)
            {
                arrivingReservation ??= EnsureArrivalReservation(__0, __1);
                RebindArrivingReservation(
                    __0,
                    __1,
                    cell,
                    quota,
                    reservesNewStack,
                    arrivingReservation);
                arrivingReservation.QuotaRejectedFallback = true;
                __result = null;
                return;
            }

            if (carried && arrivingWithReservation)
            {
                // Preferred cells can become invalid during a multi-floor trip. Strata then finds
                // a fallback cell, so move the in-flight reservation to the job's real destination.
                RebindArrivingReservation(
                    __0,
                    __1,
                    cell,
                    quota,
                    reservesNewStack,
                    arrivingReservation);
            }

            __result.count = allowed;
            __result.haulOpportunisticDuplicates = false;
        }

        private static TransferReservation EnsureArrivalReservation(Pawn pawn, Thing thing)
        {
            int pawnId = pawn.thingIDNumber;
            if (!ReservationsByPawn.TryGetValue(
                pawnId,
                out TransferReservation reservation))
            {
                reservation = new TransferReservation
                {
                    PawnId = pawnId,
                    ThingId = thing.thingIDNumber,
                    Prototype = thing,
                    Quota = new QuotaKey { Def = thing.def },
                    Count = thing.stackCount
                };
                ReservationsByPawn[pawnId] = reservation;
            }

            reservation.Arrived = true;
            reservation.StartedTick = Find.TickManager?.TicksGame ?? 0;
            return reservation;
        }

        private static bool ReservationMatchesCarriedCargo(
            TransferReservation reservation,
            Thing carried)
        {
            return reservation != null
                && carried != null
                && !(carried is Pawn)
                && carried.def != null
                && (reservation.Quota.Def == null
                    || carried.def == reservation.Quota.Def);
        }

        private static void RebindArrivingReservation(
            Pawn pawn,
            Thing thing,
            IntVec3 cell,
            QuotaKey quota,
            bool reservesNewStack,
            TransferReservation reservation)
        {
            if (pawn?.Map == null || thing?.def == null || reservation == null)
            {
                return;
            }

            // AvailableAt returns default keys for a cell that currently has no SGQ scope. Retain
            // its ThingDef and exact location so a newly linked/activated group is protected as
            // soon as any later capacity scan or the final placement check observes it.
            quota.Def = thing.def;
            reservation.Quota = quota;
            reservation.Prototype = thing;
            reservation.CellKey = new CellKey
            {
                Quota = quota,
                MapId = pawn.Map.uniqueID,
                Cell = cell
            };
            reservation.Count = thing.stackCount;
            reservation.ReservesNewStack = reservesNewStack;
            reservation.QuotaManagedDestination = quota.Scope != null
                && QuotaDataStore.Get(quota.Scope.Settings).Active;
            reservation.StartedTick = Find.TickManager?.TicksGame ?? 0;
            reservation.QuotaRejectedFallback = false;
            reservation.FinishActionAttached = false;
            reservation.FinishDriver = null;
            reservation.RecoveryJob = null;
            reservation.NextRecoveryTick = 0;
            ReservationsByPawn[pawn.thingIDNumber] = reservation;
        }

        private static void DeliverInventoryQuotaAware(Pawn pawn)
        {
            ThingOwner<Thing> inventory = pawn.inventory.innerContainer;
            // PUAH/HD track only cargo collected for hauling. Never sweep the pawn's ordinary
            // meals, medicine, ammunition, tools, or other personal inventory into storage.
            List<Thing> items = InventoryHaulingCompatibility.TrackedHauledItemsSnapshot(pawn);
            List<Thing> quotaExcess = new List<Thing>();
            for (int i = 0; i < items.Count; i++)
            {
                Thing thing = items[i];
                if (thing == null
                    || thing.Destroyed
                    || !inventory.Contains(thing)
                    || (!thing.def.EverHaulable && !thing.def.alwaysHaulable))
                {
                    continue;
                }

                if (!StoreUtility.TryFindBestBetterStorageFor(
                        thing,
                        pawn,
                        pawn.Map,
                        StoragePriority.Unstored,
                        pawn.Faction,
                        out IntVec3 cell,
                        out IHaulDestination destination,
                        needAccurateResult: false))
                {
                    // A full quota is reported as "no better storage". Strata's original Near
                    // drop can immediately select the very shelf that rejected this item, so keep
                    // it in the hauling inventory and hand it back to the active inventory-hauling
                    // backend after the cross-level job finishes.
                    RememberQuotaExcess(inventory, quotaExcess, thing);
                    continue;
                }

                IntVec3 dropCell;
                ThingPlaceMode placeMode;
                if (destination is Building building && building.Spawned)
                {
                    dropCell = building.Position;
                    placeMode = ThingPlaceMode.Near;
                }
                else if (cell.IsValid)
                {
                    dropCell = cell;
                    placeMode = ThingPlaceMode.Direct;
                }
                else
                {
                    // Treat a malformed/vanished destination like quota excess. Near placement
                    // is intentionally avoided because it can redirect onto quota-managed storage.
                    RememberQuotaExcess(inventory, quotaExcess, thing);
                    continue;
                }

                int allowed = QuotaUtility.RemainingForDestination(
                    thing,
                    dropCell,
                    pawn.Map,
                    thing.stackCount);
                if (allowed == int.MaxValue)
                {
                    bool droppedAll = inventory.TryDrop(
                        thing,
                        dropCell,
                        pawn.Map,
                        placeMode,
                        out Thing _,
                        null);
                    CombatExtendedInventoryCompatibility.NotifyInventoryChanged(pawn);
                    if (droppedAll)
                    {
                        InventoryHaulingCompatibility.UnregisterHauledItem(pawn, thing);
                    }
                    else
                    {
                        RememberQuotaExcess(inventory, quotaExcess, thing);
                    }
                    continue;
                }

                allowed = LimitRemainingForReservations(
                    thing,
                    dropCell,
                    pawn.Map,
                    allowed);
                allowed = Math.Min(allowed, thing.stackCount);
                int physical = ExternalStorageUtility.PhysicalCapacityAt(
                    thing,
                    dropCell,
                    pawn.Map);
                if (physical >= 0)
                {
                    allowed = Math.Min(allowed, physical);
                }

                if (allowed <= 0)
                {
                    // Keep quota excess in inventory. PUAH/HD can choose another destination;
                    // dropping it at the stair landing would immediately create another haul loop.
                    RememberQuotaExcess(inventory, quotaExcess, thing);
                    continue;
                }

                // A quota is defined for the exact storage cell. Near placement could choose an
                // adjacent cell belonging to another group, so finite quota drops are always direct.
                int originalCount = thing.stackCount;
                bool dropped = inventory.TryDrop(
                    thing,
                    dropCell,
                    pawn.Map,
                    ThingPlaceMode.Direct,
                    allowed,
                    out Thing _,
                    null);
                CombatExtendedInventoryCompatibility.NotifyInventoryChanged(pawn);
                if (!dropped || allowed < originalCount)
                {
                    RememberQuotaExcess(inventory, quotaExcess, thing);
                }
                else
                {
                    InventoryHaulingCompatibility.UnregisterHauledItem(pawn, thing);
                }
            }

            if (quotaExcess.Count > 0)
            {
                RequestInventoryUnloadAfterCurrentJob(pawn, quotaExcess);
            }
        }

        private static void RememberQuotaExcess(
            ThingOwner<Thing> inventory,
            List<Thing> quotaExcess,
            Thing thing)
        {
            if (thing != null
                && !thing.Destroyed
                && inventory.Contains(thing)
                && !quotaExcess.Contains(thing))
            {
                quotaExcess.Add(thing);
            }
        }

        private static void RequestInventoryUnloadAfterCurrentJob(
            Pawn pawn,
            List<Thing> quotaExcess)
        {
            JobDriver driver = pawn?.jobs?.curDriver;
            if (driver == null)
            {
                if (QuotaExcessStillInInventory(pawn, quotaExcess))
                {
                    InventoryHaulingCompatibility.RequestUnload(pawn);
                }
                return;
            }

            driver.AddFinishAction(_ =>
            {
                if (QuotaExcessStillInInventory(pawn, quotaExcess))
                {
                    InventoryHaulingCompatibility.RequestUnload(pawn);
                }
            });
        }

        private static bool QuotaExcessStillInInventory(Pawn pawn, List<Thing> quotaExcess)
        {
            ThingOwner<Thing> inventory = pawn?.inventory?.innerContainer;
            if (inventory == null || quotaExcess == null)
            {
                return false;
            }

            for (int i = 0; i < quotaExcess.Count; i++)
            {
                Thing thing = quotaExcess[i];
                if (thing != null && !thing.Destroyed && inventory.Contains(thing))
                {
                    return true;
                }
            }

            return false;
        }

        private static int AvailableAt(
            Thing thing,
            IntVec3 cell,
            Map map,
            int excludePawnId,
            out QuotaKey quota,
            out CellKey cellKey,
            out bool reservesNewStack)
        {
            quota = default;
            cellKey = default;
            reservesNewStack = false;
            ISlotGroup scope = QuotaUtility.ScopeAt(cell, map);
            if (scope == null || thing?.def == null)
            {
                return int.MaxValue;
            }

            PruneReservations();
            quota = new QuotaKey { Scope = scope, Def = thing.def };
            cellKey = new CellKey { Quota = quota, MapId = map.uniqueID, Cell = cell };
            int totalRemaining = QuotaUtility.TotalRemainingForDestination(
                thing,
                cell,
                map,
                thing.stackCount);
            StorageQuotaData data = QuotaDataStore.Get(scope.Settings);
            if (totalRemaining == int.MaxValue && data.Mode != QuotaMode.SimilarStacks)
            {
                return int.MaxValue;
            }

            long reservedTotal = 0;
            int reservedAtCell = 0;
            HashSet<CellKey> reservedNewStackCells = new HashSet<CellKey>();
            foreach (TransferReservation reservation in ReservationsByPawn.Values)
            {
                if (reservation.PawnId == excludePawnId)
                {
                    continue;
                }

                // A shelf can be linked, unlinked, or moved to another storage group while a
                // pawn is between floors. Resolve the reservation's current scope from its cell
                // so it protects the group that will actually receive the cargo, not a stale
                // ISlotGroup object captured when the source job was created.
                Map reservationMap = FindMap(reservation.CellKey.MapId);
                if (reservationMap == null
                    || !reservation.CellKey.Cell.IsValid
                    || !reservation.CellKey.Cell.InBounds(reservationMap))
                {
                    continue;
                }

                ISlotGroup reservationScope = QuotaUtility.ScopeAt(
                    reservation.CellKey.Cell,
                    reservationMap);
                if (reservationScope == null)
                {
                    continue;
                }

                QuotaKey effectiveQuota = new QuotaKey
                {
                    Scope = reservationScope,
                    Def = reservation.Quota.Def
                };
                CellKey effectiveCell = new CellKey
                {
                    Quota = effectiveQuota,
                    MapId = reservation.CellKey.MapId,
                    Cell = reservation.CellKey.Cell
                };
                reservation.Quota = effectiveQuota;
                reservation.CellKey = effectiveCell;
                if (!effectiveQuota.Equals(quota))
                {
                    continue;
                }

                reservedTotal += reservation.Count;
                if (effectiveCell.Equals(cellKey))
                {
                    reservedAtCell += reservation.Count;
                }

                // Whether this trip consumes a stack slot can change while the pawn is between
                // floors. A formerly empty target may gain a compatible stack, or the compatible
                // stack that existed at claim time may be removed. Recompute from the live cell
                // for every reservation instead of trusting its captured one-way flag.
                Thing prototype = reservation.Prototype ?? thing;
                bool stackNowExists = effectiveCell.Cell
                    .GetThingList(reservationMap)
                    .Any(candidate => !candidate.Destroyed
                        && candidate.def == effectiveQuota.Def
                        && prototype != null
                        && candidate.CanStackWith(prototype));
                reservation.ReservesNewStack = !stackNowExists;
                if (!stackNowExists)
                {
                    reservedNewStackCells.Add(effectiveCell);
                }
            }

            int totalAvailable = totalRemaining == int.MaxValue
                ? int.MaxValue
                : (int)Math.Max(0L, (long)totalRemaining - reservedTotal);
            if (data.Mode != QuotaMode.SimilarStacks)
            {
                return totalAvailable;
            }

            int perStack = data.EffectivePerStackUpper(thing.def);
            Thing compatibleStack = cell.GetThingList(map)
                .FirstOrDefault(candidate => !candidate.Destroyed
                    && candidate.def == thing.def
                    && candidate.CanStackWith(thing));
            int cellAvailable;
            if (compatibleStack != null)
            {
                cellAvailable = Math.Max(
                    0,
                    perStack - compatibleStack.stackCount - reservedAtCell);
            }
            else
            {
                bool cellAlreadyReserved = reservedNewStackCells.Contains(cellKey);
                if (cellAlreadyReserved)
                {
                    return 0;
                }

                int occupiedOrReserved = QuotaUtility.StackCount(scope, thing.def)
                    + reservedNewStackCells.Count;
                if (occupiedOrReserved >= data.EffectiveMaxStacks(thing.def))
                {
                    return 0;
                }

                reservesNewStack = true;
                cellAvailable = Math.Max(0, perStack - reservedAtCell);
            }

            return Math.Min(totalAvailable, cellAvailable);
        }

        private static void PruneReservations()
        {
            if (ReservationsByPawn.Count == 0)
            {
                return;
            }

            int tick = Find.TickManager?.TicksGame ?? 0;
            List<int> stale = null;
            foreach (KeyValuePair<int, TransferReservation> pair in ReservationsByPawn)
            {
                TransferReservation reservation = pair.Value;
                bool expired = tick < reservation.StartedTick
                    || tick - reservation.StartedTick > ReservationLifetimeTicks;
                Pawn pawn = FindPawn(pair.Key);
                bool deliveredOrDropped = reservation.Arrived
                    && pawn != null
                    && pawn.carryTracker?.CarriedThing == null;
                if (expired
                    && !deliveredOrDropped
                    && ReservationIsStillActive(reservation, pawn))
                {
                    reservation.StartedTick = tick;
                    continue;
                }

                // Pawns are briefly despawned while EnterPortal moves them between maps.
                // A missing spawned pawn therefore is not stale by itself; the age limit and
                // relay/driver lifecycle patches are the authoritative cleanup paths.
                if (expired
                    || deliveredOrDropped
                    || (pawn != null && (pawn.Dead || pawn.Destroyed)))
                {
                    if (stale == null)
                    {
                        stale = new List<int>();
                    }

                    stale.Add(pair.Key);
                }
            }

            if (stale == null)
            {
                return;
            }

            for (int i = 0; i < stale.Count; i++)
            {
                ReservationsByPawn.Remove(stale[i]);
            }
        }

        private static bool ReservationIsStillActive(
            TransferReservation reservation,
            Pawn pawn)
        {
            if (reservation == null || pawn == null || pawn.Dead || pawn.Destroyed)
            {
                return false;
            }

            JobDriver driver = pawn.jobs?.curDriver;
            if ((haulDriverType != null && haulDriverType.IsInstanceOfType(driver))
                || (reservation.FinishDriver != null
                    && ReferenceEquals(driver, reservation.FinishDriver))
                || (reservation.Arrived && pawn.carryTracker?.CarriedThing != null))
            {
                return true;
            }

            if (portalHasIntent == null)
            {
                return false;
            }

            try
            {
                return (bool)portalHasIntent.Invoke(null, new object[] { pawn });
            }
            catch (Exception exception)
            {
                // Losing a live reservation can overfill the destination. If Strata's intent
                // lookup itself fails, retain the reservation for one more bounded interval.
                LogRuntimeWarning("could not verify an aged Strata relay reservation: "
                    + exception.GetBaseException().Message);
                return true;
            }
        }

        private static Pawn FindPawn(int pawnId)
        {
            List<Map> maps = Find.Maps;
            if (maps == null)
            {
                return null;
            }

            for (int i = 0; i < maps.Count; i++)
            {
                IReadOnlyList<Pawn> pawns = maps[i].mapPawns.AllPawnsSpawned;
                for (int j = 0; j < pawns.Count; j++)
                {
                    if (pawns[j].thingIDNumber == pawnId)
                    {
                        return pawns[j];
                    }
                }
            }

            return null;
        }

        private static Map FindMap(int mapId)
        {
            List<Map> maps = Find.Maps;
            if (maps == null)
            {
                return null;
            }

            for (int i = 0; i < maps.Count; i++)
            {
                if (maps[i].uniqueID == mapId)
                {
                    return maps[i];
                }
            }

            return null;
        }

        private static Map FindMapForSettings(StorageSettings settings)
        {
            List<Map> maps = Find.Maps;
            if (maps == null)
            {
                return null;
            }

            for (int i = 0; i < maps.Count; i++)
            {
                Map map = maps[i];
                List<SlotGroup> groups = map.haulDestinationManager?.AllGroupsListForReading;
                if (groups == null)
                {
                    continue;
                }

                for (int j = 0; j < groups.Count; j++)
                {
                    SlotGroup local = groups[j];
                    if (ReferenceEquals(local.Settings, settings)
                        || (local.StorageGroup != null
                            && ReferenceEquals(
                                ((ISlotGroup)local.StorageGroup).Settings,
                                settings)))
                    {
                        return map;
                    }
                }
            }

            return null;
        }

        private static void LogRuntimeWarning(string reason)
        {
            if (runtimeWarningLogged)
            {
                return;
            }

            runtimeWarningLogged = true;
            Log.Warning("[Storage Group Quotas] Strata compatibility was partially disabled: " + reason + ".");
        }
    }
}
