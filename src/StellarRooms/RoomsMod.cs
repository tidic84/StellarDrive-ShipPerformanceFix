using MelonLoader;

[assembly: MelonInfo(typeof(StellarRooms.RoomsMod), "StellarRooms", "0.1.0", "tidic84")]
[assembly: MelonGame("CuriousOwlGames", "StellarDrive")]

namespace StellarRooms
{
    public class RoomsMod : MelonMod
    {
        internal static RoomsMod Instance { get; private set; }
        internal static MelonLogger.Instance Log => Instance.LoggerInstance;

        public override void OnInitializeMelon()
        {
            Instance = this;
            RoomsFastPath.Patch(HarmonyInstance);
#if DEV
            RoomsDev.Init(HarmonyInstance, Log);
#endif
            Log.Msg("StellarRooms ready.");
        }

#if DEV
        public override void OnUpdate() => RoomsDev.Update();
#endif
    }
}
