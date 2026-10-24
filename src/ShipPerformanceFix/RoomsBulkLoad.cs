using System.Collections.Generic;
using HarmonyLib;
using Ships.Rooms;
using Ships.Rooms.Model;
using UnityEngine;

namespace ShipPerformanceFix
{
    /// <summary>
    /// When a ship is loaded or spawned, every hull voxel goes into the rooms octree in one batch, and the octree splits
    /// down to single voxels around each of them: 2.8 million leaves for a 69,000-panel ship. Every split relinks the
    /// eight new leaves to the neighbours of the old one, removing the old leaf from each neighbour's list with a linear
    /// search. That took 8 of the 13.6 s the rooms of that ship cost on load (i3-9100F), on the host and again on each
    /// client.
    ///
    /// For a batch of more than <see cref="MinWalls"/> walls into a new octree, splits only create the children, and the face neighbours
    /// of every leaf are rebuilt once at the end by pairing adjacent subtrees: the same links, without any search.
    /// </summary>
    internal static class RoomsBulkLoad
    {
        private const int MinWalls = 4096;

        public static bool Enabled = true;
        private static bool _bulk;
#if DEV
        public static double LastRebuildMs;
#endif

        private static readonly AccessTools.FieldRef<ShipRoomsVoxelsTree, ShipRoomsVoxelsTreeNode> RootRef = AccessTools.FieldRefAccess<ShipRoomsVoxelsTree, ShipRoomsVoxelsTreeNode>("_root");

        internal static void Patch(HarmonyLib.Harmony harmony)
        {
#if DEV
            // dev: start without it (to compare with the game's own links) when UserData/stellar_bulk_off exists
            Enabled = !System.IO.File.Exists(System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "stellar_bulk_off"));
#endif
            harmony.Patch(AccessTools.Method(typeof(ShipRoomsVoxelsTree), nameof(ShipRoomsVoxelsTree.AddWalls)),
                prefix: new HarmonyMethod(typeof(RoomsBulkLoad), nameof(AddWallsPrefix)),
                postfix: new HarmonyMethod(typeof(RoomsBulkLoad), nameof(AddWallsPostfix)));
            harmony.Patch(AccessTools.Method(typeof(ShipRoomsVoxelsTreeNode), "CreateChildLeaves"),
                prefix: new HarmonyMethod(typeof(RoomsBulkLoad), nameof(SplitPrefix)));
        }

        private static void AddWallsPrefix(ShipRoomsVoxelsTree __instance, IEnumerable<Vector3Int> voxelCoords)
        {
            // a new octree only (load, spawn): on a built one the rebuild would walk every leaf for a few panels
            _bulk = Enabled && voxelCoords is ICollection<Vector3Int> c && c.Count > MinWalls && RootRef(__instance).IsLeaf;
        }

        private static void AddWallsPostfix(ShipRoomsVoxelsTree __instance)
        {
            if (!_bulk) return;
            _bulk = false;
#if DEV
            var watch = System.Diagnostics.Stopwatch.StartNew();
#endif
            RebuildNeighbors(RootRef(__instance));
#if DEV
            LastRebuildMs = watch.Elapsed.TotalMilliseconds;
#endif
        }

#if DEV
        /// <summary>Compares the tree's neighbour lists with a rebuild (which then replaces them).</summary>
        public static string Check(ShipRoomsVoxelsTree tree)
        {
            var leaves = tree.GetLeaves();
            var before = new Dictionary<ShipRoomsVoxelsTreeNode, HashSet<ShipRoomsVoxelsTreeNode>[]>();
            long links = 0;
            foreach (var l in leaves)
            {
                var sets = Lists(l).ConvertAll(x => new HashSet<ShipRoomsVoxelsTreeNode>(x, ReferenceComparer.Instance)).ToArray();
                foreach (var x in Lists(l)) links += x.Count;
                before[l] = sets;
            }
            RebuildNeighbors(RootRef(tree));
            long rebuilt = 0;
            int differ = 0, dupes = 0;
            foreach (var l in leaves)
            {
                var now = Lists(l);
                for (int i = 0; i < 6; i++)
                {
                    rebuilt += now[i].Count;
                    if (new HashSet<ShipRoomsVoxelsTreeNode>(now[i], ReferenceComparer.Instance).Count != now[i].Count) dupes++;
                    if (!before[l][i].SetEquals(now[i])) differ++;
                }
            }
            return $"{leaves.Length} leaves, links {links} -> rebuilt {rebuilt}, lists differing {differ}, lists with duplicates {dupes}";
        }

        private static List<List<ShipRoomsVoxelsTreeNode>> Lists(ShipRoomsVoxelsTreeNode l) =>
            new List<List<ShipRoomsVoxelsTreeNode>> { l.NeighborsLeft, l.NeighborsRight, l.NeighborsBottom, l.NeighborsTop, l.NeighborsBack, l.NeighborsForward };

        private sealed class ReferenceComparer : IEqualityComparer<ShipRoomsVoxelsTreeNode>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public bool Equals(ShipRoomsVoxelsTreeNode a, ShipRoomsVoxelsTreeNode b) => ReferenceEquals(a, b);
            public int GetHashCode(ShipRoomsVoxelsTreeNode n) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(n);
        }
#endif

        /// <summary>During a batch: the eight children and their air, no neighbour links.</summary>
        private static bool SplitPrefix(ShipRoomsVoxelsTreeNode __instance)
        {
            if (!_bulk) return true;
            ShipRoomsVoxelsTreeNode n = __instance;
            int h = n.Bounds / 2;
            Vector3Int c = n.Coord;
            n.LeftBottomBack = Child(n, c.x, c.y, c.z, h);
            n.LeftBottomForward = Child(n, c.x, c.y, c.z + h, h);
            n.LeftTopBack = Child(n, c.x, c.y + h, c.z, h);
            n.LeftTopForward = Child(n, c.x, c.y + h, c.z + h, h);
            n.RightBottomBack = Child(n, c.x + h, c.y, c.z, h);
            n.RightBottomForward = Child(n, c.x + h, c.y, c.z + h, h);
            n.RightTopBack = Child(n, c.x + h, c.y + h, c.z, h);
            n.RightTopForward = Child(n, c.x + h, c.y + h, c.z + h, h);
            return false;
        }

        private static ShipRoomsVoxelsTreeNode Child(ShipRoomsVoxelsTreeNode parent, int x, int y, int z, int bounds) =>
            new ShipRoomsVoxelsTreeNode(new Vector3Int(x, y, z), bounds) { TmpGasContent = parent.TmpGasContent };

        /// <summary>Every leaf's six neighbour lists: the leaves that share a face with it, each once.</summary>
        internal static void RebuildNeighbors(ShipRoomsVoxelsTreeNode root)
        {
            Clear(root);
            Link(root);
        }

        private static void Clear(ShipRoomsVoxelsTreeNode n)
        {
            n.NeighborsLeft.Clear();
            n.NeighborsRight.Clear();
            n.NeighborsBottom.Clear();
            n.NeighborsTop.Clear();
            n.NeighborsBack.Clear();
            n.NeighborsForward.Clear();
            if (n.IsLeaf) return;
            Clear(n.LeftBottomBack);
            Clear(n.LeftBottomForward);
            Clear(n.LeftTopBack);
            Clear(n.LeftTopForward);
            Clear(n.RightBottomBack);
            Clear(n.RightBottomForward);
            Clear(n.RightTopBack);
            Clear(n.RightTopForward);
        }

        /// <summary>Links the faces inside a node: between its children, then inside each child.</summary>
        private static void Link(ShipRoomsVoxelsTreeNode n)
        {
            if (n.IsLeaf) return;
            LinkX(n.LeftBottomBack, n.RightBottomBack);
            LinkX(n.LeftBottomForward, n.RightBottomForward);
            LinkX(n.LeftTopBack, n.RightTopBack);
            LinkX(n.LeftTopForward, n.RightTopForward);
            LinkY(n.LeftBottomBack, n.LeftTopBack);
            LinkY(n.LeftBottomForward, n.LeftTopForward);
            LinkY(n.RightBottomBack, n.RightTopBack);
            LinkY(n.RightBottomForward, n.RightTopForward);
            LinkZ(n.LeftBottomBack, n.LeftBottomForward);
            LinkZ(n.LeftTopBack, n.LeftTopForward);
            LinkZ(n.RightBottomBack, n.RightBottomForward);
            LinkZ(n.RightTopBack, n.RightTopForward);
            Link(n.LeftBottomBack);
            Link(n.LeftBottomForward);
            Link(n.LeftTopBack);
            Link(n.LeftTopForward);
            Link(n.RightBottomBack);
            Link(n.RightBottomForward);
            Link(n.RightTopBack);
            Link(n.RightTopForward);
        }

        /// <summary>a is left of b (a's +x face against b's -x face).</summary>
        private static void LinkX(ShipRoomsVoxelsTreeNode a, ShipRoomsVoxelsTreeNode b)
        {
            bool la = a.IsLeaf, lb = b.IsLeaf;
            if (la && lb)
            {
                a.NeighborsRight.Add(b);
                b.NeighborsLeft.Add(a);
            }
            else if (!la && !lb)
            {
                LinkX(a.RightBottomBack, b.LeftBottomBack);
                LinkX(a.RightBottomForward, b.LeftBottomForward);
                LinkX(a.RightTopBack, b.LeftTopBack);
                LinkX(a.RightTopForward, b.LeftTopForward);
            }
            else if (!la)
            {
                LinkX(a.RightBottomBack, b);
                LinkX(a.RightBottomForward, b);
                LinkX(a.RightTopBack, b);
                LinkX(a.RightTopForward, b);
            }
            else
            {
                LinkX(a, b.LeftBottomBack);
                LinkX(a, b.LeftBottomForward);
                LinkX(a, b.LeftTopBack);
                LinkX(a, b.LeftTopForward);
            }
        }

        /// <summary>a is below b.</summary>
        private static void LinkY(ShipRoomsVoxelsTreeNode a, ShipRoomsVoxelsTreeNode b)
        {
            bool la = a.IsLeaf, lb = b.IsLeaf;
            if (la && lb)
            {
                a.NeighborsTop.Add(b);
                b.NeighborsBottom.Add(a);
            }
            else if (!la && !lb)
            {
                LinkY(a.LeftTopBack, b.LeftBottomBack);
                LinkY(a.LeftTopForward, b.LeftBottomForward);
                LinkY(a.RightTopBack, b.RightBottomBack);
                LinkY(a.RightTopForward, b.RightBottomForward);
            }
            else if (!la)
            {
                LinkY(a.LeftTopBack, b);
                LinkY(a.LeftTopForward, b);
                LinkY(a.RightTopBack, b);
                LinkY(a.RightTopForward, b);
            }
            else
            {
                LinkY(a, b.LeftBottomBack);
                LinkY(a, b.LeftBottomForward);
                LinkY(a, b.RightBottomBack);
                LinkY(a, b.RightBottomForward);
            }
        }

        /// <summary>a is behind b.</summary>
        private static void LinkZ(ShipRoomsVoxelsTreeNode a, ShipRoomsVoxelsTreeNode b)
        {
            bool la = a.IsLeaf, lb = b.IsLeaf;
            if (la && lb)
            {
                a.NeighborsForward.Add(b);
                b.NeighborsBack.Add(a);
            }
            else if (!la && !lb)
            {
                LinkZ(a.LeftBottomForward, b.LeftBottomBack);
                LinkZ(a.LeftTopForward, b.LeftTopBack);
                LinkZ(a.RightBottomForward, b.RightBottomBack);
                LinkZ(a.RightTopForward, b.RightTopBack);
            }
            else if (!la)
            {
                LinkZ(a.LeftBottomForward, b);
                LinkZ(a.LeftTopForward, b);
                LinkZ(a.RightBottomForward, b);
                LinkZ(a.RightTopForward, b);
            }
            else
            {
                LinkZ(a, b.LeftBottomBack);
                LinkZ(a, b.LeftTopBack);
                LinkZ(a, b.RightBottomBack);
                LinkZ(a, b.RightTopBack);
            }
        }
    }
}
