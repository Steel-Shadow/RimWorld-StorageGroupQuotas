using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace StorageGroupQuotas
{
    public sealed class JobDriver_HaulQuotaOverflowBatch : JobDriver
    {
        private const int MaxDestinationReservationAttempts = 128;

        private List<Thing> hauledThings = new List<Thing>();
        private HashSet<IntVec3> rejectedDestinationCells = new HashSet<IntVec3>();
        private Thing lastDestinationThing;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref hauledThings, "sgqHauledThings", LookMode.Reference);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && hauledThings == null)
            {
                hauledThings = new List<Thing>();
            }
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            if (job.targetQueueA.NullOrEmpty())
            {
                return false;
            }

            pawn.ReserveAsManyAsPossible(job.targetQueueA, job);
            return pawn.Reserve(job.targetQueueA[0], job, errorOnFailed: errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            if (hauledThings == null)
            {
                hauledThings = new List<Thing>();
            }

            rejectedDestinationCells = new HashSet<IntVec3>();
            AddFinishAction(condition =>
                InventoryHaulingCompatibility.NotifyJobFinished(pawn, hauledThings));

            Toil extractNextSource = Toils_JobTransforms.ExtractNextTargetFromQueue(
                TargetIndex.A,
                failIfCountFromQueueTooBig: false);
            Toil findDestination = new Toil();

            void ContinueWithNextSourceOrUnload()
            {
                if (!job.targetQueueA.NullOrEmpty())
                {
                    JumpToToil(extractNextSource);
                }
                else
                {
                    JumpToToil(findDestination);
                }
            }

            Toil goToSource = new Toil
            {
                initAction = () =>
                {
                    Thing sourceThing = TargetThingA;
                    if (!WorkGiver_MoveQuotaOverflow.BasicChecks(pawn, sourceThing)
                        || QuotaUtility.OverflowCount(sourceThing) <= 0
                        || InventoryHaulingCompatibility.LimitCount(pawn, sourceThing, 1) <= 0
                        || !EnsureSourceReservation(sourceThing))
                    {
                        ReleaseReservation(job.targetA);
                        ContinueWithNextSourceOrUnload();
                        return;
                    }

                    pawn.pather.StartPath(sourceThing, PathEndMode.ClosestTouch);
                },
                defaultCompleteMode = ToilCompleteMode.PatherArrival
            };

            Toil takeToInventory = new Toil
            {
                initAction = () =>
                {
                    Thing sourceThing = TargetThingA;
                    if (!WorkGiver_MoveQuotaOverflow.BasicChecks(pawn, sourceThing)
                        || !EnsureSourceReservation(sourceThing))
                    {
                        ReleaseReservation(job.targetA);
                        ContinueWithNextSourceOrUnload();
                        return;
                    }

                    int liveOverflow = QuotaUtility.OverflowCount(sourceThing);
                    int countToTake = Math.Min(Math.Min(job.count, liveOverflow), sourceThing.stackCount);
                    countToTake = InventoryHaulingCompatibility.LimitCount(
                        pawn,
                        sourceThing,
                        countToTake);
                    if (countToTake <= 0)
                    {
                        ReleaseReservation(job.targetA);
                        ContinueWithNextSourceOrUnload();
                        return;
                    }

                    if (pawn.inventory.innerContainer.GetCountCanAccept(sourceThing, false) < countToTake)
                    {
                        ReleaseReservation(job.targetA);
                        ContinueWithNextSourceOrUnload();
                        return;
                    }

                    IntVec3 sourceCell = sourceThing.Position;
                    Thing splitThing = sourceThing.SplitOff(countToTake);
                    if (!pawn.inventory.innerContainer.TryAdd(splitThing, false))
                    {
                        RecoverFailedInventoryAdd(sourceThing, splitThing, sourceCell);
                        ReleaseReservation(job.targetA);
                        ContinueWithNextSourceOrUnload();
                        return;
                    }

                    CombatExtendedInventoryCompatibility.NotifyInventoryChanged(pawn);
                    if (!InventoryHaulingCompatibility.RegisterHauledItem(pawn, splitThing))
                    {
                        RestoreToSource(splitThing, sourceCell);
                        ReleaseReservation(job.targetA);
                        ContinueWithNextSourceOrUnload();
                        return;
                    }

                    hauledThings.Add(splitThing);
                    ReleaseReservation(job.targetA);
                }
            };

            findDestination.initAction = () =>
            {
                Thing carriedThing = FirstCarriedThing();
                if (carriedThing == null)
                {
                    EndJobWith(JobCondition.Succeeded);
                    return;
                }

                if (!ReferenceEquals(lastDestinationThing, carriedThing))
                {
                    rejectedDestinationCells.Clear();
                    lastDestinationThing = carriedThing;
                }

                if (!TrySelectAndReserveDestination(carriedThing, out IntVec3 destination))
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }

                job.SetTarget(TargetIndex.A, carriedThing);
                job.SetTarget(TargetIndex.B, destination);
            };

            Toil goToDestination = new Toil
            {
                initAction = () => pawn.pather.StartPath(TargetB, PathEndMode.ClosestTouch),
                defaultCompleteMode = ToilCompleteMode.PatherArrival
            };

            Toil dropFromInventory = new Toil
            {
                initAction = () =>
                {
                    Thing carriedThing = TargetThingA;
                    IntVec3 destination = TargetB.Cell;
                    ISlotGroup source = SourceScope();
                    bool stillReserved = pawn.Map.reservationManager.ReservedBy(
                        job.targetB,
                        pawn,
                        job);
                    int capacity = source == null || !stillReserved
                        ? 0
                        : WorkGiver_MoveQuotaOverflow.DestinationCapacity(
                            pawn,
                            carriedThing,
                            destination,
                            source,
                            alreadyReservedByPawn: true);
                    if (carriedThing == null
                        || !pawn.inventory.innerContainer.Contains(carriedThing)
                        || capacity <= 0)
                    {
                        rejectedDestinationCells.Add(destination);
                        ReleaseReservation(job.targetB);
                        JumpToToil(findDestination);
                        return;
                    }

                    int countToDrop = Math.Min(carriedThing.stackCount, capacity);
                    bool dropped = pawn.inventory.innerContainer.TryDrop(
                        carriedThing,
                        destination,
                        pawn.Map,
                        ThingPlaceMode.Direct,
                        countToDrop,
                        out Thing _,
                        null,
                        null);
                    ReleaseReservation(job.targetB);
                    CombatExtendedInventoryCompatibility.NotifyInventoryChanged(pawn);

                    if (!dropped)
                    {
                        rejectedDestinationCells.Add(destination);
                    }
                    else
                    {
                        rejectedDestinationCells.Clear();
                        if (!pawn.inventory.innerContainer.Contains(carriedThing))
                        {
                            InventoryHaulingCompatibility.UnregisterHauledItem(pawn, carriedThing);
                            hauledThings.Remove(carriedThing);
                            lastDestinationThing = null;
                        }
                    }

                    JumpToToil(findDestination);
                }
            };

            yield return extractNextSource;
            yield return goToSource;
            yield return takeToInventory;
            yield return Toils_Jump.JumpIf(extractNextSource, () => !job.targetQueueA.NullOrEmpty());
            yield return findDestination;
            yield return goToDestination;
            yield return dropFromInventory;
            yield return Toils_Jump.Jump(findDestination);
        }

        private Thing FirstCarriedThing()
        {
            while (hauledThings.Count > 0)
            {
                Thing thing = hauledThings[0];
                if (thing != null && pawn.inventory.innerContainer.Contains(thing))
                {
                    return thing;
                }

                InventoryHaulingCompatibility.UnregisterHauledItem(pawn, thing);
                hauledThings.RemoveAt(0);
            }

            return null;
        }

        private bool TrySelectAndReserveDestination(Thing thing, out IntVec3 destination)
        {
            ISlotGroup source = SourceScope();
            if (source == null)
            {
                destination = IntVec3.Invalid;
                return false;
            }

            for (int attempt = 0; attempt < MaxDestinationReservationAttempts; attempt++)
            {
                if (!WorkGiver_MoveQuotaOverflow.TryFindStorageOutsideSource(
                    pawn,
                    thing,
                    source,
                    rejectedDestinationCells,
                    out destination,
                    out int _))
                {
                    break;
                }

                if (pawn.Map.reservationManager.Reserve(pawn, job, destination))
                {
                    return true;
                }

                rejectedDestinationCells.Add(destination);
            }

            for (int attempt = 0; attempt < MaxDestinationReservationAttempts; attempt++)
            {
                if (!WorkGiver_MoveQuotaOverflow.TryFindOutsideFloorCell(
                    pawn,
                    thing,
                    pawn.Position,
                    rejectedDestinationCells,
                    out destination))
                {
                    break;
                }

                if (pawn.Map.reservationManager.Reserve(pawn, job, destination))
                {
                    return true;
                }

                rejectedDestinationCells.Add(destination);
            }

            destination = IntVec3.Invalid;
            return false;
        }

        private ISlotGroup SourceScope()
        {
            IntVec3 sourceCell = job.targetC.Cell;
            return job.targetC.IsValid && sourceCell.InBounds(pawn.Map)
                ? QuotaUtility.ScopeAt(sourceCell, pawn.Map)
                : null;
        }

        private void RestoreToSource(Thing thing, IntVec3 sourceCell)
        {
            int count = thing.stackCount;
            bool dropped = pawn.inventory.innerContainer.TryDrop(
                thing,
                sourceCell,
                pawn.Map,
                ThingPlaceMode.Direct,
                count,
                out Thing _,
                null,
                null);
            if (!dropped)
            {
                dropped = pawn.inventory.innerContainer.TryDrop(
                    thing,
                    pawn.Position,
                    pawn.Map,
                    ThingPlaceMode.Near,
                    count,
                    out Thing _,
                    null,
                    null);
            }

            CombatExtendedInventoryCompatibility.NotifyInventoryChanged(pawn);
            if (dropped || !pawn.inventory.innerContainer.Contains(thing))
            {
                InventoryHaulingCompatibility.UnregisterHauledItem(pawn, thing);
                return;
            }

            if (!InventoryHaulingCompatibility.RegisterHauledItem(pawn, thing))
            {
                pawn.inventory.UnloadEverything = true;
            }
            if (!hauledThings.Contains(thing))
            {
                hauledThings.Add(thing);
            }
        }

        private void ReleaseReservation(LocalTargetInfo target)
        {
            if (target.IsValid
                && pawn.Map.reservationManager.ReservedBy(target, pawn, job))
            {
                pawn.Map.reservationManager.Release(target, pawn, job);
            }
        }

        private bool EnsureSourceReservation(Thing sourceThing)
        {
            LocalTargetInfo target = sourceThing;
            return pawn.Map.reservationManager.ReservedBy(target, pawn, job)
                || pawn.Map.reservationManager.Reserve(
                    pawn,
                    job,
                    target,
                    errorOnFailed: false);
        }

        private void RecoverFailedInventoryAdd(
            Thing sourceThing,
            Thing splitThing,
            IntVec3 sourceCell)
        {
            if (!ReferenceEquals(sourceThing, splitThing)
                && sourceThing.Spawned
                && sourceThing.CanStackWith(splitThing)
                && sourceThing.TryAbsorbStack(splitThing, false))
            {
                return;
            }

            if (GenPlace.TryPlaceThing(splitThing, sourceCell, pawn.Map, ThingPlaceMode.Direct)
                || GenPlace.TryPlaceThing(splitThing, sourceCell, pawn.Map, ThingPlaceMode.Near))
            {
                return;
            }

            Log.Error("[Storage Group Quotas] Could not return a failed inventory pickup to its source; "
                + "spawning it at the hauler as a last-resort no-loss recovery.");
            GenSpawn.Spawn(splitThing, pawn.Position, pawn.Map, WipeMode.VanishOrMoveAside);
        }
    }
}
