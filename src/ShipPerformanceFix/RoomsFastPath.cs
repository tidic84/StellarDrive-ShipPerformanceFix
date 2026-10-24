using System.Collections.Generic;
using HarmonyLib;
using Items.Model;
using Ships.Interface.Model;
using Ships.Interface.Model.Gases;
using Ships.Interface.Model.State;
using Ships.Interface.Values;
using Ships.Rooms;
using Ships.Rooms.Model;
using UnityEngine;
using WorldProvision.Interface.Services;

namespace ShipPerformanceFix
{
    /// <summary>
    /// The game recomputes a ship's rooms after every hull change by clearing the air region around the change and
    /// flooding it again from both sides of each panel. The air outside the hull is one region that follows the whole
    /// hull surface through the octree, so every panel placed or removed against it costs a walk over the entire
    /// outside of the ship (tens of milliseconds at 2,000 parts, on the host and again on every client).
    ///
    /// Same labels and volumes as the game, without the full walks:
    /// - outside air carries the same label as cleared air (<see cref="ShipRoomsConstants.NoRoomId"/>), so it is never
    ///   cleared, and a flood that reaches the octree bounds is known to be outside and stops there (largest nodes
    ///   first, so it gets there in a few steps);
    /// - panels added inside one room keep the room's labels: floods from both sides stop as soon as they meet, and
    ///   only a piece actually cut off is walked (<see cref="UpdateInRoom"/>);
    /// - panels removed: the freed air joins the single region it touches (<see cref="UpdateRemoved"/>).
    /// Anything else (a room opened to the outside, rooms merged, adds and removes in one tick) clears the regions
    /// involved and floods them as the game does. Gas: a room's air keeps its density through splits and new panels;
    /// the game counted every leaf the octree split during the update as vacuum, thinning the air a little each time.
    /// Room ids are local to each machine (saves use the seed coordinate), so hosts and clients may mix versions.
    /// </summary>
    internal static class RoomsFastPath
    {
        internal static bool Enabled = true;
        /// <summary>Which path the last update took (dev timing).</summary>
        internal static string LastPath = "";

        private static readonly AccessTools.FieldRef<ShipRoomsVoxelUpdater, HashSet<Vector3Int>> ClearSeedsRef = AccessTools.FieldRefAccess<ShipRoomsVoxelUpdater, HashSet<Vector3Int>>("_clearRoomsVoxelsToScan");
        private static readonly AccessTools.FieldRef<ShipRoomsVoxelUpdater, HashSet<Vector3Int>> CreateSeedsRef = AccessTools.FieldRefAccess<ShipRoomsVoxelUpdater, HashSet<Vector3Int>>("_createRoomsVoxelsToScan");
        private static readonly AccessTools.FieldRef<ShipRoomsVoxelUpdater, List<ShipRoomsVoxelsTreeNode>> AllVisitedRef = AccessTools.FieldRefAccess<ShipRoomsVoxelUpdater, List<ShipRoomsVoxelsTreeNode>>("_allVisitedNodes");
        private static readonly AccessTools.FieldRef<ShipRoomsVoxelUpdater, List<ShipRoomsVoxelsTreeNode>> ClearedRef = AccessTools.FieldRefAccess<ShipRoomsVoxelUpdater, List<ShipRoomsVoxelsTreeNode>>("_clearedRoomVoxels");
        private static readonly AccessTools.FieldRef<ShipRoomsVoxelUpdater, HashSet<ushort>> PreviousRoomsRef = AccessTools.FieldRefAccess<ShipRoomsVoxelUpdater, HashSet<ushort>>("_previousRooms");
        private static readonly AccessTools.FieldRef<ShipRoomsVoxelUpdater, ShipRoomsVoxelsTree> TreeRef = AccessTools.FieldRefAccess<ShipRoomsVoxelUpdater, ShipRoomsVoxelsTree>("_tree");
        private static readonly AccessTools.FieldRef<ShipRoomsVoxelUpdater, ShipRoomsGases> GasesRef = AccessTools.FieldRefAccess<ShipRoomsVoxelUpdater, ShipRoomsGases>("_gases");
        private static readonly AccessTools.FieldRef<ShipRoomsVoxelUpdater, IWorldEnvironmentProvider> EnvRef = AccessTools.FieldRefAccess<ShipRoomsVoxelUpdater, IWorldEnvironmentProvider>("_envProvider");
        private static readonly AccessTools.FieldRef<ShipRoomsVoxelUpdater, IShipStateRead> ShipRef = AccessTools.FieldRefAccess<ShipRoomsVoxelUpdater, IShipStateRead>("_ship");
        private static readonly AccessTools.FieldRef<ShipRoomsVoxelUpdater, List<Vector3Int>> AddWallsRef = AccessTools.FieldRefAccess<ShipRoomsVoxelUpdater, List<Vector3Int>>("_addWallsVoxels");
        private static readonly AccessTools.FieldRef<ShipRoomsVoxelUpdater, List<Vector3Int>> RemoveWallsRef = AccessTools.FieldRefAccess<ShipRoomsVoxelUpdater, List<Vector3Int>>("_removeWallsVoxels");

        // Frontier bucketed by node size (log2 of Bounds): the largest pending node is expanded first.
        private static readonly Stack<ShipRoomsVoxelsTreeNode>[] Frontier = NewFrontier();
        private static int _top = -1;
        private static readonly Stack<ShipRoomsVoxelsTreeNode> Plain = new Stack<ShipRoomsVoxelsTreeNode>();
        private static readonly List<ShipRoomsVoxelsTreeNode> SeedNodes = new List<ShipRoomsVoxelsTreeNode>();
        private static readonly HashSet<ShipRoomsVoxelsTreeNode> Outside = new HashSet<ShipRoomsVoxelsTreeNode>(NodeRef.Instance);
        // Gas density of the air nodes whose state this update knows: cleared room air, and leaves split from known air
        // or merged from it. Nodes in neither map nor Unknown are untouched outside air, at the atmosphere's density.
        private static readonly Dictionary<ShipRoomsVoxelsTreeNode, float> Known = new Dictionary<ShipRoomsVoxelsTreeNode, float>(NodeRef.Instance);
        // air that was a wall a moment ago (or merged only from such air): no state to carry over
        private static readonly HashSet<ShipRoomsVoxelsTreeNode> Unknown = new HashSet<ShipRoomsVoxelsTreeNode>(NodeRef.Instance);
        private static float _atmosphere;

        // In-room update (only panels added, all inside one room): the room keeps its labels and is not cleared.
        private static ushort _inRoom;
        private static int _walledVoxels;
        private static readonly Dictionary<ShipRoomsVoxelsTreeNode, ushort> SplitRoom = new Dictionary<ShipRoomsVoxelsTreeNode, ushort>(NodeRef.Instance);
        private sealed class Flood
        {
            public readonly Stack<ShipRoomsVoxelsTreeNode> Frontier = new Stack<ShipRoomsVoxelsTreeNode>();
            public readonly List<ShipRoomsVoxelsTreeNode> Nodes = new List<ShipRoomsVoxelsTreeNode>();
            public int Parent;
            public Vector3Int Seed;
        }
        private static readonly List<Flood> Floods = new List<Flood>();
        private static readonly HashSet<int> OpenRoots = new HashSet<int>();
        private static readonly Dictionary<int, float> GroupVolumes = new Dictionary<int, float>();
        private static readonly Dictionary<int, List<int>> Members = new Dictionary<int, List<int>>();
        private static readonly Stack<Flood> FloodPool = new Stack<Flood>();
        private static readonly Dictionary<ShipRoomsVoxelsTreeNode, int> Owner = new Dictionary<ShipRoomsVoxelsTreeNode, int>(NodeRef.Instance);
        // Removal-only update: the leaves the removed walls freed or merged, and the freed voxels.
        private static bool _removing;
        private static readonly HashSet<ShipRoomsVoxelsTreeNode> Touched = new HashSet<ShipRoomsVoxelsTreeNode>(NodeRef.Instance);
        private static readonly HashSet<ShipRoomsVoxelsTreeNode> Live = new HashSet<ShipRoomsVoxelsTreeNode>(NodeRef.Instance);
        private static readonly List<Vector3Int> Freed = new List<Vector3Int>();
        private static readonly List<ShipRoomsVoxelsTreeNode> Group = new List<ShipRoomsVoxelsTreeNode>();
        private static readonly Dictionary<ushort, int> FreedPerRoom = new Dictionary<ushort, int>();

        public static void Patch(HarmonyLib.Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(ShipRoomsVoxelUpdater), "ClearRoomsVoxels"),
                prefix: new HarmonyMethod(typeof(RoomsFastPath), nameof(ClearPrefix)));
            h.Patch(AccessTools.Method(typeof(ShipRoomsVoxelUpdater), "CreateRooms"),
                prefix: new HarmonyMethod(typeof(RoomsFastPath), nameof(CreatePrefix)));
            h.Patch(AccessTools.Method(typeof(ShipRoomsVoxelsTreeNode), "CreateChildLeaves"),
                postfix: new HarmonyMethod(typeof(RoomsFastPath), nameof(SplitPostfix)));
            h.Patch(AccessTools.Method(typeof(ShipRoomsVoxelsTreeNode), "RemoveChildLeaves"),
                prefix: new HarmonyMethod(typeof(RoomsFastPath), nameof(MergePrefix)));
            h.Patch(AccessTools.Method(typeof(ShipRoomsVoxelsTreeNode), nameof(ShipRoomsVoxelsTreeNode.RemoveWall)),
                prefix: new HarmonyMethod(typeof(RoomsFastPath), nameof(RemoveWallPrefix)), postfix: new HarmonyMethod(typeof(RoomsFastPath), nameof(RemoveWallPostfix)));
            h.Patch(AccessTools.Method(typeof(ShipRoomsVoxelsTreeNode), nameof(ShipRoomsVoxelsTreeNode.AddWall)),
                prefix: new HarmonyMethod(typeof(RoomsFastPath), nameof(AddWallPrefix)), postfix: new HarmonyMethod(typeof(RoomsFastPath), nameof(AddWallPostfix)));
        }

        private static Stack<ShipRoomsVoxelsTreeNode>[] NewFrontier()
        {
            var f = new Stack<ShipRoomsVoxelsTreeNode>[32];
            for (int i = 0; i < f.Length; i++) f[i] = new Stack<ShipRoomsVoxelsTreeNode>();
            return f;
        }

        /// <summary>A leaf split in eight: the children hold the air the leaf held.</summary>
        private static void SplitPostfix(ShipRoomsVoxelsTreeNode __instance)
        {
            if (!Enabled) return;
            if (_inRoom != 0 && SplitRoom.TryGetValue(__instance, out ushort room))
                foreach (ShipRoomsVoxelsTreeNode c in Children(__instance)) c.RoomId = room;
            bool known = Known.TryGetValue(__instance, out float d);
            bool unknown = !known && Unknown.Contains(__instance);
            foreach (ShipRoomsVoxelsTreeNode c in Children(__instance))
            {
                if (known) Known[c] = d;
                else if (unknown) Unknown.Add(c);
            }
        }

        /// <summary>Eight empty children about to merge back into their parent: it takes their mean density.</summary>
        private static void MergePrefix(ShipRoomsVoxelsTreeNode __instance)
        {
            if (!Enabled) return;
            if (_removing) Touched.Add(__instance);
            float sum = 0f;
            int n = 0;
            foreach (ShipRoomsVoxelsTreeNode c in Children(__instance))
            {
                if (Unknown.Contains(c)) continue;
                sum += Known.TryGetValue(c, out float d) ? d : _atmosphere;
                n++;
            }
            Unknown.Remove(__instance);
            if (n > 0) Known[__instance] = sum / n;
            else
            {
                Known.Remove(__instance);
                Unknown.Add(__instance);
            }
        }

        /// <summary>AddWall drops the label of every node on its way down: keep the room of the leaf it splits.</summary>
        private static void AddWallPrefix(ShipRoomsVoxelsTreeNode __instance, out bool __state)
        {
            __state = _inRoom != 0 && __instance.Bounds == 1 && __instance.WallCount == 0;
            if (_inRoom != 0 && __instance.RoomId != ShipRoomsConstants.NoRoomId && __instance.IsLeaf) SplitRoom[__instance] = __instance.RoomId;
        }

        private static void AddWallPostfix(ShipRoomsVoxelsTreeNode __instance, bool __state)
        {
            if (__state && __instance.WallCount > 0) _walledVoxels++;
        }

        private static void RemoveWallPrefix(ShipRoomsVoxelsTreeNode __instance, out bool __state)
        {
            __state = __instance.Bounds == 1 && __instance.WallCount > 0;
        }

        private static void RemoveWallPostfix(ShipRoomsVoxelsTreeNode __instance, bool __state)
        {
            if (!Enabled || !__state || __instance.WallCount > 0) return;
            Unknown.Add(__instance);
            if (_removing)
            {
                Touched.Add(__instance);
                Freed.Add(__instance.Coord);
            }
        }

        private static IEnumerable<ShipRoomsVoxelsTreeNode> Children(ShipRoomsVoxelsTreeNode p)
        {
            yield return p.LeftBottomBack;
            yield return p.LeftBottomForward;
            yield return p.LeftTopBack;
            yield return p.LeftTopForward;
            yield return p.RightBottomBack;
            yield return p.RightBottomForward;
            yield return p.RightTopBack;
            yield return p.RightTopForward;
        }

        /// <summary>The game's clear, minus the outside region: its nodes already carry NoRoomId.</summary>
        private static bool ClearPrefix(ShipRoomsVoxelUpdater __instance)
        {
            if (!Enabled) return true;
            ShipRoomsVoxelsTree tree = TreeRef(__instance);
            ShipRoomsGases gases = GasesRef(__instance);
            List<ShipRoomsVoxelsTreeNode> cleared = ClearedRef(__instance);
            HashSet<ushort> previous = PreviousRoomsRef(__instance);
            IShipStateRead ship = ShipRef(__instance);
            _atmosphere = EnvRef(__instance).GetAtmosphereDensityAtPosition(ship.RelativeSpaceId, Vector3.zero);
            Known.Clear();
            Unknown.Clear();
            SplitRoom.Clear();
            Touched.Clear();
            Freed.Clear();
            _removing = false;
            _inRoom = InRoomCandidate(__instance, tree);
            _walledVoxels = 0;
            if (_inRoom != 0) LastPath = "in-room";
            if (_inRoom != 0) return false;
            // only panels removed: nothing to clear, the freed air is labelled after the walls come out
            _removing = AddWallsRef(__instance).Count == 0 && RemoveWallsRef(__instance).Count > 0;
            if (_removing) LastPath = "removed";
            if (_removing) return false;

            Plain.Clear();
            foreach (Vector3Int seed in ClearSeedsRef(__instance))
            {
                ShipRoomsVoxelsTreeNode leaf = tree.GetLeaf(seed);
                if (leaf.RoomId != ShipRoomsConstants.NoRoomId) Plain.Push(leaf);
            }
            while (Plain.Count > 0)
            {
                ShipRoomsVoxelsTreeNode n = Plain.Pop();
                if (n.Visited || n.WallCount > 0) continue;
                n.Visited = true;
                cleared.Add(n);
                previous.Add(n.RoomId);
                PushAll(n.NeighborsLeft, Plain);
                PushAll(n.NeighborsRight, Plain);
                PushAll(n.NeighborsBottom, Plain);
                PushAll(n.NeighborsTop, Plain);
                PushAll(n.NeighborsBack, Plain);
                PushAll(n.NeighborsForward, Plain);
            }
            foreach (ShipRoomsVoxelsTreeNode n in cleared)
            {
                n.TmpGasContent = gases.TryGetRoomGasContent(n.RoomId, out GasState g) ? g : new GasState { GasItemId = 0u, GasDensity = 0f };
                Known[n] = n.TmpGasContent.GasDensity;
                n.RoomId = ShipRoomsConstants.NoRoomId;
                n.Visited = false;
            }
            return false;
        }

        private static bool CreatePrefix(ShipRoomsVoxelUpdater __instance)
        {
            if (!Enabled) return true;
            ShipRoomsVoxelsTree tree = TreeRef(__instance);
            ShipRoomsGases gases = GasesRef(__instance);
            List<ShipRoomsVoxelsTreeNode> allVisited = AllVisitedRef(__instance);
            Vector3Int lower = tree.LowerBounds, upper = tree.UpperBounds;
            Outside.Clear();
            try
            {
                if (_inRoom != 0 && UpdateInRoom(__instance, tree, gases)) return false;
                if (_removing && UpdateRemoved(__instance, tree, gases)) return false;
                foreach (Vector3Int seed in CreateSeedsRef(__instance))
                    FloodFromSeed(tree.GetLeaf(seed), tree, gases, allVisited, lower, upper, seed);
            }
            finally
            {
                ClearFrontier();
                Outside.Clear();
                Known.Clear();
                Unknown.Clear();
                SeedNodes.Clear();
                _inRoom = 0;
                _removing = false;
                Touched.Clear();
                Freed.Clear();
                Live.Clear();
                SplitRoom.Clear();
                Owner.Clear();
                foreach (Flood f in Floods)
                {
                    f.Frontier.Clear();
                    f.Nodes.Clear();
                    FloodPool.Push(f);
                }
                Floods.Clear();
            }
            return false;
        }

        // ------------------------------------------------------------------ panels added inside one room

        /// <summary>
        /// The room whose air every changed voxel lies in, when this update only adds panels and all of them go into
        /// the same room (not the outside); 0 otherwise.
        /// </summary>
        private static ushort InRoomCandidate(ShipRoomsVoxelUpdater updater, ShipRoomsVoxelsTree tree)
        {
            LastPath = "full: mixed";
            if (RemoveWallsRef(updater).Count > 0) return 0;
            LastPath = "full: outside or several rooms";
            ushort room = 0;
            foreach (Vector3Int v in ClearSeedsRef(updater))
            {
                ShipRoomsVoxelsTreeNode leaf = tree.GetLeaf(v);
                if (leaf.WallCount > 0) continue;
                if (leaf.RoomId == ShipRoomsConstants.NoRoomId || (room != 0 && leaf.RoomId != room)) return 0;
                room = leaf.RoomId;
            }
            if (room == 0) return 0;
            foreach (Vector3Int v in CreateSeedsRef(updater))
            {
                ShipRoomsVoxelsTreeNode leaf = tree.GetLeaf(v);
                if (leaf.WallCount == 0 && leaf.RoomId != room) return 0;
            }
            return GasesRef(updater).HasRoom(room) ? room : (ushort)0;
        }

        /// <summary>
        /// New panels inside room R (walls already added, R's labels kept): a flood from each side of the panels, run
        /// in turns, merges with the floods it meets. When they all meet, R is still one room and only loses the volume
        /// the panels took. When a group of floods runs out without meeting the rest, it enclosed a piece of R: that
        /// piece becomes a new room with R's air. So only the pieces cut off are walked, never the whole room.
        /// Returns false when the seeds are not as expected: R is then cleared and rebuilt by the regular floods.
        /// </summary>
        private static bool UpdateInRoom(ShipRoomsVoxelUpdater updater, ShipRoomsVoxelsTree tree, ShipRoomsGases gases)
        {
            ushort room = _inRoom;
            if (!gases.TryGetRoomGasState(room, out RoomGasState state)) return FallBack(updater, tree, room);
            foreach (Vector3Int seed in CreateSeedsRef(updater))
            {
                ShipRoomsVoxelsTreeNode leaf = tree.GetLeaf(seed);
                if (leaf.WallCount > 0) continue;
                if (leaf.RoomId != room) return FallBack(updater, tree, room);
                if (Owner.ContainsKey(leaf)) continue;
                Flood f = FloodPool.Count > 0 ? FloodPool.Pop() : new Flood();
                f.Parent = Floods.Count;
                f.Seed = seed;
                f.Frontier.Push(leaf);
                Owner[leaf] = Floods.Count;
                f.Nodes.Add(leaf);
                Floods.Add(f);
            }

            const int Step = 16;
            int roots = Floods.Count;
            while (roots > 1)
            {
                for (int i = 0; i < Floods.Count; i++)
                {
                    Flood f = Floods[i];
                    for (int k = 0; k < Step && f.Frontier.Count > 0; k++)
                    {
                        ShipRoomsVoxelsTreeNode n = f.Frontier.Pop();
                        Expand(n.NeighborsLeft, i, room);
                        Expand(n.NeighborsRight, i, room);
                        Expand(n.NeighborsBottom, i, room);
                        Expand(n.NeighborsTop, i, room);
                        Expand(n.NeighborsBack, i, room);
                        Expand(n.NeighborsForward, i, room);
                    }
                }
                // at most one group may stay open: every other one has run out, it is a piece cut off from the room
                roots = 0;
                OpenRoots.Clear();
                for (int i = 0; i < Floods.Count; i++)
                {
                    int r = Find(i);
                    if (r == i) roots++;
                    if (Floods[i].Frontier.Count > 0) OpenRoots.Add(r);
                }
                if (OpenRoots.Count <= 1) break;
            }
            if (roots <= 1)
            {
                state.RoomVolume -= _walledVoxels * ShipRoomsConstants.GasVolumeCapacityPerVoxel;
                gases.SetRoomGasContent(room, state);
                return true;
            }

            LastPath = "in-room split";
            // R split: the open group (or else the largest one) keeps R, every other group becomes a new room
            GroupVolumes.Clear();
            Members.Clear();
            OpenRoots.Clear();
            for (int j = 0; j < Floods.Count; j++)
            {
                int r = Find(j);
                float v = 0f;
                foreach (ShipRoomsVoxelsTreeNode n in Floods[j].Nodes) v += n.Bounds * n.Bounds * n.Bounds;
                GroupVolumes[r] = GroupVolumes.TryGetValue(r, out float sum) ? sum + v : v;
                if (!Members.TryGetValue(r, out List<int> list)) Members[r] = list = new List<int>();
                list.Add(j);
                if (Floods[j].Frontier.Count > 0) OpenRoots.Add(r);
            }
            int keeper = -1;
            float keeperVolume = -1f;
            foreach (var kv in GroupVolumes)
            {
                if (OpenRoots.Contains(kv.Key))
                {
                    keeper = kv.Key;
                    keeperVolume = float.PositiveInfinity;
                    break;
                }
                if (kv.Value > keeperVolume || kv.Value == keeperVolume && kv.Key < keeper)
                {
                    keeper = kv.Key;
                    keeperVolume = kv.Value;
                }
            }
            float cutOff = 0f;
            for (int i = 0; i < Floods.Count; i++)
            {
                if (Find(i) != i || i == keeper) continue;
                ushort id = tree.RegisterNewRoomId();
                float volume = 0f;
                foreach (int j in Members[i])
                {
                    foreach (ShipRoomsVoxelsTreeNode n in Floods[j].Nodes)
                    {
                        n.RoomId = id;
                        volume += n.Bounds * n.Bounds * n.Bounds;
                    }
                }
                cutOff += volume;
                gases.SetRoomGasContent(id, new RoomGasState
                {
                    RoomVolume = volume * ShipRoomsConstants.GasVolumeCapacityPerVoxel,
                    RoomScanSeedCoord = Floods[i].Seed,
                    GasState = state.GasState,
                });
            }
            state.RoomVolume = float.IsPositiveInfinity(keeperVolume)
                ? state.RoomVolume - (cutOff + _walledVoxels) * ShipRoomsConstants.GasVolumeCapacityPerVoxel
                : keeperVolume * ShipRoomsConstants.GasVolumeCapacityPerVoxel;
            // the seed the game reloads the room's air from must still lie in the room
            ShipRoomsVoxelsTreeNode seedLeaf = tree.GetLeaf(state.RoomScanSeedCoord);
            if (seedLeaf.WallCount > 0 || seedLeaf.RoomId != room) state.RoomScanSeedCoord = Floods[keeper].Seed;
            gases.SetRoomGasContent(room, state);
            return true;
        }

        private static void Expand(List<ShipRoomsVoxelsTreeNode> neighbors, int flood, ushort room)
        {
            for (int i = 0; i < neighbors.Count; i++)
            {
                ShipRoomsVoxelsTreeNode n = neighbors[i];
                if (n.WallCount > 0 || n.RoomId != room) continue;
                if (Owner.TryGetValue(n, out int other))
                {
                    int a = Find(flood), b = Find(other);
                    if (a != b) Floods[Mathf.Max(a, b)].Parent = Mathf.Min(a, b);
                    continue;
                }
                Owner[n] = flood;
                Floods[flood].Nodes.Add(n);
                Floods[flood].Frontier.Push(n);
            }
        }

        private static int Find(int i)
        {
            while (Floods[i].Parent != i)
            {
                Floods[i].Parent = Floods[Floods[i].Parent].Parent;
                i = Floods[i].Parent;
            }
            return i;
        }

        /// <summary>Tree nodes by reference: the game's Equals and GetHashCode compare coordinates.</summary>
        internal sealed class NodeRef : IEqualityComparer<ShipRoomsVoxelsTreeNode>
        {
            public static readonly NodeRef Instance = new NodeRef();
            public bool Equals(ShipRoomsVoxelsTreeNode a, ShipRoomsVoxelsTreeNode b) => ReferenceEquals(a, b);
            public int GetHashCode(ShipRoomsVoxelsTreeNode n) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(n);
        }

        /// <summary>Unexpected seeds: clear room R now (its walls are in) and let the regular floods rebuild it.</summary>
        private static bool FallBack(ShipRoomsVoxelUpdater updater, ShipRoomsVoxelsTree tree, ushort room)
        {
            LastPath = "in-room fallback";
            ShipRoomsGases gases = GasesRef(updater);
            List<ShipRoomsVoxelsTreeNode> cleared = ClearedRef(updater);
            gases.TryGetRoomGasContent(room, out GasState gas);
            Plain.Clear();
            foreach (Vector3Int v in CreateSeedsRef(updater)) Plain.Push(tree.GetLeaf(v));
            foreach (Vector3Int v in ClearSeedsRef(updater)) Plain.Push(tree.GetLeaf(v));
            while (Plain.Count > 0)
            {
                ShipRoomsVoxelsTreeNode n = Plain.Pop();
                if (n.Visited || n.WallCount > 0 || n.RoomId != room) continue;
                n.Visited = true;
                cleared.Add(n);
                PushAll(n.NeighborsLeft, Plain);
                PushAll(n.NeighborsRight, Plain);
                PushAll(n.NeighborsBottom, Plain);
                PushAll(n.NeighborsTop, Plain);
                PushAll(n.NeighborsBack, Plain);
                PushAll(n.NeighborsForward, Plain);
            }
            foreach (ShipRoomsVoxelsTreeNode n in cleared)
            {
                n.TmpGasContent = gas;
                Known[n] = gas.GasDensity;
                n.RoomId = ShipRoomsConstants.NoRoomId;
                n.Visited = false;
            }
            PreviousRoomsRef(updater).Add(room);
            _inRoom = 0;
            Owner.Clear();
            return false;
        }

        // ------------------------------------------------------------------ panels removed

        /// <summary>
        /// Only panels removed (walls already out): the freed and merged leaves form small groups. A group that touches
        /// air of a single label (one room, or the outside) simply joins it, and a room gains the freed volume at its
        /// own density. A group joining two labels (a room opened to the outside, two rooms merged) or touching none
        /// returns false: the caller clears the regions involved and rebuilds them with the regular floods.
        /// </summary>
        private static bool UpdateRemoved(ShipRoomsVoxelUpdater updater, ShipRoomsVoxelsTree tree, ShipRoomsGases gases)
        {
            foreach (ShipRoomsVoxelsTreeNode n in Touched)
                if (n.WallCount == 0 && n.IsLeaf && tree.GetLeaf(n.Coord) == n) Live.Add(n);
            if (Live.Count == 0) return true;

            int groups = 0;
            foreach (ShipRoomsVoxelsTreeNode start in Live)
            {
                if (Owner.ContainsKey(start)) continue;
                Group.Clear();
                Plain.Clear();
                Plain.Push(start);
                Owner[start] = groups;
                int label = -1;
                while (Plain.Count > 0)
                {
                    ShipRoomsVoxelsTreeNode n = Plain.Pop();
                    Group.Add(n);
                    if (!Neighbours(n.NeighborsLeft, groups, ref label) || !Neighbours(n.NeighborsRight, groups, ref label)
                        || !Neighbours(n.NeighborsBottom, groups, ref label) || !Neighbours(n.NeighborsTop, groups, ref label)
                        || !Neighbours(n.NeighborsBack, groups, ref label) || !Neighbours(n.NeighborsForward, groups, ref label))
                        return FallBackRemoved(updater, tree);
                }
                if (label < 0 || (label != ShipRoomsConstants.NoRoomId && !gases.HasRoom((ushort)label))) return FallBackRemoved(updater, tree);
                foreach (ShipRoomsVoxelsTreeNode n in Group) n.RoomId = (ushort)label;
                groups++;
            }

            FreedPerRoom.Clear();
            foreach (Vector3Int c in Freed)
            {
                ShipRoomsVoxelsTreeNode leaf = tree.GetLeaf(c);
                if (leaf.WallCount > 0 || leaf.RoomId == ShipRoomsConstants.NoRoomId) continue;
                FreedPerRoom.TryGetValue(leaf.RoomId, out int k);
                FreedPerRoom[leaf.RoomId] = k + 1;
            }
            foreach (KeyValuePair<ushort, int> kv in FreedPerRoom)
            {
                gases.TryGetRoomGasState(kv.Key, out RoomGasState state);
                state.RoomVolume += kv.Value * ShipRoomsConstants.GasVolumeCapacityPerVoxel;
                gases.SetRoomGasContent(kv.Key, state);
            }
            return true;
        }

        /// <summary>Walks a group's neighbours: grows the group over touched leaves, else records the label met. False on a second label.</summary>
        private static bool Neighbours(List<ShipRoomsVoxelsTreeNode> neighbors, int group, ref int label)
        {
            for (int i = 0; i < neighbors.Count; i++)
            {
                ShipRoomsVoxelsTreeNode m = neighbors[i];
                if (m.WallCount > 0) continue;
                if (Live.Contains(m))
                {
                    if (!Owner.ContainsKey(m))
                    {
                        Owner[m] = group;
                        Plain.Push(m);
                    }
                    continue;
                }
                if (label < 0) label = m.RoomId;
                else if (label != m.RoomId) return false;
            }
            return true;
        }

        /// <summary>
        /// The game's clear, run after the walls came out: every room the freed air now joins is cleared (the outside
        /// keeps its label), the freed leaves lose their stale labels, and the regular floods relabel the lot.
        /// </summary>
        private static bool FallBackRemoved(ShipRoomsVoxelUpdater updater, ShipRoomsVoxelsTree tree)
        {
            LastPath = "removed fallback";
            ShipRoomsGases gases = GasesRef(updater);
            List<ShipRoomsVoxelsTreeNode> cleared = ClearedRef(updater);
            HashSet<ushort> previous = PreviousRoomsRef(updater);
            foreach (ShipRoomsVoxelsTreeNode n in Live) n.RoomId = ShipRoomsConstants.NoRoomId;
            Plain.Clear();
            foreach (ShipRoomsVoxelsTreeNode n in Live) Plain.Push(n);
            while (Plain.Count > 0)
            {
                ShipRoomsVoxelsTreeNode n = Plain.Pop();
                if (n.Visited || n.WallCount > 0) continue;
                bool live = Live.Contains(n);
                if (!live && n.RoomId == ShipRoomsConstants.NoRoomId) continue;
                n.Visited = true;
                cleared.Add(n);
                if (!live)
                {
                    previous.Add(n.RoomId);
                    n.TmpGasContent = gases.TryGetRoomGasContent(n.RoomId, out GasState g) ? g : new GasState { GasItemId = 0u, GasDensity = 0f };
                    Known[n] = n.TmpGasContent.GasDensity;
                }
                PushAll(n.NeighborsLeft, Plain);
                PushAll(n.NeighborsRight, Plain);
                PushAll(n.NeighborsBottom, Plain);
                PushAll(n.NeighborsTop, Plain);
                PushAll(n.NeighborsBack, Plain);
                PushAll(n.NeighborsForward, Plain);
            }
            foreach (ShipRoomsVoxelsTreeNode n in cleared)
            {
                n.RoomId = ShipRoomsConstants.NoRoomId;
                n.Visited = false;
            }
            _removing = false;
            Owner.Clear();
            return false;
        }

        private static void FloodFromSeed(ShipRoomsVoxelsTreeNode start, ShipRoomsVoxelsTree tree, ShipRoomsGases gases, List<ShipRoomsVoxelsTreeNode> allVisited,
            Vector3Int lower, Vector3Int upper, Vector3Int seed)
        {
            SeedNodes.Clear();
            ClearFrontier();
            Push(start);
            bool outside = false;
            while (!outside)
            {
                ShipRoomsVoxelsTreeNode n = Pop();
                if (n == null) break;
                if (n.WallCount > 0 || n.RoomId > ShipRoomsConstants.NoRoomId) continue;
                if (n.Visited)
                {
                    // part of an earlier flood of this update that stopped at the bounds: connected to the outside
                    if (Outside.Contains(n)) outside = true;
                    continue;
                }
                n.Visited = true;
                SeedNodes.Add(n);
                Vector3Int c = n.Coord;
                Vector3Int far = c + Vector3Int.one * (n.Bounds - 1);
                if (c.x <= lower.x || c.y <= lower.y || c.z <= lower.z || far.x >= upper.x || far.y >= upper.y || far.z >= upper.z)
                {
                    outside = true;
                    break;
                }
                PushAll(n.NeighborsTop);
                PushAll(n.NeighborsLeft);
                PushAll(n.NeighborsRight);
                PushAll(n.NeighborsBack);
                PushAll(n.NeighborsForward);
                PushAll(n.NeighborsBottom);
            }
            if (SeedNodes.Count == 0) return;

            if (outside)
            {
                foreach (ShipRoomsVoxelsTreeNode n in SeedNodes)
                {
                    n.RoomId = ShipRoomsConstants.NoRoomId;
                    Outside.Add(n);
                }
            }
            else
            {
                // Density: volume-weighted over the air whose state is known. The game counted every leaf split or merged
                // during the update as vacuum, which thinned a room's air each time a panel went in and made the air of a
                // freshly closed hull depend on how the octree happened to subdivide.
                ushort roomId = tree.RegisterNewRoomId();
                float volume = 0f, known = 0f, gas = 0f;
                foreach (ShipRoomsVoxelsTreeNode n in SeedNodes)
                {
                    float v = n.Bounds * n.Bounds * n.Bounds;
                    volume += v;
                    if (!Unknown.Contains(n))
                    {
                        known += v;
                        gas += (Known.TryGetValue(n, out float d) ? d : _atmosphere) * v;
                    }
                    n.RoomId = roomId;
                }
                gases.SetRoomGasContent(roomId, new RoomGasState
                {
                    RoomVolume = volume * ShipRoomsConstants.GasVolumeCapacityPerVoxel,
                    RoomScanSeedCoord = seed,
                    GasState = new GasState { GasItemId = ItemsConstants.OxygenItemId, GasDensity = known > 0f ? gas / known : 0f },
                });
            }
            allVisited.AddRange(SeedNodes);
        }

        private static void PushAll(List<ShipRoomsVoxelsTreeNode> list, Stack<ShipRoomsVoxelsTreeNode> to)
        {
            for (int i = 0; i < list.Count; i++) to.Push(list[i]);
        }

        private static void PushAll(List<ShipRoomsVoxelsTreeNode> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                ShipRoomsVoxelsTreeNode n = list[i];
                if (!n.Visited || Outside.Contains(n)) Push(n);
            }
        }

        private static void Push(ShipRoomsVoxelsTreeNode n)
        {
            int level = 0;
            for (int b = n.Bounds; b > 1 && level < 31; b >>= 1) level++;
            Frontier[level].Push(n);
            if (level > _top) _top = level;
        }

        private static ShipRoomsVoxelsTreeNode Pop()
        {
            while (_top >= 0)
            {
                if (Frontier[_top].Count > 0) return Frontier[_top].Pop();
                _top--;
            }
            return null;
        }

        private static void ClearFrontier()
        {
            for (int i = 0; i < Frontier.Length; i++) Frontier[i].Clear();
            _top = -1;
        }
    }
}
