using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace StorageGroupQuotas
{
    public sealed class WorkGiver_MoveQuotaOverflow : WorkGiver_Scanner
    {
        private const float BatchSearchRadius = 12f;
        private const int MaxBatchSources = 64;

        public override ThingRequest PotentialWorkThingRequest => ThingRequest.ForGroup(ThingRequestGroup.Undefined);

        public override bool Prioritized => true;

        public override PathEndMode PathEndMode => PathEndMode.ClosestTouch;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
            return QuotaUtility.OverflowThings(pawn.Map);
        }

        public override float GetPriority(Pawn pawn, TargetInfo target)
        {
            return target.Thing == null ? 0f : 1000f + QuotaUtility.OverflowCount(target.Thing);
        }

        public override bool HasJobOnThing(Pawn pawn, Thing thing, bool forced = false)
        {
            return JobOnThing(pawn, thing, forced) != null;
        }

        public override Job JobOnThing(Pawn pawn, Thing thing, bool forced = false)
        {
            if (!BasicChecks(pawn, thing))
            {
                return null;
            }

            int amountToMoveOutside = QuotaUtility.OverflowCount(thing);
            bool isTotalCountOverflow = amountToMoveOutside > 0;
            if (amountToMoveOutside <= 0)
            {
                Job rebalanceJob = TryMakeInternalRebalanceJob(pawn, thing);
                if (rebalanceJob != null)
                {
                    return rebalanceJob;
                }

                amountToMoveOutside = UnresolvedLayoutExcessCount(thing);
            }

            if (amountToMoveOutside <= 0)
            {
                return null;
            }

            if (TryFindStorageOutsideSource(pawn, thing, out IntVec3 storageCell, out int capacity))
            {
                if (isTotalCountOverflow)
                {
                    Job batchJob = TryMakeInventoryBatchJob(pawn, thing, amountToMoveOutside);
                    if (batchJob != null)
                    {
                        return batchJob;
                    }
                }

                Job job = JobMaker.MakeJob(JobDefOf.HaulToCell, thing, storageCell);
                job.count = Math.Min(Math.Min(amountToMoveOutside, thing.stackCount), capacity);
                job.haulMode = HaulMode.ToCellStorage;
                job.haulOpportunisticDuplicates = false;
                job.ignoreDesignations = true;
                return job.count > 0 ? job : null;
            }

            if (TryFindOutsideFloorCell(pawn, thing, out IntVec3 floorCell))
            {
                if (isTotalCountOverflow)
                {
                    Job batchJob = TryMakeInventoryBatchJob(pawn, thing, amountToMoveOutside);
                    if (batchJob != null)
                    {
                        return batchJob;
                    }
                }

                Job job = JobMaker.MakeJob(JobDefOf.HaulToCell, thing, floorCell);
                job.count = Math.Min(amountToMoveOutside, thing.stackCount);
                job.haulMode = HaulMode.ToCellNonStorage;
                job.haulOpportunisticDuplicates = false;
                job.ignoreDesignations = true;
                return job;
            }

            return null;
        }

        internal static bool BasicChecks(Pawn pawn, Thing thing)
        {
            return pawn != null
                && thing != null
                && thing.Spawned
                && thing.Map == pawn.Map
                && thing.def.EverHaulable
                && !thing.IsForbidden(pawn)
                && !thing.IsBurning()
                && pawn.CanReserveAndReach(thing, PathEndMode.ClosestTouch, pawn.NormalMaxDanger());
        }

        private static bool TryFindStorageOutsideSource(
            Pawn pawn,
            Thing thing,
            out IntVec3 foundCell,
            out int capacity)
        {
            return TryFindStorageOutsideSource(
                pawn,
                thing,
                QuotaUtility.ScopeForThing(thing),
                null,
                out foundCell,
                out capacity);
        }

        internal static bool TryFindStorageOutsideSource(
            Pawn pawn,
            Thing thing,
            ISlotGroup source,
            HashSet<IntVec3> excludedCells,
            out IntVec3 foundCell,
            out int capacity)
        {
            Map map = pawn.Map;
            HashSet<ISlotGroup> seen = new HashSet<ISlotGroup>(ReferenceComparer.Instance);

            foreach (SlotGroup local in map.haulDestinationManager.AllGroupsListInPriorityOrder)
            {
                ISlotGroup candidate = local.StorageGroup ?? (ISlotGroup)local;
                if (ReferenceEquals(candidate, source)
                    || !seen.Add(candidate)
                    || !candidate.Settings.AllowedToAccept(thing)
                    || candidate.CellsList.Count == 0)
                {
                    continue;
                }

                if ((excludedCells == null || excludedCells.Count == 0)
                    && StoreUtility.TryFindBestBetterStoreCellForIn(
                        thing,
                        pawn,
                        map,
                        StoragePriority.Unstored,
                        pawn.Faction,
                        candidate,
                        out IntVec3 bestCell,
                        true))
                {
                    capacity = DestinationCapacity(pawn, thing, bestCell, source);
                    if (capacity > 0)
                    {
                        foundCell = bestCell;
                        return true;
                    }
                }

                List<IntVec3> cells = new List<IntVec3>(candidate.CellsList);
                cells.Sort((left, right) =>
                    left.DistanceToSquared(pawn.Position).CompareTo(right.DistanceToSquared(pawn.Position)));
                foreach (IntVec3 cell in cells)
                {
                    if (excludedCells?.Contains(cell) == true)
                    {
                        continue;
                    }

                    capacity = DestinationCapacity(pawn, thing, cell, source);
                    if (capacity > 0)
                    {
                        foundCell = cell;
                        return true;
                    }
                }
            }

            foundCell = IntVec3.Invalid;
            capacity = 0;
            return false;
        }

        internal static int DestinationCapacity(
            Pawn pawn,
            Thing thing,
            IntVec3 cell,
            ISlotGroup source,
            bool alreadyReservedByPawn = false)
        {
            Map map = pawn?.Map;
            if (map == null || thing == null || !cell.InBounds(map))
            {
                return 0;
            }

            ISlotGroup destination = QuotaUtility.ScopeAt(cell, map);
            if (destination == null)
            {
                return FloorDestinationCapacity(pawn, thing, cell);
            }

            bool validCell = alreadyReservedByPawn
                ? !cell.IsForbidden(pawn)
                    && !cell.ContainsStaticFire(map)
                    && StoreUtility.IsValidStorageFor(cell, map, thing)
                : StoreUtility.IsGoodStoreCell(cell, map, thing, pawn, pawn.Faction);
            if (ReferenceEquals(destination, source)
                || !destination.Settings.AllowedToAccept(thing)
                || !validCell)
            {
                return 0;
            }

            int quotaCapacity = QuotaUtility.RemainingForDestination(thing, cell, map);
            int physicalCapacity = InventoryHaulingCompatibility.CapacityAt(thing, cell, map);
            return quotaCapacity == int.MaxValue
                ? physicalCapacity
                : Math.Min(physicalCapacity, quotaCapacity);
        }

        private static int FloorDestinationCapacity(Pawn pawn, Thing thing, IntVec3 cell)
        {
            Map map = pawn.Map;
            if (cell.GetSlotGroup(map) != null
                || cell.IsForbidden(pawn)
                || !cell.Standable(map)
                || cell.ContainsStaticFire(map)
                || GenPlace.HaulPlaceBlockerIn(thing, cell, map, true) != null)
            {
                return 0;
            }

            if (thing.def.BlocksPlanting() && map.zoneManager.ZoneAt(cell) is Zone_Growing)
            {
                return 0;
            }

            return Math.Max(0, cell.GetItemStackSpaceLeftFor(map, thing.def));
        }

        private static Job TryMakeInventoryBatchJob(
            Pawn pawn,
            Thing seed,
            int seedOverflow)
        {
            JobDef batchJobDef = DefDatabase<JobDef>.GetNamedSilentFail("SGQ_HaulQuotaOverflowBatch");
            if (batchJobDef == null
                || !InventoryHaulingCompatibility.CanUseBatchHauling(pawn, seed)
                || InventoryHaulingCompatibility.LimitCount(
                    pawn,
                    seed,
                    Math.Min(seedOverflow, seed.stackCount)) <= 0)
            {
                return null;
            }

            ISlotGroup source = QuotaUtility.ScopeForThing(seed);
            if (source == null)
            {
                return null;
            }

            List<Thing> candidates = QuotaUtility.OverflowThings(pawn.Map)
                .Where(candidate => candidate != null
                    && ReferenceEquals(QuotaUtility.ScopeForThing(candidate), source)
                    && candidate.Position.DistanceToSquared(seed.Position)
                        <= BatchSearchRadius * BatchSearchRadius)
                .OrderBy(candidate => candidate == seed ? 0 : 1)
                .ThenBy(candidate => candidate.Position.DistanceToSquared(seed.Position))
                .ThenBy(candidate => candidate.thingIDNumber)
                .Take(MaxBatchSources)
                .ToList();

            if (!candidates.Contains(seed))
            {
                candidates.Insert(0, seed);
            }

            List<LocalTargetInfo> targets = new List<LocalTargetInfo>();
            List<int> counts = new List<int>();
            foreach (Thing candidate in candidates)
            {
                if (!BasicChecks(pawn, candidate)
                    || !InventoryHaulingCompatibility.AllowsThing(pawn, candidate))
                {
                    continue;
                }

                int overflow = Math.Min(QuotaUtility.OverflowCount(candidate), candidate.stackCount);
                if (overflow <= 0)
                {
                    continue;
                }

                targets.Add(candidate);
                counts.Add(overflow);
            }

            if (targets.Count == 0 || targets[0].Thing != seed)
            {
                return null;
            }

            Job job = JobMaker.MakeJob(batchJobDef);
            job.targetQueueA = targets;
            job.countQueue = counts;
            job.SetTarget(TargetIndex.C, seed.Position);
            job.ignoreDesignations = true;
            return job;
        }

        private static Job TryMakeInternalRebalanceJob(Pawn pawn, Thing thing)
        {
            ISlotGroup scope = QuotaUtility.ScopeForThing(thing);
            if (scope == null)
            {
                return null;
            }

            StorageQuotaData data = QuotaDataStore.Get(scope.Settings);
            if (data.Mode != QuotaMode.SimilarStacks)
            {
                return null;
            }

            int perStack = data.EffectivePerStackUpper(thing.def);
            int maxStacks = data.EffectiveMaxStacks(thing.def);
            int totalUpper = data.EffectiveTotalUpper(thing.def);
            if (QuotaUtility.Count(scope, thing.def) > totalUpper)
            {
                return null;
            }

            List<Thing> stacks = new List<Thing>();
            foreach (Thing stack in scope.HeldThings)
            {
                if (stack.Spawned && stack.def == thing.def)
                {
                    stacks.Add(stack);
                }
            }

            stacks.Sort((left, right) =>
            {
                int byCount = right.stackCount.CompareTo(left.stackCount);
                return byCount != 0 ? byCount : left.thingIDNumber.CompareTo(right.thingIDNumber);
            });

            int sourceIndex = stacks.IndexOf(thing);
            if (sourceIndex < 0)
            {
                return null;
            }

            int amountToMove;
            if (thing.stackCount > perStack)
            {
                amountToMove = thing.stackCount - perStack;
            }
            else if (sourceIndex >= maxStacks)
            {
                amountToMove = thing.stackCount;
            }
            else
            {
                return null;
            }

            int keepCount = Math.Min(maxStacks, stacks.Count);
            Thing destinationStack = null;
            for (int i = 0; i < keepCount; i++)
            {
                Thing candidate = stacks[i];
                if (candidate != thing
                    && candidate.Position != thing.Position
                    && candidate.stackCount < perStack
                    && candidate.CanStackWith(thing)
                    && StoreUtility.IsGoodStoreCell(candidate.Position, pawn.Map, thing, pawn, pawn.Faction))
                {
                    if (destinationStack == null || candidate.stackCount > destinationStack.stackCount)
                    {
                        destinationStack = candidate;
                    }
                }
            }

            IntVec3 destination;
            int destinationCapacity;
            if (destinationStack != null)
            {
                destination = destinationStack.Position;
                destinationCapacity = Math.Min(
                    perStack - destinationStack.stackCount,
                    destination.GetItemStackSpaceLeftFor(pawn.Map, thing.def));
            }
            else
            {
                if (stacks.Count >= maxStacks)
                {
                    return null;
                }

                destination = IntVec3.Invalid;
                List<IntVec3> cells = new List<IntVec3>(scope.CellsList);
                cells.Sort((left, right) =>
                    left.DistanceToSquared(thing.Position).CompareTo(right.DistanceToSquared(thing.Position)));
                foreach (IntVec3 cell in cells)
                {
                    if (cell != thing.Position
                        && !cell.GetThingList(pawn.Map).Any(candidate =>
                            candidate.def == thing.def && candidate.CanStackWith(thing))
                        && StoreUtility.IsGoodStoreCell(cell, pawn.Map, thing, pawn, pawn.Faction))
                    {
                        destination = cell;
                        break;
                    }
                }

                if (!destination.IsValid)
                {
                    return null;
                }

                destinationCapacity = Math.Min(
                    perStack,
                    destination.GetItemStackSpaceLeftFor(pawn.Map, thing.def));
            }

            int count = Math.Min(Math.Min(amountToMove, destinationCapacity), thing.stackCount);
            if (count <= 0)
            {
                return null;
            }

            Job job = JobMaker.MakeJob(JobDefOf.HaulToCell, thing, destination);
            job.count = count;
            job.haulMode = HaulMode.ToCellStorage;
            job.haulOpportunisticDuplicates = false;
            job.ignoreDesignations = true;
            return job;
        }

        private static int UnresolvedLayoutExcessCount(Thing thing)
        {
            ISlotGroup scope = QuotaUtility.ScopeForThing(thing);
            if (scope == null)
            {
                return 0;
            }

            StorageQuotaData data = QuotaDataStore.Get(scope.Settings);
            if (data.Mode != QuotaMode.SimilarStacks
                || QuotaUtility.Count(scope, thing.def) > data.EffectiveTotalUpper(thing.def))
            {
                return 0;
            }

            int perStack = data.EffectivePerStackUpper(thing.def);
            int maxStacks = data.EffectiveMaxStacks(thing.def);
            if (thing.stackCount > perStack)
            {
                return thing.stackCount - perStack;
            }

            List<Thing> stacks = scope.HeldThings
                .Where(stack => stack.Spawned && stack.def == thing.def)
                .OrderByDescending(stack => stack.stackCount)
                .ThenBy(stack => stack.thingIDNumber)
                .ToList();
            return stacks.IndexOf(thing) >= maxStacks ? thing.stackCount : 0;
        }

        private static bool TryFindOutsideFloorCell(Pawn pawn, Thing thing, out IntVec3 cell)
        {
            return TryFindOutsideFloorCell(pawn, thing, thing.Position, null, out cell);
        }

        internal static bool TryFindOutsideFloorCell(
            Pawn pawn,
            Thing thing,
            IntVec3 searchCenter,
            HashSet<IntVec3> excludedCells,
            out IntVec3 cell)
        {
            Map map = pawn.Map;
            int cellsToCheck = GenRadial.NumCellsInRadius(40f);
            for (int i = 0; i < cellsToCheck; i++)
            {
                IntVec3 candidate = searchCenter + GenRadial.RadialPattern[i];
                if (!candidate.InBounds(map)
                    || excludedCells?.Contains(candidate) == true
                    || candidate.GetSlotGroup(map) != null
                    || (thing.Spawned && candidate == thing.Position)
                    || candidate.IsForbidden(pawn)
                    || !candidate.Standable(map)
                    || candidate.ContainsStaticFire(map)
                    || GenPlace.HaulPlaceBlockerIn(thing, candidate, map, true) != null
                    || !pawn.CanReserveAndReach(candidate, PathEndMode.OnCell, pawn.NormalMaxDanger()))
                {
                    continue;
                }

                if (thing.def.BlocksPlanting() && map.zoneManager.ZoneAt(candidate) is Zone_Growing)
                {
                    continue;
                }

                cell = candidate;
                return true;
            }

            cell = IntVec3.Invalid;
            return false;
        }

        private sealed class ReferenceComparer : IEqualityComparer<ISlotGroup>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();

            public bool Equals(ISlotGroup x, ISlotGroup y) => ReferenceEquals(x, y);

            public int GetHashCode(ISlotGroup obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }

}
