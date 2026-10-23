using MelonLoader;

[assembly: MelonInfo(typeof(ShipPerformanceFix.ShipPerfMod), "ShipPerformanceFix", "0.4.1", "tidic84")]
[assembly: MelonGame("CuriousOwlGames", "StellarDrive")]

namespace ShipPerformanceFix
{
    public class ShipPerfMod : MelonMod
    {
        internal static ShipPerfMod Instance { get; private set; }
        internal static MelonLogger.Instance Log => Instance.LoggerInstance;

        public override void OnInitializeMelon()
        {
            Instance = this;
            RoomsFastPath.Patch(HarmonyInstance);
            RoomsBulkLoad.Patch(HarmonyInstance);
            BigShipJoin.Patch(HarmonyInstance);
            ClientShipPose.Patch(HarmonyInstance);
            HullBoundsCache.Patch(HarmonyInstance);
            FieldDoorThrottle.Patch(HarmonyInstance);
            ShipCcd.Patch(HarmonyInstance);
            ShipCornerCulling.Patch(HarmonyInstance);
#if DEV
            RoomsDev.Init(HarmonyInstance, Log);
#endif
            Log.Msg("ShipPerformanceFix ready.");
        }

#if DEV
        public override void OnUpdate() => RoomsDev.Update();
#endif
    }
}
