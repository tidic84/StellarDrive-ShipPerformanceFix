#if DEV
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MelonLoader;
using UnityEngine;
using UnityEngine.LowLevel;

namespace ShipPerformanceFix
{
    /// <summary>
    /// Dev frame breakdown: a timestamp before every player loop system, so each system's time is known even in a
    /// release player (physics, scripts, culling and rendering on the main thread).
    ///   frame [frames]   average the next frames (default 300) and log the heaviest systems
    ///   scene            counts of what the scene holds (renderers, colliders, rigidbodies, per ship)
    /// </summary>
    internal static class FrameProf
    {
        private struct Marker { }

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly double TickMs = 1000.0 / Stopwatch.Frequency;
        private static string[] _names;
        private static double[] _total;
        private static int _last = -1, _framesLeft, _frames, _fixedSteps, _maxFixed;
        private static long _lastTick, _frameStart;
        private static double _worstFrame, _frameSum;
        private static bool _installed;
        private static MelonLogger.Instance _log;
        private static double _watchMs;
        private static float _nextReport;

        private static double _physMs;
        private static int _physCalls;
        private static long _physStart;

        public static void PhysStart() => _physStart = Clock.ElapsedTicks;

        public static void PhysStop()
        {
            if (_framesLeft <= 0) return;
            _physMs += (Clock.ElapsedTicks - _physStart) * TickMs;
            _physCalls++;
        }

        /// <summary>meshcols on|off: enables or disables the mesh colliders (corners, slope corners) of every ship.</summary>
        public static void MeshColliders(string[] a, MelonLogger.Instance log)
        {
            bool on = a.Length > 1 && a[1] == "on";
            int n = 0;
            foreach (var c in UnityEngine.Object.FindObjectsByType<MeshCollider>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (c.GetComponentInParent<Ships.Physics.Collision.ShipHullColliderChunk>() != null) { c.enabled = on; n++; }
            log.Msg($"[FRAME] {n} ship mesh colliders {(on ? "on" : "off")}");
        }

        private static readonly Dictionary<System.Reflection.MethodBase, double> UpdateMs = new Dictionary<System.Reflection.MethodBase, double>();
        private static bool _updatesPatched;

        /// <summary>Times every Update / LateUpdate / FixedUpdate of every MonoBehaviour and every mod's OnUpdate (inclusive).</summary>
        private static void PatchUpdates(HarmonyLib.Harmony h)
        {
            _updatesPatched = true;
            var pre = new HarmonyLib.HarmonyMethod(typeof(FrameProf), nameof(UpdStart));
            var post = new HarmonyLib.HarmonyMethod(typeof(FrameProf), nameof(UpdStop));
            string[] names = { "Update", "LateUpdate", "FixedUpdate", "OnUpdate", "OnLateUpdate", "OnFixedUpdate", "OnGUI" };
            int n = 0;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string an = asm.GetName().Name;
                if (an.StartsWith("Unity") || an.StartsWith("System") || an == "mscorlib" || an.StartsWith("Mono") || an == "0Harmony" || an == "MelonLoader") continue;
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                catch { continue; }
                foreach (Type t in types)
                {
                    if (t.IsGenericTypeDefinition || !(typeof(MonoBehaviour).IsAssignableFrom(t) || typeof(MelonBase).IsAssignableFrom(t))) continue;
                    foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
                    {
                        if (!names.Contains(m.Name) || m.GetParameters().Length > 0 || m.IsAbstract || m.GetMethodBody() == null) continue;
                        if (m.DeclaringType == typeof(ShipPerfMod)) continue;
                        try { h.Patch(m, pre, post); UpdateMs[m] = 0; n++; }
                        catch { }
                    }
                }
            }
            foreach (string tn in new[] { "Managers.Server.WorldPhysicsServerUpdater", "Managers.Client.WorldPhysicsClientUpdater", "Managers.Client.ClientVisualsUpdater" })
                foreach (var m in HarmonyLib.AccessTools.TypeByName(tn).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    if (m.IsAbstract || m.ContainsGenericParameters || m.GetMethodBody() == null || m.Name.Contains("<") || m.Name.StartsWith("Rpc") || m.Name.StartsWith("NetworkInitialize") || UpdateMs.ContainsKey(m)) continue;
                    try { h.Patch(m, pre, post); UpdateMs[m] = 0; n++; }
                    catch { }
                }
            _log.Msg($"[FRAME] timing {n} update methods");
        }

        private static void UpdStart(out long __state) => __state = Clock.ElapsedTicks;

        private static void UpdStop(long __state, System.Reflection.MethodBase __originalMethod)
        {
            if (_framesLeft <= 0) return;
            UpdateMs[__originalMethod] = UpdateMs.TryGetValue(__originalMethod, out double v) ? v + (Clock.ElapsedTicks - __state) * TickMs : 0;
        }

        public static void Command(string[] a, MelonLogger.Instance log, HarmonyLib.Harmony h)
        {
            _log = log;
            if (!_installed) Install();
            _watchMs = a.Length > 1 && a[1] == "watch" ? (a.Length > 2 ? double.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture) : 30) : 0;
            if ((_watchMs > 0 || a.Length > 2 && a[2] == "updates") && !_updatesPatched) PatchUpdates(h);
            Reset();
            if (_watchMs <= 0) _framesLeft = a.Length > 1 && a[1] != "off" ? int.Parse(a[1]) : a.Length > 1 ? 0 : 300;
            log.Msg($"[FRAME] measuring {(_watchMs > 0 ? $"(watch, windows over {_watchMs} ms)" : _framesLeft + " frames")} over {_names.Length} systems");
        }

        private static void Reset()
        {
            Array.Clear(_total, 0, _total.Length);
            foreach (var k in UpdateMs.Keys.ToList()) UpdateMs[k] = 0;
            _frames = _fixedSteps = _maxFixed = 0;
            _worstFrame = _frameSum = _physMs = 0;
            _physCalls = 0;
            ClientShipPose.Written = ClientShipPose.Skipped = 0;
            _last = -1;
            _frameStart = 0;
            if (_watchMs > 0) _framesLeft = 60;
        }

        private static void Install()
        {
            _installed = true;
            var names = new List<string>();
            PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
            var phases = root.subSystemList;
            for (int p = 0; p < phases.Length; p++)
            {
                var subs = phases[p].subSystemList ?? Array.Empty<PlayerLoopSystem>();
                var list = new List<PlayerLoopSystem>();
                foreach (var s in subs)
                {
                    list.Add(MarkerSystem(names.Count));
                    names.Add(phases[p].type.Name + "." + (s.type?.Name ?? "?"));
                    list.Add(s);
                }
                // closes the last system of the phase
                list.Add(MarkerSystem(names.Count));
                names.Add(phases[p].type.Name + ".<end>");
                phases[p].subSystemList = list.ToArray();
            }
            root.subSystemList = phases;
            PlayerLoop.SetPlayerLoop(root);
            _names = names.ToArray();
            _total = new double[_names.Length];
        }

        private static PlayerLoopSystem MarkerSystem(int index) => new PlayerLoopSystem
        {
            type = typeof(Marker),
            updateDelegate = () => Mark(index),
        };

        private static void Mark(int index)
        {
            if (_framesLeft <= 0) return;
            long now = Clock.ElapsedTicks;
            if (_last >= 0) _total[_last] += (now - _lastTick) * TickMs;
            if (index == 0)
            {
                // first system of the loop: a new frame starts
                if (_frameStart > 0 && _last >= 0)
                {
                    double ms = (now - _frameStart) * TickMs;
                    _frameSum += ms;
                    _worstFrame = Math.Max(_worstFrame, ms);
                    _frames++;
                    if (--_framesLeft == 0)
                    {
                        if (_watchMs <= 0 || (_frameSum / _frames > _watchMs && Time.realtimeSinceStartup >= _nextReport))
                        {
                            Report();
                            _nextReport = Time.realtimeSinceStartup + 5f;
                        }
                        if (_watchMs <= 0) return;
                        Reset();
                    }
                }
                _frameStart = now;
            }
            if (_names[index] == "FixedUpdate.PhysicsFixedUpdate") _fixedSteps++;
            _last = index;
            _lastTick = now;
        }

        private static void Report()
        {
            _log.Msg($"[FRAME] {_frames} frames: {_frameSum / _frames:F2} ms avg ({1000.0 * _frames / _frameSum:F1} fps), worst {_worstFrame:F1} ms, physics steps {(double)_fixedSteps / _frames:F2}/frame, game SimulatePhysics {_physMs / _frames:F2} ms/frame ({(double)_physCalls / _frames:F2} calls), client ship poses written {ClientShipPose.Written} skipped {ClientShipPose.Skipped}");
            foreach (int i in Enumerable.Range(0, _names.Length).OrderByDescending(i => _total[i]).Take(8))
                _log.Msg($"[FRAME]   {_total[i] / _frames,7:F2} ms  {_names[i]}");
            foreach (var kv in UpdateMs.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).Take(25))
                _log.Msg($"[FRAME]   upd {kv.Value / _frames,7:F3} ms  {kv.Key.DeclaringType.Assembly.GetName().Name}: {kv.Key.DeclaringType.FullName}.{kv.Key.Name}");
        }

        public static void Scene(MelonLogger.Instance log)
        {
            var renderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
            var colliders = UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsSortMode.None);
            var bodies = UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsSortMode.None);
            var behaviours = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
            log.Msg($"[SCENE] renderers {renderers.Length} ({renderers.Count(r => r.enabled && r.gameObject.activeInHierarchy)} active, {renderers.Count(r => r.isVisible)} visible), colliders {colliders.Length} ({colliders.Count(c => c.enabled && c.gameObject.activeInHierarchy)} active), rigidbodies {bodies.Length} ({bodies.Count(b => !b.IsSleeping() && !b.isKinematic)} awake dynamic), behaviours {behaviours.Length}, gameobjects {UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Length}");
            log.Msg($"[SCENE] fixedDeltaTime {Time.fixedDeltaTime}, maximumDeltaTime {Time.maximumDeltaTime}, vSync {QualitySettings.vSyncCount}, targetFrameRate {Application.targetFrameRate}, solverIterations {Physics.defaultSolverIterations}");
            foreach (var g in behaviours.Where(b => b.isActiveAndEnabled).GroupBy(b => b.GetType().FullName).OrderByDescending(g => g.Count()).Take(15))
                log.Msg($"[SCENE]   {g.Count(),6} x {g.Key}");
            foreach (var b in bodies.OrderByDescending(b => b.GetComponentsInChildren<Collider>().Length).Take(6))
            {
                var cs = b.GetComponentsInChildren<Collider>();
                var rs = b.GetComponentsInChildren<Renderer>();
                long tris = b.GetComponentsInChildren<MeshFilter>().Where(f => f.sharedMesh != null).Sum(f => Enumerable.Range(0, f.sharedMesh.subMeshCount).Sum(i => (long)f.sharedMesh.GetIndexCount(i)) / 3);
                log.Msg($"[SCENE] body {b.name}: kinematic {b.isKinematic}, sleeping {b.IsSleeping()}, colliders {cs.Length} (box {cs.Count(c => c is BoxCollider)}, mesh {cs.Count(c => c is MeshCollider)}), renderers {rs.Length}, triangles {tris}");
            }
        }
    }
}
#endif
