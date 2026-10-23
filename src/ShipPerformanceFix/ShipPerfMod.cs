using MelonLoader;

[assembly: MelonInfo(typeof(ShipPerformanceFix.ShipPerfMod), "ShipPerformanceFix", "0.2.0", "tidic84")]
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
            BigShipJoin.Patch(HarmonyInstance);
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
