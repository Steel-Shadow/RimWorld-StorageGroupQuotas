using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using RimWorld;
using Verse;

namespace StorageGroupQuotas
{
    internal static class QuotaUtility
    {
        private sealed class OverflowSnapshot
        {
            internal int Tick;
            internal int Version;
            internal List<Thing> Things;
        }

        private static readonly Dictionary<Map, OverflowSnapshot> Snapshots =
            new Dictionary<Map, OverflowSnapshot>();

        private static int version;

        internal static ISlotGroup ScopeAt(IntVec3 cell, Map map)
        {
            SlotGroup local = cell.GetSlotGroup(map);
            return local == null ? null : (ISlotGroup)(local.StorageGroup ?? (ISlotGroup)local);
        }

        internal static ISlotGroup ScopeForSettings(StorageSettings settings)
        {
            if (settings?.owner is StorageGroup group)
            {
                return group;
            }

            if (settings?.owner is ISlotGroupParent parent)
            {
                SlotGroup local = parent.GetSlotGroup();
                return local == null ? null : (ISlotGroup)(local.StorageGroup ?? (ISlotGroup)local);
            }

            return null;
        }

        internal static ISlotGroup ScopeForThing(Thing thing)
        {
            return thing != null && thing.Spawned ? ScopeAt(thing.Position, thing.Map) : null;
        }

        internal static int Count(ISlotGroup scope, ThingDef def)
        {
            if (scope == null || def == null)
            {
                return 0;
            }

            long total = 0;
            foreach (Thing thing in scope.HeldThings)
            {
                if (thing.def == def)
                {
                    total += thing.stackCount;
                    if (total >= int.MaxValue)
                    {
                        return int.MaxValue;
                    }
                }
            }

            return (int)total;
        }

        internal static int StackCount(ISlotGroup scope, ThingDef def)
        {
            return scope?.HeldThings.Count(thing => thing.Spawned && thing.def == def) ?? 0;
        }

        internal static int RemainingForDestination(Thing incoming, IntVec3 cell, Map map)
        {
            ISlotGroup destination = ScopeAt(cell, map);
            if (destination == null)
            {
                return int.MaxValue;
            }

            StorageQuotaData data = QuotaDataStore.Get(destination.Settings);
            int upper = data.EffectiveTotalUpper(incoming.def);
            int current = Count(destination, incoming.def);
            ISlotGroup source = ScopeForThing(incoming);
            if (ReferenceEquals(source, destination))
            {
                current = Math.Max(0, current - incoming.stackCount);
            }

            int totalRemaining = upper == int.MaxValue
                ? int.MaxValue
                : Math.Max(0, upper - current);
            if (data.Mode != QuotaMode.SimilarStacks)
            {
                return totalRemaining;
            }

            int perStack = data.EffectivePerStackUpper(incoming.def);
            int maxStacks = data.EffectiveMaxStacks(incoming.def);
            List<Thing> sameDefInCell = cell.GetThingList(map)
                .Where(thing => !thing.Destroyed && thing.def == incoming.def)
                .ToList();
            Thing compatibleStack = sameDefInCell.FirstOrDefault(thing => thing.CanStackWith(incoming));
            int cellRemaining;
            if (compatibleStack != null)
            {
                cellRemaining = Math.Max(0, perStack - compatibleStack.stackCount);
            }
            else
            {
                int stacks = StackCount(destination, incoming.def);
                cellRemaining = stacks < maxStacks ? perStack : 0;
            }

            return Math.Min(totalRemaining, cellRemaining);
        }

        internal static int OverflowCount(Thing thing)
        {
            ISlotGroup scope = ScopeForThing(thing);
            if (scope == null)
            {
                return 0;
            }

            int upper = QuotaDataStore.Get(scope.Settings).EffectiveTotalUpper(thing.def);
            if (upper == int.MaxValue)
            {
                return 0;
            }

            List<Thing> stacks = scope.HeldThings
                .Where(t => t.Spawned && t.def == thing.def)
                .OrderByDescending(t => t.stackCount)
                .ThenBy(t => t.thingIDNumber)
                .ToList();

            int keep = upper;
            foreach (Thing stack in stacks)
            {
                int keptHere = Math.Min(keep, stack.stackCount);
                if (ReferenceEquals(stack, thing))
                {
                    return stack.stackCount - keptHere;
                }

                keep -= keptHere;
            }

            return 0;
        }

        internal static IEnumerable<Thing> OverflowThings(Map map)
        {
            int tick = Find.TickManager?.TicksGame ?? 0;
            if (Snapshots.TryGetValue(map, out OverflowSnapshot snapshot)
                && snapshot.Version == version
                && tick - snapshot.Tick < 30)
            {
                return snapshot.Things;
            }

            List<Thing> result = BuildQuotaWorkThings(map);
            Snapshots[map] = new OverflowSnapshot
            {
                Tick = tick,
                Version = version,
                Things = result
            };
            return result;
        }

        private static List<Thing> BuildQuotaWorkThings(Map map)
        {
            List<Thing> result = new List<Thing>();
            HashSet<ISlotGroup> seen = new HashSet<ISlotGroup>(ReferenceEqualityComparer<ISlotGroup>.Instance);

            foreach (SlotGroup local in map.haulDestinationManager.AllGroupsListInPriorityOrder)
            {
                ISlotGroup scope = local.StorageGroup ?? (ISlotGroup)local;
                if (!seen.Add(scope))
                {
                    continue;
                }

                StorageQuotaData data = QuotaDataStore.Get(scope.Settings);
                if (!data.Active)
                {
                    continue;
                }

                foreach (IGrouping<ThingDef, Thing> defStacks in scope.HeldThings
                    .Where(t => t.Spawned)
                    .GroupBy(t => t.def))
                {
                    int upper = data.EffectiveTotalUpper(defStacks.Key);
                    List<Thing> orderedStacks = defStacks
                        .OrderByDescending(t => t.stackCount)
                        .ThenBy(t => t.thingIDNumber)
                        .ToList();
                    if (upper != int.MaxValue)
                    {
                        int keep = upper;
                        foreach (Thing stack in orderedStacks)
                        {
                            int keptHere = Math.Min(keep, stack.stackCount);
                            if (stack.stackCount > keptHere)
                            {
                                result.Add(stack);
                            }

                            keep -= keptHere;
                        }
                    }

                    if (data.Mode == QuotaMode.SimilarStacks)
                    {
                        int perStack = data.EffectivePerStackUpper(defStacks.Key);
                        int maxStacks = data.EffectiveMaxStacks(defStacks.Key);
                        for (int i = 0; i < orderedStacks.Count; i++)
                        {
                            Thing stack = orderedStacks[i];
                            if ((stack.stackCount > perStack || i >= maxStacks)
                                && !result.Contains(stack))
                            {
                                result.Add(stack);
                            }
                        }
                    }
                }
            }

            return result;
        }

        internal static void NotifySettingsChanged(StorageSettings settings)
        {
            version++;
            settings?.owner?.Notify_SettingsChanged();
        }

        private sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
        {
            internal static readonly ReferenceEqualityComparer<T> Instance = new ReferenceEqualityComparer<T>();

            public bool Equals(T x, T y) => ReferenceEquals(x, y);

            public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
