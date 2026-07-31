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
                Job job = JobMaker.MakeJob(JobDefOf.HaulToCell, thing, storageCell);
                job.count = Math.Min(Math.Min(amountToMoveOutside, thing.stackCount), capacity);
                job.haulMode = HaulMode.ToCellStorage;
                job.haulOpportunisticDuplicates = false;
                job.ignoreDesignations = true;
                return job.count > 0 ? job : null;
            }

            if (TryFindOutsideFloorCell(pawn, thing, out IntVec3 floorCell))
            {
                Job job = JobMaker.MakeJob(JobDefOf.HaulToCell, thing, floorCell);
                job.count = Math.Min(amountToMoveOutside, thing.stackCount);
                job.haulMode = HaulMode.ToCellNonStorage;
                job.haulOpportunisticDuplicates = false;
                job.ignoreDesignations = true;
                return job;
            }

            return null;
        }

        private static bool BasicChecks(Pawn pawn, Thing thing)
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
            Map map = pawn.Map;
            ISlotGroup source = QuotaUtility.ScopeForThing(thing);
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

                if (StoreUtility.TryFindBestBetterStoreCellForIn(
                    thing,
                    pawn,
                    map,
                    StoragePriority.Unstored,
                    pawn.Faction,
                    candidate,
                    out IntVec3 cell,
                    true))
                {
                    int quotaCapacity = QuotaUtility.RemainingForDestination(thing, cell, map);
                    int cellCapacity = cell.GetItemStackSpaceLeftFor(map, thing.def);
                    capacity = quotaCapacity == int.MaxValue
                        ? cellCapacity
                        : Math.Min(cellCapacity, quotaCapacity);
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
            else if (sourceIndex >= data.SimilarStackCount)
            {
                amountToMove = thing.stackCount;
            }
            else
            {
                return null;
            }

            int keepCount = Math.Min(data.SimilarStackCount, stacks.Count);
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
                if (stacks.Count >= data.SimilarStackCount)
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
            if (thing.stackCount > perStack)
            {
                return thing.stackCount - perStack;
            }

            List<Thing> stacks = scope.HeldThings
                .Where(stack => stack.Spawned && stack.def == thing.def)
                .OrderByDescending(stack => stack.stackCount)
                .ThenBy(stack => stack.thingIDNumber)
                .ToList();
            return stacks.IndexOf(thing) >= data.SimilarStackCount ? thing.stackCount : 0;
        }

        private static bool TryFindOutsideFloorCell(Pawn pawn, Thing thing, out IntVec3 cell)
        {
            Map map = pawn.Map;
            int cellsToCheck = GenRadial.NumCellsInRadius(40f);
            for (int i = 0; i < cellsToCheck; i++)
            {
                IntVec3 candidate = thing.Position + GenRadial.RadialPattern[i];
                if (!candidate.InBounds(map)
                    || candidate.GetSlotGroup(map) != null
                    || candidate == thing.Position
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
