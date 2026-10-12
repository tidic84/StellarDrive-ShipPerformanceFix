#if DEV
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using MelonLoader;
using Ships;
using Ships.Interface.Model;
using Ships.Interface.Model.Placement;
using Ships.Interface.Services;
using Ships.Interface.Settings;
using Ships.Interface.Values;
using Ships.Services;
using Stellar.Shared;
using UnityEngine;

namespace ShipPerformanceFix
{
    /// <summary>
    /// Dev commands (UserData/stellar_rooms_cmd.txt), plus timers on the room, hull mesh and collider updates:
    ///   bigship &lt;ship&gt; &lt;w&gt; &lt;h&gt; &lt;d&gt; [settings] [decks]   a closed hull box on the host
    ///   addpart / rmpart &lt;ship&gt; x y z [type]               one panel (addpart goes through the build request)
    ///   addbox &lt;ship&gt; &lt;type&gt; x0 y0 z0 x1 y1 z1             a range of panels
    ///   rooms / roomcheck &lt;ship&gt;                          dump the rooms / check their invariants (host and client)
    ///   fuzz &lt;ship&gt; &lt;steps&gt; [seed]                       random panels in and around the bigship box, checked each step
    ///   vacuum &lt;ship&gt;                                     empty every room
    ///   roomsfast on|off, prof on [ms]|off, spikes &lt;ms&gt;
    /// </summary>
    internal static class RoomsDev
    {
        private static DevHarness _harness;

        public static void Init(HarmonyLib.Harmony h, MelonLogger.Instance log)
        {
            _harness = new DevHarness("rooms", log, autoLaunch: false);
            _harness.Command("prof", a => ShipProf.Command(a, h, log));
            _harness.Command("spikes", a => _spikeMs = a.Length > 1 ? float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture) : 0f);
            _harness.Command("bigship", a => BigShip(log, a));
            _harness.Command("roomsfast", a => log.Msg($"[DEV] rooms fast path {(RoomsFastPath.Enabled = a.Length < 2 || a[1] != "off")}"));
            _harness.Command("rooms", a =>
            {
                uint id = uint.Parse(a[1]);
                var server = Core.Services.ServiceLocator.GetService<ShipsServerTracker>()?.GetShip(id);
                var client = Core.Services.ServiceLocator.GetService<ShipsClientTracker>()?.Ships.FirstOrDefault(s => s.Id == id);
                void Dump(string side, Ships.Rooms.ShipRooms rooms)
                {
                    if (rooms == null) return;
                    var list = rooms.RoomGases.OrderBy(r => r.RoomScanSeedCoord.x).ThenBy(r => r.RoomScanSeedCoord.y).ThenBy(r => r.RoomScanSeedCoord.z).ToList();
                    log.Msg($"[DEV] rooms {side}: {list.Count}, leaves {rooms.Tree.GetLeaves().Length}");
                    foreach (var r in list)
                        log.Msg($"[DEV]   volume {r.RoomVolume:F1} density {r.GasState.GasDensity:F4}");
                }
                Dump("server", server == null ? null : Traverse.Create(server).Property("Rooms").GetValue<Ships.Rooms.ShipRooms>());
                Dump("client", client == null ? null : Traverse.Create(client).Property("Rooms").GetValue<Ships.Rooms.ShipRooms>());
            });
            _harness.Command("addpart", a =>
            {
                // addpart <ship> <x> <y> <z> [type 0 floor|1 forward wall|2 right wall]: one panel, through the same request as the build tool
                int I(int i) => int.Parse(a[i]);
                foreach (var p in Core.Services.ServiceLocator.GetService<Players.Services.PlayersServerTracker>().ActivePlayers)
                    if (!p.IsUsingCheatFreeBuild) Traverse.Create(p).Property("NetworkState").Method("ServerSetUsingCheatFreeBuild", true).GetValue();
                var service = Core.Services.ServiceLocator.GetService<Managers.Interface.Services.IShipInteractionNetworkService>();
                service.PlaceHullPart(new Managers.Interface.Model.PlaceHullPartNetworkRequest
                {
                    ShipId = uint.Parse(a[1]),
                    SettingsId = 70,
                    Placement = Panel(new Vector3Int(I(2), I(3), I(4)), a.Length > 5 ? (VoxelPartType)I(5) : VoxelPartType.Floor),
                });
                log.Msg($"[DEV] addpart sent at {I(2)},{I(3)},{I(4)}");
            });
            _harness.Command("roomcheck", a =>
            {
                // roomcheck <ship>: invariants of the host's and the client's rooms (labels, volumes, gas records)
                uint id = uint.Parse(a[1]);
                var server = Core.Services.ServiceLocator.GetService<ShipsServerTracker>()?.GetShip(id);
                var client = Core.Services.ServiceLocator.GetService<ShipsClientTracker>()?.Ships.FirstOrDefault(s => s.Id == id);
                if (server != null) log.Msg("[DEV] roomcheck server: " + RoomCheck(Traverse.Create(server).Property("Rooms").GetValue<Ships.Rooms.ShipRooms>()));
                if (client != null) log.Msg("[DEV] roomcheck client: " + RoomCheck(Traverse.Create(client).Property("Rooms").GetValue<Ships.Rooms.ShipRooms>()));
            });
            _harness.Command("fuzz", a => MelonCoroutines.Start(Fuzz(log, uint.Parse(a[1]), int.Parse(a[2]), a.Length > 3 ? int.Parse(a[3]) : 1)));
            _harness.Command("vacuum", a =>
            {
                // vacuum <ship>: empties every room of the ship on the host
                var server = Core.Services.ServiceLocator.GetService<ShipsServerTracker>()?.GetShip(uint.Parse(a[1]));
                var rooms = server == null ? null : Traverse.Create(server).Property("Rooms").GetValue<Ships.Rooms.ShipRooms>();
                if (rooms == null) return;
                for (ushort id = 1; id < 64; id++)
                    if (rooms.TryGetRoomGasState(id, out var g))
                        rooms.UpdateRoomGasState(id, g with { GasState = new Ships.Interface.Model.GasState { GasItemId = g.GasState.GasItemId, GasDensity = 0f } });
                log.Msg("[DEV] rooms emptied");
            });
            _harness.Command("rmpart", a =>
            {
                // rmpart <ship> <x> <y> <z> [type]: removes one panel on the host
                int I(int i) => int.Parse(a[i]);
                var ship = Core.Services.ServiceLocator.GetService<ShipsServerTracker>()?.GetShip(uint.Parse(a[1]));
                ship?.RemoveHullPart(new HullPartPosition(new Vector3Int(I(2), I(3), I(4)), a.Length > 5 ? (VoxelPartType)I(5) : VoxelPartType.Floor));
                log.Msg($"[DEV] rmpart at {I(2)},{I(3)},{I(4)} -> {ship?.PartCount} parts");
            });
            _harness.Command("addbox", a =>
            {
                // addbox <ship> <type> <x0> <y0> <z0> <x1> <y1> <z1>: fills a range of panels of one type on the host
                int I(int i) => int.Parse(a[i]);
                var ship = Core.Services.ServiceLocator.GetService<ShipsServerTracker>()?.GetShip(uint.Parse(a[1]));
                if (ship == null) return;
                var settings = (HullPartSettings)new PartSettingsProvider().GetPartSettingById(70);
                var type = (VoxelPartType)I(2);
                for (int x = I(3); x <= I(6); x++)
                for (int y = I(4); y <= I(7); y++)
                for (int z = I(5); z <= I(8); z++)
                    if (!ship.State.ContainsHullPart(new HullPartPosition(new Vector3Int(x, y, z), type)))
                        ship.AddHullPart(settings, Panel(new Vector3Int(x, y, z), type));
                log.Msg($"[DEV] addbox -> {ship.PartCount} parts");
            });
            h.Patch(AccessTools.Method(typeof(Ships.Rooms.ShipRoomsVoxelUpdater), "UpdateRooms"),
                prefix: new HarmonyMethod(typeof(RoomsDev), nameof(RoomsStart)), postfix: new HarmonyMethod(typeof(RoomsDev), nameof(RoomsStop)));
            foreach (var m in new[] { AccessTools.Method(typeof(Ships.Visuals.ShipHullVisuals), "UpdateHull"), AccessTools.Method(typeof(Ships.Physics.Collision.ShipHullColliders), "UpdateColliders") })
                h.Patch(m, prefix: new HarmonyMethod(typeof(RoomsDev), nameof(TimedStart)), postfix: new HarmonyMethod(typeof(RoomsDev), nameof(TimedStop)));
        }

        private static float _spikeMs;

        public static void Update()
        {
            _harness?.Update();
            if (_spikeMs > 0f && Time.unscaledDeltaTime * 1000f > _spikeMs)
                ShipPerfMod.Log.Msg($"[DEV] spike frame {Time.frameCount}: {Time.unscaledDeltaTime * 1000f:F1} ms");
        }

        /// <summary>
        /// bigship &lt;ship&gt; &lt;w&gt; &lt;h&gt; &lt;d&gt; [settings] [decks]: a closed hull box of w x h x d voxels added on the host
        /// next to the ship's origin, with intermediate decks every h / (decks + 1) voxels.
        /// </summary>
        private static void BigShip(MelonLogger.Instance log, string[] a)
        {
            var ships = Core.Services.ServiceLocator.GetService<ShipsServerTracker>();
            TrackedShipServer ship = ships?.GetShip(uint.Parse(a[1]));
            if (ship == null) return;
            int w = int.Parse(a[2]), hgt = int.Parse(a[3]), d = int.Parse(a[4]);
            ushort id = a.Length > 5 ? ushort.Parse(a[5]) : (ushort)70;
            int decks = a.Length > 6 ? int.Parse(a[6]) : 0;
            var settings = (HullPartSettings)new PartSettingsProvider().GetPartSettingById(id);
            const int ox = 2, oy = 2, oz = 2;
            int n = 0;
            void Add(int x, int y, int z, VoxelPartType type, PartOrientation fwd, PartOrientation up)
            {
                var pos = new Vector3Int(ox + x, oy + y, oz + z);
                if (ship.State.ContainsHullPart(new HullPartPosition(pos, type))) return;
                ship.AddHullPart(settings, new HullPartPlacement(pos, type, fwd, up));
                n++;
            }
            var floors = new List<int> { 0, hgt };
            for (int k = 1; k <= decks; k++) floors.Add(hgt * k / (decks + 1));
            foreach (int y in floors)
                for (int x = 0; x < w; x++)
                for (int z = 0; z < d; z++)
                    Add(x, y, z, VoxelPartType.Floor, PartOrientation.Up, PartOrientation.Forward);
            for (int y = 0; y < hgt; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Add(x, y, -1, VoxelPartType.ForwardWall, PartOrientation.Forward, PartOrientation.Up);
                    Add(x, y, d - 1, VoxelPartType.ForwardWall, PartOrientation.Forward, PartOrientation.Up);
                }
                for (int z = 0; z < d; z++)
                {
                    Add(-1, y, z, VoxelPartType.RightWall, PartOrientation.Right, PartOrientation.Up);
                    Add(w - 1, y, z, VoxelPartType.RightWall, PartOrientation.Right, PartOrientation.Up);
                }
            }
            log.Msg($"[DEV] bigship added {n} panels, ship {ship.Id} now {ship.PartCount} parts");
        }

        private static string RoomCheck(Ships.Rooms.ShipRooms rooms)
        {
            var tree = rooms.Tree;
            var leaves = tree.GetLeaves();
            Vector3Int lo = tree.LowerBounds, hi = tree.UpperBounds;
            var volume = new Dictionary<ushort, float>();
            int mismatch = 0, boundary = 0;
            foreach (var n in leaves)
            {
                if (n.WallCount > 0) continue;
                if (n.RoomId != 0)
                {
                    volume.TryGetValue(n.RoomId, out float v);
                    volume[n.RoomId] = v + n.Bounds * n.Bounds * n.Bounds;
                    Vector3Int c = n.Coord, far = c + Vector3Int.one * (n.Bounds - 1);
                    if (c.x <= lo.x || c.y <= lo.y || c.z <= lo.z || far.x >= hi.x || far.y >= hi.y || far.z >= hi.z) boundary++;
                }
                foreach (var list in new[] { n.NeighborsLeft, n.NeighborsRight, n.NeighborsBottom, n.NeighborsTop, n.NeighborsBack, n.NeighborsForward })
                    foreach (var m in list)
                        if (m.WallCount == 0 && m.RoomId != n.RoomId) mismatch++;
            }
            int badVolume = 0, orphanGas = 0, noGas = 0;
            foreach (var kv in volume)
            {
                if (!rooms.TryGetRoomGasState(kv.Key, out var g)) { noGas++; continue; }
                if (Mathf.Abs(g.RoomVolume - kv.Value * ShipRoomsConstants.GasVolumeCapacityPerVoxel) > 0.01f) badVolume++;
            }
            for (ushort r = 1; r < 256; r++)
                if (rooms.TryGetRoomGasState(r, out _) && !volume.ContainsKey(r)) orphanGas++;
            bool ok = mismatch == 0 && boundary == 0 && badVolume == 0 && orphanGas == 0 && noGas == 0;
            return $"{(ok ? "OK" : "FAIL")} rooms={volume.Count} leaves={leaves.Length} mismatch={mismatch} boundary={boundary} badVolume={badVolume} orphanGas={orphanGas} noGas={noGas}";
        }

        /// <summary>fuzz &lt;ship&gt; &lt;steps&gt; [seed]: random panels added and removed in and around the bigship box, checking rooms after each.</summary>
        private static System.Collections.IEnumerator Fuzz(MelonLogger.Instance log, uint shipId, int steps, int seed)
        {
            var rng = new System.Random(seed);
            var ship = Core.Services.ServiceLocator.GetService<ShipsServerTracker>()?.GetShip(shipId);
            if (ship == null) yield break;
            var settings = (HullPartSettings)new PartSettingsProvider().GetPartSettingById(70);
            var rooms = Traverse.Create(ship).Property("Rooms").GetValue<Ships.Rooms.ShipRooms>();
            int fails = 0;
            var added = new List<HullPartPosition>();
            HullPartPosition? opened = null;
            for (int s = 0; s < steps; s++)
            {
                if (opened.HasValue)
                {
                    ship.AddHullPart(settings, Panel(opened.Value.VoxelCoord, opened.Value.Type));
                    opened = null;
                }
                else if (rng.Next(8) == 0)
                {
                    // open the hull for one step: a wall panel of the box (x = 21 side, both decks)
                    var hp = new HullPartPosition(new Vector3Int(21, rng.Next(2, 12), rng.Next(2, 22)), VoxelPartType.RightWall);
                    if (ship.State.ContainsHullPart(hp))
                    {
                        ship.RemoveHullPart(hp);
                        opened = hp;
                    }
                }
                int count = rng.Next(4) == 0 ? rng.Next(2, 12) : 1; // sometimes a burst in one tick, like a zoop
                for (int k = 0; k < count; k++)
                {
                    if (added.Count > 0 && rng.Next(3) == 0)
                    {
                        int i = rng.Next(added.Count);
                        ship.RemoveHullPart(added[i]);
                        added.RemoveAt(i);
                        continue;
                    }
                    var type = (VoxelPartType)rng.Next(3);
                    bool inside = rng.Next(4) != 0;
                    var pos = inside ? new Vector3Int(rng.Next(2, 21), rng.Next(3, 12), rng.Next(2, 21)) : new Vector3Int(rng.Next(22, 26), rng.Next(1, 14), rng.Next(0, 24));
                    var hp = new HullPartPosition(pos, type);
                    if (ship.State.ContainsHullPart(hp)) continue;
                    ship.AddHullPart(settings, Panel(pos, type));
                    added.Add(hp);
                }
                yield return null;
                yield return null;
                yield return null;
                string r = RoomCheck(rooms);
                if (!r.StartsWith("OK")) fails++;
                if (!r.StartsWith("OK") || s % 10 == 0) log.Msg($"[DEV] fuzz {s}: {r}");
            }
            log.Msg($"[DEV] fuzz done steps={steps} fails={fails}");
        }

        private static HullPartPlacement Panel(Vector3Int pos, VoxelPartType type) => type switch
        {
            VoxelPartType.ForwardWall => new HullPartPlacement(pos, type, PartOrientation.Forward, PartOrientation.Up),
            VoxelPartType.RightWall => new HullPartPlacement(pos, type, PartOrientation.Right, PartOrientation.Up),
            _ => new HullPartPlacement(pos, VoxelPartType.Floor, PartOrientation.Up, PartOrientation.Forward),
        };

        private static void TimedStart(out long __state) => __state = Stopwatch.GetTimestamp();

        private static void TimedStop(long __state, System.Reflection.MethodBase __originalMethod)
        {
            double ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
            if (ms > 0.5) ShipPerfMod.Log.Msg($"[DEV] {__originalMethod.DeclaringType.Name}.{__originalMethod.Name} {ms:F2} ms");
        }

        private static readonly Stopwatch RoomsWatch = new Stopwatch();

        private static void RoomsStart() => RoomsWatch.Restart();

        private static void RoomsStop()
        {
            RoomsWatch.Stop();
            if (RoomsWatch.Elapsed.TotalMilliseconds > 0.5)
                ShipPerfMod.Log.Msg($"[DEV] UpdateRooms {RoomsWatch.Elapsed.TotalMilliseconds:F2} ms ({(RoomsFastPath.Enabled ? RoomsFastPath.LastPath : "original")})");
        }
    }
}
#endif
