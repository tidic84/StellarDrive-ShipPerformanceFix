#if DEV
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace StellarRooms
{
    /// <summary>
    /// Dev profiler for the game's ship code: times every method of the Ships assemblies and the world updaters
    /// (self and inclusive time) and logs the heaviest ones of any frame slower than a threshold.
    ///   prof on [ms]   patch (first time) and start logging frames slower than ms (default 30)
    ///   prof off       stop logging
    /// </summary>
    internal static class ShipProf
    {
        private sealed class Stat
        {
            public string Name;
            public double Self, Incl;
            public int Calls;
        }

        private struct Frame
        {
            public Stat Stat;
            public long Start, Child;
        }

        private static readonly Dictionary<MethodBase, Stat> Stats = new Dictionary<MethodBase, Stat>();
        private static readonly List<Stat> Touched = new List<Stat>();
        private static readonly Stack<Frame> Stack = new Stack<Frame>();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly double TickMs = 1000.0 / Stopwatch.Frequency;
        private static bool _patched, _on;
        private static double _thresholdMs = 30;
        private static int _mainThread, _frame = -1;
        private static long _frameStart;
        private static MelonLogger.Instance _log;

        public static void Command(string[] a, HarmonyLib.Harmony h, MelonLogger.Instance log)
        {
            _log = log;
            _mainThread = Thread.CurrentThread.ManagedThreadId;
            if (a.Length > 1 && a[1] == "off")
            {
                _on = false;
                return;
            }
            if (a.Length > 2) _thresholdMs = double.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
            if (!_patched) PatchAll(h);
            _on = true;
            log.Msg($"[PROF] on, frames over {_thresholdMs} ms, {Stats.Count} methods");
        }

        private static void PatchAll(HarmonyLib.Harmony h)
        {
            _patched = true;
            string[] assemblies = { "Ships", "Ships.Interface", "Tools", "Assembly-CSharp", "WorldObjects", "Players" };
            var prefix = new HarmonyMethod(typeof(ShipProf), nameof(Enter));
            var postfix = new HarmonyMethod(typeof(ShipProf), nameof(Exit));
            var finalizer = new HarmonyMethod(typeof(ShipProf), nameof(Fin));
            int failed = 0;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies().Where(x => assemblies.Contains(x.GetName().Name)))
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                foreach (Type t in types)
                {
                    if (t.IsInterface || t.IsGenericTypeDefinition || t.ContainsGenericParameters) continue;
                    string ns = t.Namespace ?? "";
                    if (ns.StartsWith("FishNet") || t.Name.Contains("<")) continue;
                    if (asm.GetName().Name == "Assembly-CSharp" && !ns.StartsWith("Managers")) continue;
                    foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (m.IsAbstract || m.IsGenericMethodDefinition || m.ContainsGenericParameters || m.IsSpecialName) continue;
                        if (m.Name.Contains("<") || m.Name.StartsWith("RpcWriter") || m.Name.StartsWith("RpcReader")) continue;
                        MethodBody body = m.GetMethodBody();
                        if (body == null || body.GetILAsByteArray().Length < 24) continue;
                        try
                        {
                            Stats[m] = new Stat { Name = t.Name + "." + m.Name };
                            h.Patch(m, prefix, postfix, finalizer: finalizer);
                        }
                        catch
                        {
                            Stats.Remove(m);
                            failed++;
                        }
                    }
                }
            }
            _log.Msg($"[PROF] patched {Stats.Count} methods ({failed} failed)");
        }

        private static void Enter(MethodBase __originalMethod)
        {
            if (!_on || Thread.CurrentThread.ManagedThreadId != _mainThread) return;
            Roll();
            if (!Stats.TryGetValue(__originalMethod, out Stat s)) return;
            Stack.Push(new Frame { Stat = s, Start = Clock.ElapsedTicks });
        }

        private static void Exit(MethodBase __originalMethod)
        {
            if (!_on || Thread.CurrentThread.ManagedThreadId != _mainThread || Stack.Count == 0) return;
            Frame f = Stack.Pop();
            if (!Stats.TryGetValue(__originalMethod, out Stat s) || s != f.Stat)
            {
                Stack.Clear(); // unbalanced (exception path or late enable): drop the stack
                return;
            }
            long incl = Clock.ElapsedTicks - f.Start;
            if (s.Calls == 0 && s.Incl == 0) Touched.Add(s);
            s.Calls++;
            s.Incl += incl * TickMs;
            s.Self += (incl - f.Child) * TickMs;
            if (Stack.Count > 0)
            {
                Frame parent = Stack.Pop();
                parent.Child += incl;
                Stack.Push(parent);
            }
        }

        private static Exception Fin(MethodBase __originalMethod, Exception __exception)
        {
            if (__exception != null) Stack.Clear();
            return __exception;
        }

        /// <summary>On the first call of a new frame: report the previous one if it was slow, then reset.</summary>
        private static void Roll()
        {
            int frame = Time.frameCount;
            if (frame == _frame) return;
            long now = Clock.ElapsedTicks;
            double frameMs = (now - _frameStart) * TickMs;
            if (_frame >= 0 && frameMs > _thresholdMs && Touched.Count > 0)
            {
                double measured = Touched.Sum(x => x.Self);
                _log.Msg($"[PROF] frame {_frame}: {frameMs:F1} ms, {measured:F1} ms in game ship code");
                foreach (Stat s in Touched.OrderByDescending(x => x.Self).Take(18))
                    _log.Msg($"[PROF]   self {s.Self,8:F2}  incl {s.Incl,8:F2}  x{s.Calls,-6} {s.Name}");
            }
            foreach (Stat s in Touched) { s.Self = s.Incl = 0; s.Calls = 0; }
            Touched.Clear();
            Stack.Clear();
            _frame = frame;
            _frameStart = now;
        }
    }
}
#endif
