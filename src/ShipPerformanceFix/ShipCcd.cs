using HarmonyLib;
using Ships;
using Ships.Services;
using UnityEngine;

namespace ShipPerformanceFix
{
    /// <summary>
    /// The host simulates every ship as a rigidbody with sweep-based continuous collision detection
    /// (ContinuousDynamic), so that thin hull panels (0.1 m) do not pass through the ground. On a big ship that is
    /// thousands of shapes swept against the terrain every tick: a 25,000-panel ship pushed along the ground at
    /// 15 m/s costs ~22 ms per physics tick.
    ///
    /// Big ships use PhysX's speculative CCD instead (ContinuousSpeculative): contacts are created ahead of the motion
    /// instead of sweeping every shape, which keeps the ground solid at a fraction of the cost (~10 ms per tick in
    /// the same run, and cheaper at rest too).
    /// </summary>
    internal static class ShipCcd
    {
        private const int MinParts = 1000;

        public static bool Enabled = true;

        internal static void Patch(HarmonyLib.Harmony harmony)
        {
            harmony.Patch(AccessTools.Method("Managers.Server.Updaters.PhysicsScenesServerUpdater:SimulatePhysics"),
                prefix: new HarmonyMethod(typeof(ShipCcd), nameof(BeforeSimulate)));
        }

        private static void BeforeSimulate()
        {
            if (!Enabled) return;
            var tracker = Core.Services.ServiceLocator.GetService<ShipsServerTracker>();
            if (tracker == null) return;
            foreach (TrackedShipServer ship in tracker.Ships)
            {
                if (!ship.IsRigidbodyLoaded || ship.RigidbodyObject == null || ship.PartCount < MinParts) continue;
                var rb = ship.RigidbodyObject.GetComponent<Rigidbody>();
                // the game only ever sets ContinuousDynamic; anything else was chosen by someone else
                if (rb != null && rb.collisionDetectionMode == CollisionDetectionMode.ContinuousDynamic)
                    rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            }
        }
    }
}
