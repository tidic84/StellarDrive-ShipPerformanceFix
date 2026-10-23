using HarmonyLib;
using Ships.Physics;
using Ships.Physics.Model;
using UnityEngine;

namespace ShipPerformanceFix
{
    /// <summary>
    /// On the client, a ship's colliders hang under a transform with no rigidbody: they are static colliders. Every
    /// physics tick the game writes the ship's pose to that transform, even when the ship has not moved, and PhysX then
    /// moves every collider of the ship one by one (about 15,000 on a 25,000-panel ship: ~9 ms per tick).
    ///
    /// The pose is written only when it really changed: past 0.1 mm, or a rotation that moves a point 250 m away by
    /// about a millimetre. A parked ship, or the ship the player stands in, then costs nothing.
    /// </summary>
    internal static class ClientShipPose
    {
        private const float PositionTolerance = 1e-4f;
        private const float RotationTolerance = 2e-6f;

        public static bool Enabled = true;
#if DEV
        public static int Written, Skipped;
#endif

        internal static void Patch(HarmonyLib.Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(ShipRigidbody), nameof(ShipRigidbody.SetPhysicsStateClient)),
                prefix: new HarmonyMethod(typeof(ClientShipPose), nameof(Prefix)));
        }

        private static bool Prefix(ShipRigidbody __instance, ShipPhysicsCalculationProperties props,
            ref Vector3 ____lastPositionSetAttempt, ref Quaternion ____lastRotationSetAttempt)
        {
            if (!Enabled) return true;
            var state = props.PhysicsState;
            Transform t = __instance.transform;
            t.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
            if (!Same(position, rotation, state.Position, state.Rotation))
            {
#if DEV
                Written++;
#endif
                return true;
            }
#if DEV
            Skipped++;
#endif
            ____lastPositionSetAttempt = state.Position;
            ____lastRotationSetAttempt = state.Rotation;
            return false;
        }

        private static bool Same(Vector3 p, Quaternion r, Vector3 p2, Quaternion r2)
        {
            if (Mathf.Abs(p.x - p2.x) > PositionTolerance || Mathf.Abs(p.y - p2.y) > PositionTolerance || Mathf.Abs(p.z - p2.z) > PositionTolerance)
                return false;
            // q and -q are the same rotation
            float sign = r.x * r2.x + r.y * r2.y + r.z * r2.z + r.w * r2.w < 0f ? -1f : 1f;
            return Mathf.Abs(r.x - sign * r2.x) <= RotationTolerance && Mathf.Abs(r.y - sign * r2.y) <= RotationTolerance
                && Mathf.Abs(r.z - sign * r2.z) <= RotationTolerance && Mathf.Abs(r.w - sign * r2.w) <= RotationTolerance;
        }
    }
}
