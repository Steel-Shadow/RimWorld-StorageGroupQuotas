using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace StorageGroupQuotas
{
    internal static class ExternalStorageUtility
    {
        private const string DigitalBufferTypeName =
            "DigitalStorage.Components.Building_BufferWarehouse";
        private const string DigitalBufferDefName = "DigitalStorage_BufferWarehouse";

        internal static bool IsExternallyManaged(ISlotGroup scope)
        {
            if (scope is SlotGroup local)
            {
                return IsExternallyManaged(local);
            }

            if (scope is StorageGroup storageGroup)
            {
                return IsExternallyManaged(storageGroup);
            }

            return false;
        }

        internal static bool IsExternallyManaged(SlotGroup local)
        {
            return local != null && IsExternallyManaged(local.parent);
        }

        internal static bool IsExternallyManaged(StorageGroup storageGroup)
        {
            if (storageGroup?.members == null)
            {
                return false;
            }

            foreach (object member in storageGroup.members)
            {
                if (member is ISlotGroupParent parent && IsExternallyManaged(parent))
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool IsExternallyManaged(ISlotGroupParent parent)
        {
            if (parent == null)
            {
                return false;
            }

            if (parent.GetType().FullName == DigitalBufferTypeName)
            {
                return true;
            }

            return parent is Thing thing && thing.def?.defName == DigitalBufferDefName;
        }

        internal static int PhysicalCapacityAt(Thing incoming, IntVec3 cell, Map map)
        {
            if (incoming?.def == null
                || map == null
                || !cell.InBounds(map)
                || !StoreUtility.IsValidStorageFor(cell, map, incoming))
            {
                return 0;
            }

            int exactCapacity = ExactStackCapacityAt(incoming, cell, map);

            // PUAH's helper can carry additional storage-mod rules. It is def-based in vanilla,
            // so the exact CanStackWith calculation above remains the stricter upper bound.
            int externalCapacity = InventoryHaulingCompatibility.CapacityAt(incoming, cell, map);
            return Math.Min(exactCapacity, externalCapacity);
        }

        internal static int ExactStackCapacityAt(Thing incoming, IntVec3 cell, Map map)
        {
            if (incoming?.def == null || map == null || !cell.InBounds(map))
            {
                return 0;
            }

            int occupiedItemSlots = 0;
            long compatibleSpace = 0;
            List<Thing> things = cell.GetThingList(map);
            for (int i = 0; i < things.Count; i++)
            {
                Thing thing = things[i];
                if (thing.def.category == ThingCategory.Item)
                {
                    occupiedItemSlots++;
                }

                if (thing.CanStackWith(incoming))
                {
                    compatibleSpace += Math.Max(0, thing.def.stackLimit - thing.stackCount);
                }
            }

            int emptySlots = Math.Max(0, cell.GetMaxItemsAllowedInCell(map) - occupiedItemSlots);
            compatibleSpace += (long)emptySlots * Math.Max(1, incoming.def.stackLimit);
            int exactCapacity = compatibleSpace >= int.MaxValue
                ? int.MaxValue
                : (int)compatibleSpace;
            return exactCapacity;
        }
    }

    /// <summary>
    /// Digital Storage 3.0 directly moves things between its ledger and physical storage. Those
    /// routes deliberately bypass vanilla hauling, so SGQ wraps only the mod's selected direct
    /// placement and returns any part beyond the live quota to the same ledger.
    /// </summary>
    internal static class DigitalStorageCompatibility
    {
        private const string ItemRouterTypeName = "DigitalStorage.Services.ItemRouter";
        private const string CoreTypeName = "DigitalStorage.Components.Building_StorageCore";

        private enum RouteKind
        {
            Ground,
            Core
        }

        private sealed class RouteContext
        {
            internal RouteKind Kind;
            internal object Ledger;
            internal int CoreCapacity;
            internal Map Map;
            internal bool Armed;
            internal IntVec3 Destination;
        }

        [ThreadStatic]
        private static RouteContext currentRoute;

        private static PropertyInfo ledgerProperty;
        private static MethodInfo getCapacity;
        private static MethodInfo ledgerIngest;
        private static bool runtimeDisabled;
        private static bool warningLogged;

        internal static void Apply(Harmony harmony)
        {
            Type itemRouterType = AccessTools.TypeByName(ItemRouterTypeName);
            Type coreType = AccessTools.TypeByName(CoreTypeName);
            if (itemRouterType == null || coreType == null)
            {
                return;
            }

            MethodInfo routeGround = AccessTools.Method(itemRouterType, "RouteGroundItem", new[]
            {
                typeof(Thing), typeof(Map), coreType, typeof(int)
            });
            MethodInfo routeCore = AccessTools.Method(itemRouterType, "RouteCoreToStorage", new[]
            {
                coreType, typeof(Map)
            });
            MethodInfo canCellAccept = AccessTools.Method(itemRouterType, "CanCellAcceptItem", new[]
            {
                typeof(IntVec3), typeof(Map), typeof(Thing)
            });
            MethodInfo findBestStorage = AccessTools.Method(itemRouterType, "FindBestStorageFor", new[]
            {
                typeof(Thing), typeof(Map), typeof(StoragePriority)
            });
            MethodInfo findCellInGroup = AccessTools.Method(itemRouterType, "FindCellInGroup", new[]
            {
                typeof(SlotGroup), typeof(Map), typeof(Thing)
            });
            MethodInfo directPlacement = typeof(GenPlace).GetMethods(BindingFlags.Static | BindingFlags.Public)
                .FirstOrDefault(method =>
                {
                    if (method.Name != nameof(GenPlace.TryPlaceThing) || method.ReturnType != typeof(bool))
                    {
                        return false;
                    }

                    ParameterInfo[] parameters = method.GetParameters();
                    return parameters.Length == 8
                        && parameters[0].ParameterType == typeof(Thing)
                        && parameters[1].ParameterType == typeof(IntVec3)
                        && parameters[2].ParameterType == typeof(Map)
                        && parameters[3].ParameterType == typeof(ThingPlaceMode);
                });

            ledgerProperty = AccessTools.Property(coreType, "Ledger");
            getCapacity = AccessTools.Method(coreType, "GetCapacity", Type.EmptyTypes);
            Type ledgerType = ledgerProperty?.PropertyType;
            ledgerIngest = ledgerType == null
                ? null
                : AccessTools.Method(ledgerType, "Ingest", new[] { typeof(Thing), typeof(int) });

            if (routeGround == null
                || routeCore == null
                || canCellAccept == null
                || findBestStorage == null
                || findCellInGroup == null
                || directPlacement == null
                || ledgerProperty == null
                || getCapacity == null
                || ledgerIngest == null)
            {
                Log.Warning("[Storage Group Quotas] Digital Storage was found, but its direct-routing API "
                    + "does not match the supported 3.0 layout. SGQ will leave Digital Storage routes unchanged.");
                return;
            }

            HarmonyMethod finalizer = new HarmonyMethod(
                typeof(DigitalStorageCompatibility), nameof(RouteFinalizer));
            harmony.Patch(
                routeGround,
                prefix: new HarmonyMethod(typeof(DigitalStorageCompatibility), nameof(RouteGroundPrefix)),
                finalizer: finalizer);
            harmony.Patch(
                routeCore,
                prefix: new HarmonyMethod(typeof(DigitalStorageCompatibility), nameof(RouteCorePrefix)),
                finalizer: finalizer);
            harmony.Patch(
                canCellAccept,
                postfix: new HarmonyMethod(typeof(DigitalStorageCompatibility), nameof(CanCellAcceptPostfix)));
            harmony.Patch(
                findBestStorage,
                postfix: new HarmonyMethod(typeof(DigitalStorageCompatibility), nameof(FindBestStoragePostfix)));
            harmony.Patch(
                findCellInGroup,
                postfix: new HarmonyMethod(typeof(DigitalStorageCompatibility), nameof(FindCellInGroupPostfix)));
            harmony.Patch(
                directPlacement,
                prefix: new HarmonyMethod(typeof(DigitalStorageCompatibility), nameof(DirectPlacementPrefix)));

            Log.Message("[Storage Group Quotas] Digital Storage 3.0 quota-aware direct routing enabled.");
        }

        private static void RouteGroundPrefix(object[] __args)
        {
            BeginRoute(RouteKind.Ground, __args);
        }

        private static void RouteCorePrefix(object[] __args)
        {
            BeginRoute(RouteKind.Core, __args);
        }

        private static void BeginRoute(RouteKind kind, object[] arguments)
        {
            int coreIndex = kind == RouteKind.Ground ? 2 : 0;
            if (runtimeDisabled || arguments == null || arguments.Length <= coreIndex)
            {
                currentRoute = null;
                return;
            }

            try
            {
                object core = arguments[coreIndex];
                Map map = arguments.Length > 1 ? arguments[1] as Map : null;
                object ledger = core == null ? null : ledgerProperty.GetValue(core, null);
                if (ledger == null || map == null)
                {
                    currentRoute = null;
                    return;
                }

                currentRoute = new RouteContext
                {
                    Kind = kind,
                    Ledger = ledger,
                    CoreCapacity = Math.Max(0, (int)getCapacity.Invoke(core, null)),
                    Map = map,
                    Destination = IntVec3.Invalid
                };
            }
            catch (Exception exception)
            {
                Disable(exception);
                currentRoute = null;
            }
        }

        private static Exception RouteFinalizer(Exception __exception)
        {
            currentRoute = null;
            return __exception;
        }

        private static void CanCellAcceptPostfix(IntVec3 __0, Map __1, Thing __2, ref bool __result)
        {
            if (__result && QuotaUtility.RemainingForDestination(__2, __0, __1) <= 0)
            {
                __result = false;
            }
        }

        private static void FindBestStoragePostfix(Thing __0, ref IntVec3? __result)
        {
            RouteContext context = currentRoute;
            if (context == null || context.Kind != RouteKind.Ground || !__result.HasValue)
            {
                return;
            }

            ArmIfQuotaManaged(context, __result.Value, __0);
        }

        private static void FindCellInGroupPostfix(
            SlotGroup __0,
            Map __1,
            Thing __2,
            ref IntVec3 __result)
        {
            RouteContext context = currentRoute;
            if (context == null || context.Kind != RouteKind.Core || __0 == null || __2 == null)
            {
                return;
            }

            if (__result.IsValid)
            {
                int remaining = QuotaUtility.RemainingForDestination(__2, __result, __1);
                if (remaining == int.MaxValue)
                {
                    context.Armed = false;
                    return;
                }

                if (remaining > 0)
                {
                    Arm(context, __result);
                    return;
                }
            }

            foreach (IntVec3 cell in __0.CellsList)
            {
                int remaining = QuotaUtility.RemainingForDestination(__2, cell, __1);
                if (remaining == int.MaxValue || remaining <= 0)
                {
                    continue;
                }

                if (__1.reservationManager.IsReservedByAnyoneOf(cell, Faction.OfPlayer)
                    || ExternalStorageUtility.PhysicalCapacityAt(__2, cell, __1) <= 0)
                {
                    continue;
                }

                __result = cell;
                Arm(context, cell);
                return;
            }

            context.Armed = false;
            __result = IntVec3.Invalid;
        }

        private static bool DirectPlacementPrefix(
            Thing __0,
            IntVec3 __1,
            Map __2,
            ref bool __result)
        {
            RouteContext context = currentRoute;
            if (runtimeDisabled
                || context == null
                || !context.Armed
                || context.Map != __2
                || context.Destination != __1)
            {
                return true;
            }

            // Consume the arm before doing any work. RouteGroundItem uses the same GenPlace overload
            // once more to restore a failed target placement to its source cell.
            context.Armed = false;

            Thing excess = null;
            try
            {
                int remaining = QuotaUtility.RemainingForDestination(__0, __1, __2);
                int physical = ExternalStorageUtility.PhysicalCapacityAt(__0, __1, __2);
                int allowed = Math.Min(__0.stackCount, Math.Min(remaining, physical));
                if (allowed <= 0)
                {
                    __result = false;
                    return false;
                }

                if (allowed >= __0.stackCount)
                {
                    return true;
                }

                excess = __0.SplitOff(__0.stackCount - allowed);
                if (excess == null)
                {
                    __result = false;
                    return false;
                }

                bool returned = (bool)ledgerIngest.Invoke(
                    context.Ledger,
                    new object[] { excess, context.CoreCapacity });
                if (!returned)
                {
                    RestoreSplit(__0, excess);
                    __result = false;
                    return false;
                }

                return true;
            }
            catch (Exception exception)
            {
                RestoreSplit(__0, excess);
                Disable(exception);
                __result = false;
                return false;
            }
        }

        private static void ArmIfQuotaManaged(RouteContext context, IntVec3 destination, Thing thing)
        {
            int remaining = QuotaUtility.RemainingForDestination(thing, destination, context.Map);
            if (remaining != int.MaxValue && remaining > 0)
            {
                Arm(context, destination);
            }
            else
            {
                context.Armed = false;
            }
        }

        private static void Arm(RouteContext context, IntVec3 destination)
        {
            context.Armed = true;
            context.Destination = destination;
        }

        private static void RestoreSplit(Thing kept, Thing excess)
        {
            if (kept == null || excess == null || excess.Destroyed)
            {
                return;
            }

            int count = excess.stackCount;
            kept.stackCount += count;
            excess.Destroy(DestroyMode.Vanish);
        }

        private static void Disable(Exception exception)
        {
            runtimeDisabled = true;
            if (warningLogged)
            {
                return;
            }

            warningLogged = true;
            Log.Warning("[Storage Group Quotas] Digital Storage compatibility failed at runtime; "
                + "its direct routes are no longer adjusted for this session. "
                + exception.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Manipulator Beam chooses valid vanilla storage but normally transports the whole source
    /// stack. Clamp ordinary transfers when the destination is claimed, reserve both their
    /// linked-group amount and any new physical stack slot, and revalidate the actual carried
    /// thing at arrival. Producer unloads expose their product only after lift, so those are
    /// guarded at arrival rather than reserving against the harvester/barrel building.
    /// </summary>
    internal static class ManipulatorBeamCompatibility
    {
        private const string UtilityTypeName = "ManipulatorBeam.BeamManipulatorUtility";
        private const string ClaimUtilityTypeName = "ManipulatorBeam.BeamClaimUtility";
        private const string TransferTypeName = "ManipulatorBeam.BeamTransfer";
        private const string AutoBuildingTypeName = "ManipulatorBeam.Building_BeamManipulatorAuto";

        private struct ReservationKey : IEquatable<ReservationKey>
        {
            internal ISlotGroup Scope;
            internal ThingDef Def;

            public bool Equals(ReservationKey other)
            {
                return ReferenceEquals(Scope, other.Scope) && ReferenceEquals(Def, other.Def);
            }

            public override bool Equals(object obj)
            {
                return obj is ReservationKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return RuntimeHelpers.GetHashCode(Scope) * 397
                        ^ RuntimeHelpers.GetHashCode(Def);
                }
            }
        }

        private sealed class Reservation
        {
            internal ReservationKey Key;
            internal int Count;
            internal Map Map;
            internal IntVec3 Destination;
            internal bool ReservesStackSlot;
        }

        private sealed class TransferTracking
        {
            internal Map Map;
            internal int OwnerKey;
            internal Reservation Reservation;
        }

        private sealed class QuotaBudget
        {
            internal ReservationKey Key;
            internal Map Map;
            internal int TotalRemaining;
            internal int CellRemaining;
            internal int CurrentStacks;
            internal int MaxStacks;
            internal bool NeedsStackSlot;
        }

        private sealed class PendingClaim
        {
            internal Map Map;
            internal int OwnerKey;
            internal Reservation Reservation;
            internal int OriginalCount;
            internal bool CountChanged;
        }

        private sealed class PendingRetarget
        {
            internal TransferTracking Tracking;
            internal Reservation NewReservation;
        }

        private sealed class FinishTransferState
        {
            internal object Transfer;
            internal IntVec3 Destination;
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();

            public new bool Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return RuntimeHelpers.GetHashCode(obj);
            }
        }

        private static FieldInfo transferThing;
        private static FieldInfo transferDestination;
        private static FieldInfo transferContainer;
        private static FieldInfo transferCount;
        private static FieldInfo transferBioferriteUnload;
        private static FieldInfo transferFermentingUnload;
        private static MethodInfo releaseClaimMethod;
        private static MethodInfo resetAutoRuntimeMethod;
        private static Type autoBuildingRuntimeType;
        private static readonly Dictionary<ReservationKey, long> ReservedByKey =
            new Dictionary<ReservationKey, long>();
        private static readonly Dictionary<ReservationKey, int> ReservedStackSlotsByKey =
            new Dictionary<ReservationKey, int>();
        private static readonly Dictionary<object, TransferTracking> TrackingByTransfer =
            new Dictionary<object, TransferTracking>(ReferenceComparer.Instance);
        private static bool warningLogged;

        internal static void Apply(Harmony harmony)
        {
            Type utilityType = AccessTools.TypeByName(UtilityTypeName);
            Type claimUtilityType = AccessTools.TypeByName(ClaimUtilityTypeName);
            Type transferType = AccessTools.TypeByName(TransferTypeName);
            Type autoBuildingType = AccessTools.TypeByName(AutoBuildingTypeName);
            if (utilityType == null
                || claimUtilityType == null
                || transferType == null
                || autoBuildingType == null)
            {
                return;
            }

            MethodInfo claimStorage = AccessTools.Method(
                claimUtilityType,
                "TryClaimStorageDestination",
                new[] { transferType, typeof(int) });
            MethodInfo retargetStorage = AccessTools.Method(
                claimUtilityType,
                "TryRetargetStorageDestination",
                new[] { transferType, typeof(IntVec3), typeof(int) });
            MethodInfo releaseClaim = AccessTools.Method(
                claimUtilityType,
                "ReleaseClaim",
                new[] { transferType, typeof(int) });
            MethodInfo finishPawn = AccessTools.Method(utilityType, "FinishTransfer", new[]
            {
                typeof(Pawn), typeof(Thing), transferType, typeof(IntVec3)
            });
            MethodInfo finishAuto = AccessTools.Method(utilityType, "FinishTransferAuto", new[]
            {
                autoBuildingType, typeof(Thing), transferType, typeof(IntVec3)
            });
            MethodInfo cellAllowed = AccessTools.Method(utilityType, "IsBeamStorageCellAllowed", new[]
            {
                typeof(Map), typeof(IntVec3), typeof(Thing)
            });

            transferThing = AccessTools.Field(transferType, "thing");
            transferDestination = AccessTools.Field(transferType, "destination");
            transferContainer = AccessTools.Field(transferType, "destinationContainer");
            transferCount = AccessTools.Field(transferType, "count");
            transferBioferriteUnload = AccessTools.Field(
                transferType,
                "isBioferriteHarvesterUnload");
            transferFermentingUnload = AccessTools.Field(
                transferType,
                "isFermentingBarrelUnload");
            // The auto emitter has no native destruction cleanup. Patch the implemented base
            // method it actually dispatches to; the prefix immediately type-checks the instance,
            // so every other Building takes only one cheap branch.
            MethodInfo autoDestroy = AccessTools.DeclaredMethod(
                typeof(Building),
                nameof(Thing.Destroy),
                new[] { typeof(DestroyMode) });
            MethodInfo makeMinified = AccessTools.Method(
                typeof(MinifyUtility),
                nameof(MinifyUtility.MakeMinified),
                new[] { typeof(Thing), typeof(DestroyMode) });
            MethodInfo resetAutoRuntime = AccessTools.Method(
                autoBuildingType,
                "ResetAutoRuntime",
                new[] { typeof(bool) });
            if (claimStorage == null
                || retargetStorage == null
                || releaseClaim == null
                || finishPawn == null
                || finishAuto == null
                || cellAllowed == null
                || transferThing == null
                || transferDestination == null
                || transferContainer == null
                || transferCount == null
                || transferBioferriteUnload == null
                || transferFermentingUnload == null
                || autoDestroy == null
                || makeMinified == null
                || resetAutoRuntime == null)
            {
                Log.Warning("[Storage Group Quotas] Manipulator Beam Emitter was found, but its "
                    + "transfer API does not match the supported version. Beam hauling is unchanged.");
                return;
            }

            releaseClaimMethod = releaseClaim;
            resetAutoRuntimeMethod = resetAutoRuntime;
            autoBuildingRuntimeType = autoBuildingType;

            harmony.Patch(
                claimStorage,
                prefix: new HarmonyMethod(
                    typeof(ManipulatorBeamCompatibility), nameof(ClaimStoragePrefix)),
                postfix: new HarmonyMethod(
                    typeof(ManipulatorBeamCompatibility), nameof(ClaimStoragePostfix)));
            harmony.Patch(
                retargetStorage,
                prefix: new HarmonyMethod(
                    typeof(ManipulatorBeamCompatibility), nameof(RetargetStoragePrefix)),
                postfix: new HarmonyMethod(
                    typeof(ManipulatorBeamCompatibility), nameof(RetargetStoragePostfix)));
            harmony.Patch(
                releaseClaim,
                postfix: new HarmonyMethod(
                    typeof(ManipulatorBeamCompatibility), nameof(ReleaseClaimPostfix)));
            HarmonyMethod finishPrefix = new HarmonyMethod(
                typeof(ManipulatorBeamCompatibility), nameof(FinishTransferPrefix));
            HarmonyMethod finishFinalizer = new HarmonyMethod(
                typeof(ManipulatorBeamCompatibility), nameof(FinishTransferFinalizer));
            harmony.Patch(finishPawn, prefix: finishPrefix, finalizer: finishFinalizer);
            harmony.Patch(finishAuto, prefix: finishPrefix, finalizer: finishFinalizer);
            harmony.Patch(
                cellAllowed,
                postfix: new HarmonyMethod(
                    typeof(ManipulatorBeamCompatibility), nameof(CellAllowedPostfix)));
            if (autoDestroy != null)
            {
                harmony.Patch(
                    autoDestroy,
                    prefix: new HarmonyMethod(
                        typeof(ManipulatorBeamCompatibility), nameof(AutoBuildingDestroyPrefix)));
            }
            if (makeMinified != null)
            {
                harmony.Patch(
                    makeMinified,
                    prefix: new HarmonyMethod(
                        typeof(ManipulatorBeamCompatibility), nameof(AutoBuildingMinifyPrefix)));
            }

            Log.Message("[Storage Group Quotas] Manipulator Beam Emitter quota-aware transfers enabled.");
        }

        private static void CellAllowedPostfix(Map __0, IntVec3 __1, Thing __2, ref bool __result)
        {
            if (!__result || __2 == null)
            {
                return;
            }

            int sourceCountLeaving = __2.Spawned ? __2.stackCount : 0;
            if (!TryGetQuotaBudget(
                __2,
                __1,
                __0,
                sourceCountLeaving,
                out QuotaBudget budget))
            {
                return;
            }

            Reservation destinationReservation = ReservationAt(
                budget.Key,
                __0,
                __1);
            long reservedAmount = ReservedCount(budget.Key);
            int reservedSlots = ReservedStackSlots(budget.Key);
            if (destinationReservation != null)
            {
                reservedAmount = Math.Max(
                    0L,
                    reservedAmount - destinationReservation.Count);
                if (destinationReservation.ReservesStackSlot)
                {
                    reservedSlots = Math.Max(0, reservedSlots - 1);
                }
            }

            long available = budget.TotalRemaining == int.MaxValue
                ? long.MaxValue
                : Math.Max(0L, (long)budget.TotalRemaining - reservedAmount);
            if (available <= 0
                || budget.CellRemaining <= 0
                || (budget.NeedsStackSlot
                    && (long)budget.CurrentStacks + reservedSlots
                        >= budget.MaxStacks))
            {
                __result = false;
            }
        }

        private static bool ClaimStoragePrefix(
            object __0,
            int __1,
            ref bool __result,
            out PendingClaim __state)
        {
            __state = null;
            try
            {
                object transfer = __0;
                if (transfer == null
                    || transferContainer.GetValue(transfer) != null
                    || TrackingByTransfer.ContainsKey(transfer))
                {
                    return true;
                }

                IntVec3 destination = (IntVec3)transferDestination.GetValue(transfer);
                Thing thing = transferThing.GetValue(transfer) as Thing;
                Map map = thing?.MapHeld;
                if (thing == null || map == null || !destination.IsValid)
                {
                    return true;
                }

                int originalCount = (int)transferCount.GetValue(transfer);
                __state = new PendingClaim
                {
                    Map = map,
                    OwnerKey = __1,
                    OriginalCount = originalCount
                };

                // These transfers carry a building at claim time and create Bioferrite/Beer
                // only when lifted. The actual product is checked at arrival.
                if (IsSpecialProducerUnload(transfer))
                {
                    return true;
                }

                int requested = originalCount;
                if (requested <= 0)
                {
                    requested = thing.stackCount;
                }
                requested = Math.Min(requested, thing.stackCount);

                if (!TryGetQuotaBudget(
                    thing,
                    destination,
                    map,
                    requested,
                    out QuotaBudget budget))
                {
                    return true;
                }

                int allowed = AllowedCount(
                    budget,
                    thing,
                    destination,
                    requested,
                    null);
                if (allowed <= 0)
                {
                    __state = null;
                    __result = false;
                    return false;
                }

                transferCount.SetValue(transfer, allowed);
                __state.CountChanged = originalCount != allowed;
                __state.Reservation = new Reservation
                {
                    Key = budget.Key,
                    Count = allowed,
                    Map = map,
                    Destination = destination,
                    ReservesStackSlot = budget.NeedsStackSlot
                };
                return true;
            }
            catch (Exception exception)
            {
                RestoreClaimCount(__0, __state);
                __state = null;
                LogWarning(exception);
                return true;
            }
        }

        private static void ClaimStoragePostfix(
            object __0,
            bool __result,
            PendingClaim __state)
        {
            if (__state == null)
            {
                return;
            }

            if (__result)
            {
                RegisterTracking(
                    __0,
                    new TransferTracking
                    {
                        Map = __state.Map,
                        OwnerKey = __state.OwnerKey,
                        Reservation = __state.Reservation
                    });
            }
            else
            {
                RestoreClaimCount(__0, __state);
            }
        }

        private static bool RetargetStoragePrefix(
            object __0,
            IntVec3 __1,
            int __2,
            ref bool __result,
            out PendingRetarget __state)
        {
            __state = null;
            object transfer = __0;
            if (transfer == null
                || !TrackingByTransfer.TryGetValue(transfer, out TransferTracking tracking))
            {
                return true;
            }

            try
            {
                if (IsSpecialProducerUnload(transfer))
                {
                    return true;
                }

                Thing thing = transferThing.GetValue(transfer) as Thing;
                if (thing == null || tracking.Map == null)
                {
                    return true;
                }

                int transferAmount = tracking.Reservation?.Count
                    ?? Math.Max(0, (int)transferCount.GetValue(transfer));
                if (transferAmount <= 0)
                {
                    transferAmount = Math.Max(0, thing.stackCount);
                }

                if (!TryGetQuotaBudget(
                    thing,
                    __1,
                    tracking.Map,
                    0,
                    out QuotaBudget budget))
                {
                    // Retargeting out of a quota-managed scope is allowed; release the old
                    // reservation only after the beam successfully claims its new cell.
                    __state = new PendingRetarget { Tracking = tracking };
                    return true;
                }

                if (!CanFitFullAmount(
                    budget,
                    thing,
                    __1,
                    transferAmount,
                    tracking.Reservation))
                {
                    __result = false;
                    return false;
                }

                __state = new PendingRetarget
                {
                    Tracking = tracking,
                    NewReservation = new Reservation
                    {
                        Key = budget.Key,
                        Count = transferAmount,
                        Map = tracking.Map,
                        Destination = __1,
                        ReservesStackSlot = budget.NeedsStackSlot
                    }
                };
                return true;
            }
            catch (Exception exception)
            {
                LogWarning(exception);
                return true;
            }
        }

        private static void RetargetStoragePostfix(
            object __0,
            bool __result,
            PendingRetarget __state)
        {
            if (!__result || __state == null || __0 == null)
            {
                return;
            }

            ReplaceReservation(__state.Tracking, __state.NewReservation);
        }

        private static void ReleaseClaimPostfix(object __0)
        {
            ReleaseTracking(__0);
        }

        private static void FinishTransferPrefix(
            object[] __args,
            out FinishTransferState __state)
        {
            __state = null;
            try
            {
                Thing actor = __args?[0] as Thing;
                Thing carried = __args?[1] as Thing;
                object transfer = __args?[2];
                if (actor?.Map == null
                    || carried == null)
                {
                    return;
                }

                Map map = actor.Map;
                IntVec3 fallback = __args.Length > 3 && __args[3] is IntVec3 suppliedFallback
                    ? suppliedFallback
                    : IntVec3.Invalid;

                if (transfer == null)
                {
                    __args[3] = SafeFallbackCell(carried, fallback, actor.Position, map);
                    return;
                }

                if (transferContainer.GetValue(transfer) != null)
                {
                    return;
                }

                IntVec3 destination = (IntVec3)transferDestination.GetValue(transfer);
                if (!destination.IsValid)
                {
                    return;
                }

                if (!TryGetQuotaBudget(
                    carried,
                    destination,
                    map,
                    0,
                    out QuotaBudget budget))
                {
                    return;
                }

                TrackingByTransfer.TryGetValue(transfer, out TransferTracking tracking);
                Reservation ownReservation = tracking?.Reservation;

                if (!CanFitFullAmount(
                    budget,
                    carried,
                    destination,
                    carried.stackCount,
                    ownReservation))
                {
                    // The beam's own fallback restores the complete carried stack to its source.
                    // A later scan may fill a smaller remainder; this arrival never overfills.
                    __state = new FinishTransferState
                    {
                        Transfer = transfer,
                        Destination = destination
                    };
                    transferDestination.SetValue(transfer, IntVec3.Invalid);
                    __args[3] = SafeFallbackCell(carried, fallback, actor.Position, map);
                }
            }
            catch (Exception exception)
            {
                LogWarning(exception);
            }
        }

        private static Exception FinishTransferFinalizer(
            Exception __exception,
            FinishTransferState __state)
        {
            if (__state?.Transfer != null)
            {
                transferDestination.SetValue(__state.Transfer, __state.Destination);
            }

            return __exception;
        }

        private static bool TryGetQuotaBudget(
            Thing thing,
            IntVec3 destination,
            Map map,
            int sourceCountLeaving,
            out QuotaBudget budget)
        {
            budget = null;
            if (thing?.def == null
                || map == null
                || !destination.IsValid
                || !destination.InBounds(map))
            {
                return false;
            }

            ISlotGroup scope = QuotaUtility.ScopeAt(destination, map);
            if (scope == null)
            {
                return false;
            }

            int totalRemaining = QuotaUtility.TotalRemainingForDestination(
                thing,
                destination,
                map,
                sourceCountLeaving);
            int cellRemaining = QuotaUtility.RemainingForDestination(
                thing,
                destination,
                map,
                sourceCountLeaving);
            if (totalRemaining == int.MaxValue && cellRemaining == int.MaxValue)
            {
                return false;
            }

            StorageQuotaData data = QuotaDataStore.Get(scope.Settings);
            bool similarStacks = data.Mode == QuotaMode.SimilarStacks;
            bool compatibleStackExists = destination.GetThingList(map)
                .Any(existing => !existing.Destroyed && existing.CanStackWith(thing));
            int currentStacks = similarStacks
                ? QuotaUtility.StackCount(scope, thing.def)
                : 0;
            if (similarStacks
                && !compatibleStackExists
                && thing.Spawned
                && sourceCountLeaving >= thing.stackCount
                && ReferenceEquals(QuotaUtility.ScopeForThing(thing), scope))
            {
                currentStacks = Math.Max(0, currentStacks - 1);
            }

            budget = new QuotaBudget
            {
                Key = new ReservationKey { Scope = scope, Def = thing.def },
                Map = map,
                TotalRemaining = totalRemaining,
                CellRemaining = cellRemaining,
                CurrentStacks = currentStacks,
                MaxStacks = similarStacks ? data.EffectiveMaxStacks(thing.def) : int.MaxValue,
                NeedsStackSlot = similarStacks && !compatibleStackExists
            };
            return true;
        }

        private static int AllowedCount(
            QuotaBudget budget,
            Thing thing,
            IntVec3 destination,
            int requested,
            Reservation ownReservation)
        {
            if (budget == null || thing == null || requested <= 0)
            {
                return 0;
            }

            long reservedAmount = ReservedCount(budget.Key);
            int reservedSlots = ReservedStackSlots(budget.Key);
            if (ownReservation != null && ownReservation.Key.Equals(budget.Key))
            {
                reservedAmount = Math.Max(0L, reservedAmount - ownReservation.Count);
                if (ownReservation.ReservesStackSlot)
                {
                    reservedSlots = Math.Max(0, reservedSlots - 1);
                }
            }

            long totalAvailable = budget.TotalRemaining == int.MaxValue
                ? int.MaxValue
                : Math.Max(0L, (long)budget.TotalRemaining - reservedAmount);
            int physical = ExternalStorageUtility.PhysicalCapacityAt(
                thing,
                destination,
                budget.Map);
            long allowed = Math.Min(
                requested,
                Math.Min(
                    totalAvailable,
                    Math.Min((long)budget.CellRemaining, physical)));

            if (budget.NeedsStackSlot
                && (long)budget.CurrentStacks + reservedSlots >= budget.MaxStacks)
            {
                return 0;
            }

            return allowed <= 0 ? 0 : (int)Math.Min(int.MaxValue, allowed);
        }

        private static bool CanFitFullAmount(
            QuotaBudget budget,
            Thing thing,
            IntVec3 destination,
            int count,
            Reservation ownReservation)
        {
            return count > 0
                && AllowedCount(budget, thing, destination, count, ownReservation) >= count;
        }

        private static long ReservedCount(ReservationKey key)
        {
            return ReservedByKey.TryGetValue(key, out long count) ? count : 0L;
        }

        private static int ReservedStackSlots(ReservationKey key)
        {
            return ReservedStackSlotsByKey.TryGetValue(key, out int count) ? count : 0;
        }

        private static Reservation ReservationAt(
            ReservationKey key,
            Map map,
            IntVec3 destination)
        {
            foreach (TransferTracking tracking in TrackingByTransfer.Values)
            {
                Reservation reservation = tracking.Reservation;
                if (reservation != null
                    && reservation.Key.Equals(key)
                    && ReferenceEquals(reservation.Map, map)
                    && reservation.Destination == destination)
                {
                    return reservation;
                }
            }

            return null;
        }

        private static void RegisterTracking(object transfer, TransferTracking tracking)
        {
            if (transfer == null || tracking == null)
            {
                return;
            }

            ReleaseTracking(transfer);
            TrackingByTransfer[transfer] = tracking;
            AddReservation(tracking.Reservation);
        }

        private static void ReplaceReservation(
            TransferTracking tracking,
            Reservation reservation)
        {
            if (tracking == null)
            {
                return;
            }

            RemoveReservation(tracking.Reservation);
            tracking.Reservation = reservation;
            AddReservation(reservation);
        }

        private static void AddReservation(Reservation reservation)
        {
            if (reservation == null || reservation.Count <= 0)
            {
                return;
            }

            ReservedByKey[reservation.Key] = ReservedCount(reservation.Key) + reservation.Count;
            if (reservation.ReservesStackSlot)
            {
                ReservedStackSlotsByKey[reservation.Key] =
                    ReservedStackSlots(reservation.Key) + 1;
            }
        }

        private static void RemoveReservation(Reservation reservation)
        {
            if (reservation == null || reservation.Count <= 0)
            {
                return;
            }

            long amountRemaining = ReservedCount(reservation.Key) - reservation.Count;
            if (amountRemaining > 0)
            {
                ReservedByKey[reservation.Key] = amountRemaining;
            }
            else
            {
                ReservedByKey.Remove(reservation.Key);
            }

            if (!reservation.ReservesStackSlot)
            {
                return;
            }

            int slotsRemaining = ReservedStackSlots(reservation.Key) - 1;
            if (slotsRemaining > 0)
            {
                ReservedStackSlotsByKey[reservation.Key] = slotsRemaining;
            }
            else
            {
                ReservedStackSlotsByKey.Remove(reservation.Key);
            }
        }

        private static void ReleaseTracking(object transfer)
        {
            if (transfer == null
                || !TrackingByTransfer.TryGetValue(transfer, out TransferTracking tracking))
            {
                return;
            }

            TrackingByTransfer.Remove(transfer);
            RemoveReservation(tracking.Reservation);
        }

        private static void RestoreClaimCount(object transfer, PendingClaim state)
        {
            if (transfer != null && state != null && state.CountChanged)
            {
                transferCount.SetValue(transfer, state.OriginalCount);
            }
        }

        private static bool IsSpecialProducerUnload(object transfer)
        {
            return transfer != null
                && ((bool)transferBioferriteUnload.GetValue(transfer)
                    || (bool)transferFermentingUnload.GetValue(transfer));
        }

        private static IntVec3 SafeFallbackCell(
            Thing carried,
            IntVec3 requested,
            IntVec3 actorPosition,
            Map map)
        {
            if (CanUseFallback(carried, requested, map))
            {
                return requested;
            }

            if (TryFindUnmanagedDropCell(carried, requested, map, out IntVec3 safe)
                || TryFindUnmanagedDropCell(carried, actorPosition, map, out safe)
                || TryFindAnyUnmanagedDropCell(carried, map, out safe)
                || TryFindAnySafeFallback(carried, map, out safe))
            {
                return safe;
            }

            return actorPosition.IsValid ? actorPosition : IntVec3.Invalid;
        }

        private static bool CanUseFallback(Thing carried, IntVec3 cell, Map map)
        {
            if (carried == null || map == null || !cell.IsValid || !cell.InBounds(map))
            {
                return false;
            }

            if (!TryGetQuotaBudget(carried, cell, map, 0, out QuotaBudget budget))
            {
                return CanPlaceDirectlyInFull(carried, cell, map);
            }

            return CanFitFullAmount(
                budget,
                carried,
                cell,
                carried.stackCount,
                null);
        }

        private static bool CanPlaceDirectlyInFull(Thing carried, IntVec3 cell, Map map)
        {
            return carried != null
                && map != null
                && cell.IsValid
                && cell.InBounds(map)
                && GenPlace.HaulPlaceBlockerIn(carried, cell, map, true) == null
                && ExternalStorageUtility.ExactStackCapacityAt(carried, cell, map)
                    >= carried.stackCount;
        }

        private static bool TryFindUnmanagedDropCell(
            Thing carried,
            IntVec3 center,
            Map map,
            out IntVec3 result)
        {
            result = IntVec3.Invalid;
            if (map == null || !center.IsValid)
            {
                return false;
            }

            const int radiusLimit = 12;
            for (int radius = 0; radius <= radiusLimit; radius++)
            {
                for (int x = -radius; x <= radius; x++)
                {
                    for (int z = -radius; z <= radius; z++)
                    {
                        if (radius > 0 && Math.Abs(x) != radius && Math.Abs(z) != radius)
                        {
                            continue;
                        }

                        IntVec3 candidate = center + new IntVec3(x, 0, z);
                        if (candidate.InBounds(map)
                            && candidate.Standable(map)
                            && candidate.GetSlotGroup(map) == null
                            && !candidate.GetThingList(map)
                                .Any(thing => thing.def.category == ThingCategory.Item)
                            && CanPlaceDirectlyInFull(carried, candidate, map))
                        {
                            result = candidate;
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static bool TryFindAnyUnmanagedDropCell(
            Thing carried,
            Map map,
            out IntVec3 result)
        {
            result = IntVec3.Invalid;
            if (carried == null || map == null)
            {
                return false;
            }

            for (int z = 0; z < map.Size.z; z++)
            {
                for (int x = 0; x < map.Size.x; x++)
                {
                    IntVec3 candidate = new IntVec3(x, 0, z);
                    if (candidate.Standable(map)
                        && candidate.GetSlotGroup(map) == null
                        && !candidate.GetThingList(map)
                            .Any(thing => thing.def.category == ThingCategory.Item)
                        && CanPlaceDirectlyInFull(carried, candidate, map))
                    {
                        result = candidate;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryFindAnySafeFallback(
            Thing carried,
            Map map,
            out IntVec3 result)
        {
            result = IntVec3.Invalid;
            if (carried == null || map == null)
            {
                return false;
            }

            for (int z = 0; z < map.Size.z; z++)
            {
                for (int x = 0; x < map.Size.x; x++)
                {
                    IntVec3 candidate = new IntVec3(x, 0, z);
                    if (CanUseFallback(carried, candidate, map))
                    {
                        result = candidate;
                        return true;
                    }
                }
            }

            return false;
        }

        private static void AutoBuildingDestroyPrefix(Thing __instance)
        {
            ReleaseAutoBuildingClaims(__instance);
        }

        private static void AutoBuildingMinifyPrefix(Thing __0)
        {
            ReleaseAutoBuildingClaims(__0);
        }

        private static void ReleaseAutoBuildingClaims(Thing building)
        {
            if (building == null
                || autoBuildingRuntimeType == null
                || !autoBuildingRuntimeType.IsInstanceOfType(building))
            {
                return;
            }

            int ownerKey = building.thingIDNumber;
            try
            {
                // Use the emitter's own reset so source, storage, construction, transporter,
                // strip, and special-container claims are all released—not just SGQ-tracked
                // storage destinations. FinishTransferAuto calls made by the reset are already
                // guarded by this adapter's interruption fallback.
                resetAutoRuntimeMethod.Invoke(building, new object[] { true });
            }
            catch (Exception exception)
            {
                LogWarning(exception);
            }

            // Fail-safe for an unexpected runtime exception or an orphaned storage transfer
            // that was no longer present in the native queue/channel collections.
            List<object> transfers = TrackingByTransfer
                .Where(pair => pair.Value.OwnerKey == ownerKey)
                .Select(pair => pair.Key)
                .ToList();
            foreach (object transfer in transfers)
            {
                try
                {
                    releaseClaimMethod.Invoke(null, new[] { transfer, (object)ownerKey });
                }
                catch (Exception exception)
                {
                    ReleaseTracking(transfer);
                    LogWarning(exception);
                }
            }
        }

        private static void LogWarning(Exception exception)
        {
            if (warningLogged)
            {
                return;
            }

            warningLogged = true;
            Log.Warning("[Storage Group Quotas] Manipulator Beam compatibility encountered an "
                + "unexpected runtime API and left that transfer unchanged. "
                + exception.GetBaseException().Message);
        }
    }
}
