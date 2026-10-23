using System.Runtime.CompilerServices;
using HarmonyLib;
using Ships.Grid;
using Ships.Grid.Model;

namespace ShipPerformanceFix
{
    /// <summary>
    /// Every frame, on the client and on the host, the game looks for the ship the player could reposition, and for each
    /// ship of the space asks the grid for its bounds. The grid answers by reading every voxel of every 8x8x8 chunk: on
    /// a 25,000-panel ship that is 1,500 chunks and ~12 ms per frame.
    ///
    /// The bounds are kept per grid and dropped whenever a part is added, changed or removed.
    /// </summary>
    internal static class HullBoundsCache
    {
        private sealed class Entry
        {
            public bool Valid;
            public CoordBounds Bounds;
        }

        private static readonly ConditionalWeakTable<ShipGrid, Entry> Cache = new ConditionalWeakTable<ShipGrid, Entry>();

        public static bool Enabled = true;

        internal static void Patch(HarmonyLib.Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(ShipGrid), nameof(ShipGrid.GetCoordBounds)),
                prefix: new HarmonyMethod(typeof(HullBoundsCache), nameof(GetPrefix)),
                postfix: new HarmonyMethod(typeof(HullBoundsCache), nameof(GetPostfix)));
            var invalidate = new HarmonyMethod(typeof(HullBoundsCache), nameof(Invalidate));
            foreach (string name in new[] { nameof(ShipGrid.AddHullPart), nameof(ShipGrid.UpdateHullPart), nameof(ShipGrid.RemoveHullPart), nameof(ShipGrid.AddStatefulPart), nameof(ShipGrid.RemoveStatefulPart) })
                harmony.Patch(AccessTools.Method(typeof(ShipGrid), name), prefix: invalidate);
        }

        private static bool GetPrefix(ShipGrid __instance, ref CoordBounds __result, out bool __state)
        {
            __state = false;
            if (!Enabled) return true;
            Entry e = Cache.GetOrCreateValue(__instance);
            if (!e.Valid)
            {
                __state = true;
                return true;
            }
            __result = e.Bounds;
            return false;
        }

        private static void GetPostfix(ShipGrid __instance, CoordBounds __result, bool __state)
        {
            if (!__state) return;
            Entry e = Cache.GetOrCreateValue(__instance);
            e.Bounds = __result;
            e.Valid = true;
        }

        private static void Invalidate(ShipGrid __instance)
        {
            if (Cache.TryGetValue(__instance, out Entry e)) e.Valid = false;
        }
    }
}
