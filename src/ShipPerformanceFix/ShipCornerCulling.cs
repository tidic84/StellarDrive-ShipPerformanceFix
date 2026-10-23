using System.Collections.Generic;
using HarmonyLib;
using Ships;
using Ships.Grid.Model;
using Ships.Physics;
using Ships.Physics.Collision;
using Ships.Services;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShipPerformanceFix
{
    /// <summary>
    /// On the host, a ship's exterior colliders (the ones on its rigidbody, which meet the ground and other ships) hold
    /// one convex collider per corner and slope-corner panel: 22,000 on a big ship, against ~5,000 merged flat boxes.
    /// PhysX pays for every shape of a moving body each tick, touching something or not: with the corners the host
    /// physics of two big ships costs ~7-14 ms per tick, without them ~2.5 ms.
    ///
    /// The corners are only needed where the hull can touch something. A big ship's corners are kept in the 16-voxel
    /// collider chunks that are near the terrain, a world object or another ship (within 3 m plus 0.3 s of motion, checked
    /// every 0.1 s, kept 1 s after), and taken out of the physics elsewhere. Players walk on the separate interior
    /// colliders and every aim, build and weapon ray uses the client's colliders: neither is touched.
    ///
    /// Unity derives the inertia tensor from the enabled colliders, so it is frozen at its full value the first time
    /// corners are taken out, and after a hull change it is recomputed from the enabled colliders plus the taken-out
    /// corners (as thin plates of the same density).
    /// </summary>
    internal static class ShipCornerCulling
    {
        private const int MinParts = 1000;
        private const int CheckEveryTicks = 5;
        private const float Margin = 3f;
        private const float LookAheadSeconds = 0.3f;
        private const float KeepSeconds = 1f;
        private const float CornerThickness = 0.1f;

        private sealed class Chunk
        {
            public ShipHullColliderChunk Component;
            public readonly List<MeshCollider> Corners = new List<MeshCollider>();
            public Vector3 LocalCenter, LocalExtents;
            public bool Enabled = true, Dirty = true;
            public float KeepUntil;
        }

        private sealed class Body
        {
            public Rigidbody Rigidbody;
            public TrackedShipServer Ship;
            public readonly Dictionary<ShipHullColliderChunk, Chunk> Chunks = new Dictionary<ShipHullColliderChunk, Chunk>();
            public bool InertiaFrozen;
            public int Phase;
        }

        private static readonly Dictionary<Rigidbody, Body> Bodies = new Dictionary<Rigidbody, Body>();
        private static readonly HashSet<ShipHullColliderChunk> DirtyChunks = new HashSet<ShipHullColliderChunk>();
        private static readonly List<(TrackedShipServer Ship, Vector3 Center, float Radius, Scene Scene)> Ships = new List<(TrackedShipServer, Vector3, float, Scene)>();
        private static readonly Collider[] Hits = new Collider[64];
        private static readonly AccessTools.FieldRef<ShipRigidbody, Rigidbody> RigidbodyField = AccessTools.FieldRefAccess<ShipRigidbody, Rigidbody>("_rigidbody");
        private static int _mask = -1, _tick;

        public static bool Enabled = true;
#if DEV
        public static int CornersOn, CornersOff;
#endif

        internal static void Patch(HarmonyLib.Harmony harmony)
        {
            harmony.Patch(AccessTools.Method("Managers.Server.Updaters.PhysicsScenesServerUpdater:SimulatePhysics"),
                prefix: new HarmonyMethod(typeof(ShipCornerCulling), nameof(BeforeSimulate)));
            var dirty = new HarmonyMethod(typeof(ShipCornerCulling), nameof(MarkDirty));
            foreach (string name in new[] { nameof(ShipHullColliderChunk.AddHullPart), nameof(ShipHullColliderChunk.RemoveHullPart), nameof(ShipHullColliderChunk.UpdateColliders) })
                harmony.Patch(AccessTools.Method(typeof(ShipHullColliderChunk), name), postfix: dirty);
            harmony.Patch(AccessTools.Method(typeof(ShipRigidbody), nameof(ShipRigidbody.EnableAutomaticCalculation)),
                postfix: new HarmonyMethod(typeof(ShipCornerCulling), nameof(AfterInertiaRecomputed)));
        }

        private static void MarkDirty(ShipHullColliderChunk __instance) => DirtyChunks.Add(__instance);

        private static void BeforeSimulate()
        {
            if (!Enabled) return;
            var tracker = Core.Services.ServiceLocator.GetService<ShipsServerTracker>();
            if (tracker == null) return;
            if (_mask == -1) _mask = ~(1 << LayerMask.NameToLayer("Ship"));
            _tick++;

            Ships.Clear();
            foreach (TrackedShipServer ship in tracker.Ships)
            {
                if (!ship.IsRigidbodyLoaded || ship.RigidbodyObject == null) continue;
                CoordBounds b = ship.GetHullCoordBounds();
                Transform t = ship.RigidbodyObject.transform;
                Ships.Add((ship, t.TransformPoint(b.Center), b.Extents.magnitude, ship.RigidbodyObject.scene));
            }

            DropGoneBodies();
            ApplyDirtyChunks();
            foreach (var (ship, _, _, _) in Ships)
            {
                if (ship.PartCount < MinParts) continue;
                var rb = ship.RigidbodyObject.GetComponent<Rigidbody>();
                if (rb == null) continue;
                if (!Bodies.TryGetValue(rb, out Body body))
                {
                    body = new Body { Rigidbody = rb, Ship = ship, Phase = Bodies.Count };
                    foreach (var c in rb.GetComponentsInChildren<ShipHullColliderChunk>(true))
                        body.Chunks[c] = new Chunk { Component = c };
                    Bodies[rb] = body;
                }
                Update(body);
            }
        }

        private static void DropGoneBodies()
        {
            List<Rigidbody> gone = null;
            foreach (var kv in Bodies)
                if (kv.Key == null || kv.Value.Ship.RigidbodyObject == null || kv.Value.Ship.RigidbodyObject != kv.Key.gameObject)
                    (gone ??= new List<Rigidbody>()).Add(kv.Key);
            if (gone != null)
                foreach (var rb in gone) Bodies.Remove(rb);
        }

        /// <summary>Chunks whose colliders were rebuilt: collect their corners again and give the new ones the chunk's state.</summary>
        private static void ApplyDirtyChunks()
        {
            if (DirtyChunks.Count == 0) return;
            foreach (var component in DirtyChunks)
            {
                if (component == null) continue;
                var rb = component.GetComponentInParent<Rigidbody>();
                if (rb == null || !Bodies.TryGetValue(rb, out Body body)) continue;
                if (!body.Chunks.TryGetValue(component, out Chunk chunk))
                    body.Chunks[component] = chunk = new Chunk { Component = component };
                chunk.Dirty = true;
            }
            DirtyChunks.Clear();
        }

        private static void Update(Body body)
        {
            Rigidbody rb = body.Rigidbody;
            Transform t = rb.transform;
            float now = Time.realtimeSinceStartup;
            List<ShipHullColliderChunk> gone = null;
            int i = 0;
            foreach (var kv in body.Chunks)
            {
                Chunk chunk = kv.Value;
                if (chunk.Component == null)
                {
                    (gone ??= new List<ShipHullColliderChunk>()).Add(kv.Key);
                    continue;
                }
                if (chunk.Dirty) Collect(chunk, t);
                if (chunk.Corners.Count == 0 || (i++ + body.Phase + _tick) % CheckEveryTicks != 0 && !chunk.Dirty) continue;
                chunk.Dirty = false;

                Vector3 center = t.TransformPoint(chunk.LocalCenter);
                float speed = rb.GetPointVelocity(center).magnitude;
                if (IsNear(body, center, t.rotation, chunk.LocalExtents, Margin + speed * LookAheadSeconds))
                    chunk.KeepUntil = now + KeepSeconds;
                bool enable = now < chunk.KeepUntil;
                if (enable == chunk.Enabled) continue;
                if (!enable) FreezeInertia(body);
                foreach (var c in chunk.Corners)
                    if (c != null) c.enabled = enable;
                chunk.Enabled = enable;
#if DEV
                if (enable) CornersOn += chunk.Corners.Count;
                else CornersOff += chunk.Corners.Count;
#endif
            }
            if (gone != null)
                foreach (var c in gone) body.Chunks.Remove(c);
        }

        private static void Collect(Chunk chunk, Transform body)
        {
            chunk.Corners.Clear();
            chunk.Component.GetComponentsInChildren(true, chunk.Corners);
            if (chunk.Corners.Count == 0) return;
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            foreach (var c in chunk.Corners)
            {
                Vector3 p = body.InverseTransformPoint(c.transform.position);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
                if (c.enabled != chunk.Enabled) c.enabled = chunk.Enabled;
            }
            chunk.LocalCenter = (min + max) * 0.5f;
            // a corner reaches up to ~1 m from its pivot
            chunk.LocalExtents = (max - min) * 0.5f + Vector3.one;
        }

        private static bool IsNear(Body body, Vector3 center, Quaternion rotation, Vector3 extents, float margin)
        {
            Scene scene = body.Rigidbody.gameObject.scene;
            float radius = extents.magnitude;
            foreach (var (other, otherCenter, otherRadius, otherScene) in Ships)
                if (other != body.Ship && otherScene == scene && (otherCenter - center).sqrMagnitude < Sq(radius + otherRadius + margin)
                    && other.RigidbodyObject.GetComponentInParent<Rigidbody>() != body.Rigidbody)
                    return true;
            int n = scene.GetPhysicsScene().OverlapBox(center, extents + Vector3.one * margin, Hits, rotation, _mask, QueryTriggerInteraction.Ignore);
            if (n == Hits.Length) return true;
            for (int i = 0; i < n; i++)
                if (Hits[i].attachedRigidbody != body.Rigidbody) return true;
            return false;
        }

        /// <summary>Keeps the inertia Unity computed with every collider, before the first corners leave the body.</summary>
        private static void FreezeInertia(Body body)
        {
            if (body.InertiaFrozen) return;
            Rigidbody rb = body.Rigidbody;
            Vector3 tensor = rb.inertiaTensor;
            Quaternion rotation = rb.inertiaTensorRotation;
            rb.automaticInertiaTensor = false;
            rb.inertiaTensor = tensor;
            rb.inertiaTensorRotation = rotation;
            body.InertiaFrozen = true;
        }

        /// <summary>
        /// After a hull change the game lets Unity recompute the inertia from the enabled colliders. The taken-out corners
        /// are added back: thin plates (0.1 m) of the density of the whole hull, as point masses at their centres.
        /// </summary>
        private static void AfterInertiaRecomputed(ShipRigidbody __instance) => CompleteInertia(RigidbodyField(__instance));

        internal static void CompleteInertia(Rigidbody rb)
        {
            if (rb == null || !Bodies.TryGetValue(rb, out Body body) || !body.InertiaFrozen) return;
            Vector3 tensor = rb.inertiaTensor;
            Quaternion rotation = rb.inertiaTensorRotation;
            rb.automaticInertiaTensor = false;

            float enabledVolume = 0f, cornerVolume = 0f;
            var cornerInertia = new Matrix3();
            Vector3 com = rb.centerOfMass;
            Transform t = rb.transform;
            foreach (var c in rb.GetComponentsInChildren<Collider>(false))
                if (c.enabled && !c.isTrigger) enabledVolume += Volume(c);
            foreach (var chunk in body.Chunks.Values)
            {
                if (chunk.Enabled) continue;
                foreach (var c in chunk.Corners)
                {
                    if (c == null || c.sharedMesh == null) continue;
                    float v = Volume(c);
                    cornerVolume += v;
                    cornerInertia.AddPointMass(v, t.InverseTransformPoint(c.transform.TransformPoint(c.sharedMesh.bounds.center)) - com);
                }
            }
            if (enabledVolume <= 0f || cornerVolume <= 0f)
            {
                rb.inertiaTensor = tensor;
                rb.inertiaTensorRotation = rotation;
                return;
            }
            float total = enabledVolume + cornerVolume;
            // Unity spread the mass over the enabled colliders; spread it over all of them instead
            Matrix3 full = Matrix3.FromDiagonal(tensor, rotation).Scaled(enabledVolume / total);
            full.Add(cornerInertia.Scaled(rb.mass / total));
            full.Diagonalize(out Vector3 diagonal, out Quaternion axes);
            rb.inertiaTensor = diagonal;
            rb.inertiaTensorRotation = axes;
        }

#if DEV
        /// <summary>The inertia this class would give the body now, against Unity's with every corner back in.</summary>
        public static string Check(Rigidbody rb)
        {
            if (!Bodies.TryGetValue(rb, out Body body)) return "not managed";
            if (!body.InertiaFrozen) return $"nothing taken out, tensor {rb.inertiaTensor}";
            rb.automaticInertiaTensor = true;
            CompleteInertia(rb);
            string estimate = $"estimate {rb.inertiaTensor} rot {rb.inertiaTensorRotation.eulerAngles}";
            foreach (var chunk in body.Chunks.Values)
            {
                foreach (var c in chunk.Corners) if (c != null) c.enabled = true;
                chunk.Enabled = true;
                chunk.KeepUntil = 0f;
            }
            rb.automaticInertiaTensor = true;
            string actual = $"unity {rb.inertiaTensor} rot {rb.inertiaTensorRotation.eulerAngles}";
            body.InertiaFrozen = false;
            return estimate + " / " + actual;
        }
#endif

        private static float Volume(Collider c)
        {
            Vector3 s = c.transform.lossyScale;
            switch (c)
            {
                case BoxCollider box:
                    return Mathf.Abs(box.size.x * s.x * box.size.y * s.y * box.size.z * s.z);
                case MeshCollider mesh when mesh.sharedMesh != null:
                    // corner panels: a right triangle of the mesh's two large sides, CornerThickness thick
                    Vector3 e = Vector3.Scale(mesh.sharedMesh.bounds.size, s);
                    float a = Mathf.Abs(e.x), b = Mathf.Abs(e.y), d = Mathf.Abs(e.z);
                    float min = Mathf.Min(a, Mathf.Min(b, d));
                    return a * b * d / min * 0.5f * CornerThickness;
                default:
                    return 0f;
            }
        }

        private static float Sq(float x) => x * x;

        /// <summary>A symmetric 3x3 matrix, for inertia tensors.</summary>
        private struct Matrix3
        {
            public float XX, YY, ZZ, XY, XZ, YZ;

            public void AddPointMass(float m, Vector3 r)
            {
                XX += m * (r.y * r.y + r.z * r.z);
                YY += m * (r.x * r.x + r.z * r.z);
                ZZ += m * (r.x * r.x + r.y * r.y);
                XY -= m * r.x * r.y;
                XZ -= m * r.x * r.z;
                YZ -= m * r.y * r.z;
            }

            public void Add(Matrix3 o)
            {
                XX += o.XX; YY += o.YY; ZZ += o.ZZ; XY += o.XY; XZ += o.XZ; YZ += o.YZ;
            }

            public Matrix3 Scaled(float k) => new Matrix3 { XX = XX * k, YY = YY * k, ZZ = ZZ * k, XY = XY * k, XZ = XZ * k, YZ = YZ * k };

            /// <summary>R diag(d) R^T.</summary>
            public static Matrix3 FromDiagonal(Vector3 d, Quaternion q)
            {
                Matrix4x4 r = Matrix4x4.Rotate(q);
                var m = new Matrix3();
                for (int k = 0; k < 3; k++)
                {
                    Vector3 c = r.GetColumn(k);
                    float dk = d[k];
                    m.XX += dk * c.x * c.x; m.YY += dk * c.y * c.y; m.ZZ += dk * c.z * c.z;
                    m.XY += dk * c.x * c.y; m.XZ += dk * c.x * c.z; m.YZ += dk * c.y * c.z;
                }
                return m;
            }

            /// <summary>Jacobi eigen decomposition: principal moments and the rotation of the principal axes.</summary>
            public void Diagonalize(out Vector3 diagonal, out Quaternion rotation)
            {
                double[,] a = { { XX, XY, XZ }, { XY, YY, YZ }, { XZ, YZ, ZZ } };
                double[,] v = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
                for (int sweep = 0; sweep < 32; sweep++)
                {
                    double off = a[0, 1] * a[0, 1] + a[0, 2] * a[0, 2] + a[1, 2] * a[1, 2];
                    if (off < 1e-18 * (a[0, 0] * a[0, 0] + a[1, 1] * a[1, 1] + a[2, 2] * a[2, 2])) break;
                    for (int p = 0; p < 2; p++)
                    for (int q = p + 1; q < 3; q++)
                    {
                        if (System.Math.Abs(a[p, q]) < 1e-30) continue;
                        double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                        double tn = System.Math.Sign(theta) / (System.Math.Abs(theta) + System.Math.Sqrt(theta * theta + 1));
                        if (theta == 0) tn = 1;
                        double cs = 1 / System.Math.Sqrt(tn * tn + 1), sn = tn * cs;
                        for (int k = 0; k < 3; k++)
                        {
                            double akp = a[k, p], akq = a[k, q];
                            a[k, p] = cs * akp - sn * akq;
                            a[k, q] = sn * akp + cs * akq;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double apk = a[p, k], aqk = a[q, k];
                            a[p, k] = cs * apk - sn * aqk;
                            a[q, k] = sn * apk + cs * aqk;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double vkp = v[k, p], vkq = v[k, q];
                            v[k, p] = cs * vkp - sn * vkq;
                            v[k, q] = sn * vkp + cs * vkq;
                        }
                    }
                }
                diagonal = new Vector3((float)a[0, 0], (float)a[1, 1], (float)a[2, 2]);
                var x = new Vector3((float)v[0, 0], (float)v[1, 0], (float)v[2, 0]);
                var y = new Vector3((float)v[0, 1], (float)v[1, 1], (float)v[2, 1]);
                if (Vector3.Dot(Vector3.Cross(x, y), new Vector3((float)v[0, 2], (float)v[1, 2], (float)v[2, 2])) < 0) y = -y;
                rotation = Quaternion.LookRotation(Vector3.Cross(x, y), y);
            }
        }
    }
}
