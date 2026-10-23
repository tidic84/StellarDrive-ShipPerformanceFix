using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace ShipPerformanceFix
{
    /// <summary>
    /// TF Advanced's force field doors animate their sheet on the CPU: every vertex's colour is recomputed (exp, sin,
    /// pow) every frame for every door within 30 m, seen or not. A ship full of doors spends ~7 ms per frame there, and
    /// one painting of a 24 x 24 door takes ~5 ms.
    ///
    /// A door is painted only while its sheet is on screen, at most 30 times per second, and by a copy of the mod's
    /// Paint that gives the same colours: the sheet's two faces share their points, so each point is computed once,
    /// and the integer powers are multiplications. Only applied when TF Advanced is installed.
    /// </summary>
    internal static class FieldDoorThrottle
    {
        private const float Interval = 1f / 30f;

        private sealed class Last
        {
            public float Time = float.MinValue;
        }

        private static readonly ConditionalWeakTable<object, Last> LastPaint = new ConditionalWeakTable<object, Last>();
        private static FieldInfo _renderer, _mesh, _points, _colors, _width, _height, _rise, _touchAge, _touch;

        public static bool Enabled = true;
#if DEV
        public static int Painted, Skipped;
#endif

        internal static void Patch(HarmonyLib.Harmony harmony)
        {
            Type type = AccessTools.TypeByName("StellarDriveDemoTFAdvanced.Power.FieldDoorVisuals");
            if (type == null) return;
            MethodInfo paint = AccessTools.Method(type, "Paint", new[] { typeof(Color), typeof(bool), typeof(bool) });
            FieldInfo F(string name) => AccessTools.Field(type, name);
            _renderer = F("_fieldRenderer");
            _mesh = F("_mesh");
            _points = F("_points");
            _colors = F("_colors");
            _width = F("_builtWidth");
            _height = F("_builtHeight");
            _rise = F("_rise");
            _touchAge = F("_touchAge");
            _touch = F("_touch");
            if (paint == null || _renderer == null || _mesh == null || _points == null || _colors == null || _width == null
                || _height == null || _rise == null || _touchAge == null || _touch == null)
            {
                ShipPerfMod.Log.Warning("TF Advanced force field doors: unknown version, left as they are.");
                return;
            }
            harmony.Patch(paint, prefix: new HarmonyMethod(typeof(FieldDoorThrottle), nameof(Prefix)));
            ShipPerfMod.Log.Msg("TF Advanced force field doors: painted only on screen, 30 times per second at most.");
        }

        private static bool Prefix(object __instance, Color tint, bool outline, bool solid)
        {
            if (!Enabled) return true;
            var renderer = _renderer.GetValue(__instance) as Renderer;
            Last last = LastPaint.GetOrCreateValue(__instance);
            float now = Time.unscaledTime;
            if (renderer != null && (!renderer.isVisible || now - last.Time < Interval))
            {
#if DEV
                Skipped++;
#endif
                return false;
            }
            last.Time = now;
#if DEV
            Painted++;
#endif
            var mesh = _mesh.GetValue(__instance) as Mesh;
            var points = _points.GetValue(__instance) as Vector2[];
            var colors = _colors.GetValue(__instance) as Color32[];
            if (mesh == null || points == null) return false;
            int face = points.Length / 2;
            if (colors == null || colors.Length != points.Length || points.Length % 2 != 0 || points[0] != points[face] || points[face - 1] != points[points.Length - 1])
                return true;
            Paint(mesh, points, colors, (int)_width.GetValue(__instance), (int)_height.GetValue(__instance), (float)_rise.GetValue(__instance),
                (float)_touchAge.GetValue(__instance), (Vector2)_touch.GetValue(__instance), tint, outline, solid);
            return false;
        }

#if DEV
        /// <summary>Paints one door with the mod's Paint and with this one, and returns the largest alpha difference.</summary>
        public static int Check(object door, Color tint, bool outline, bool solid)
        {
            MethodInfo paint = AccessTools.Method(door.GetType(), "Paint");
            var mesh = (Mesh)_mesh.GetValue(door);
            var colors = (Color32[])_colors.GetValue(door);
            Enabled = false;
            paint.Invoke(door, new object[] { tint, outline, solid });
            Enabled = true;
            var original = (Color32[])colors.Clone();
            Paint(mesh, (Vector2[])_points.GetValue(door), colors, (int)_width.GetValue(door), (int)_height.GetValue(door), (float)_rise.GetValue(door),
                (float)_touchAge.GetValue(door), (Vector2)_touch.GetValue(door), tint, outline, solid);
            int worst = 0;
            for (int i = 0; i < colors.Length; i++)
                worst = Math.Max(worst, Math.Max(Math.Abs(colors[i].a - original[i].a), Math.Abs(colors[i].r - original[i].r)));
            return worst;
        }
#endif

        /// <summary>FieldDoorVisuals.Paint, computed once per point for both faces.</summary>
        private static void Paint(Mesh mesh, Vector2[] points, Color32[] colors, int builtWidth, int builtHeight, float rise,
            float touchAge, Vector2 touch, Color tint, bool outline, bool solid)
        {
            float time = Time.time;
            float halfWidth = builtWidth * 0.5f;
            float height = builtHeight;
            float risen = rise * height;
            float scan = Mathf.Repeat(time * 0.9f, height + 2f) - 1f;
            float ring = touchAge >= 0f ? touchAge * 3.2f : -10f;
            float ringFade = touchAge >= 0f ? 1f - touchAge / 1.4f : 0f;
            float pulse = 0.9f + 0.1f * Mathf.Sin(time * 2.4f);
            float baseGlow = solid ? 0.2f : 0.06f;
            float patternGlow = solid ? 0.3f : 0.1f;
            float outlinePulse = 0.85f * (0.75f + 0.25f * Mathf.Sin(time * 5f));
            var c = new Color32((byte)(Mathf.Clamp01(tint.r) * 255f), (byte)(Mathf.Clamp01(tint.g) * 255f), (byte)(Mathf.Clamp01(tint.b) * 255f), 0);
            int face = points.Length / 2;
            for (int i = 0; i < face; i++)
            {
                Vector2 p = points[i];
                float edge = Mathf.Exp(-Mathf.Min(Mathf.Min(p.x + halfWidth, halfWidth - p.x), Mathf.Min(p.y, height - p.y)) / 0.12f);
                float a;
                if (outline)
                {
                    a = edge * outlinePulse;
                }
                else
                {
                    float cells = Mathf.Abs(Mathf.Sin(p.x * 4.2f + p.y * 2.4f) * Mathf.Sin(p.x * 4.2f - p.y * 2.4f));
                    float cells2 = cells * cells;
                    cells = cells2 * cells2 * cells2 * (0.6f + 0.4f * Mathf.Sin(time * 1.3f + p.x * 0.7f + p.y * 0.5f));
                    float s = (p.y - scan) / 0.18f;
                    float scanLine = Mathf.Exp(-(s * s));
                    float wave = 0f;
                    if (ringFade > 0f)
                    {
                        float r = ((p - touch).magnitude - ring) / 0.22f;
                        wave = Mathf.Exp(-(r * r)) * ringFade;
                    }
                    a = (baseGlow + patternGlow * cells + 0.22f * scanLine + 0.75f * edge + 0.7f * wave) * pulse;
                    if (p.y > risen) a = 0f;
                    else if (rise < 1f) a += Mathf.Exp(-(risen - p.y) / 0.1f) * 0.8f;
                }
                c.a = (byte)(Mathf.Clamp01(a) * 255f);
                colors[i] = c;
                colors[i + face] = c;
            }
            mesh.colors32 = colors;
        }
    }
}
