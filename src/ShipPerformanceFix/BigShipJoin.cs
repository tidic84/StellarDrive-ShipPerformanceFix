using System;
using System.Runtime.InteropServices;
using FishNet;
using HarmonyLib;
using Steamworks;

namespace ShipPerformanceFix
{
    /// <summary>
    /// A player joining a world receives every ship in one message: the master sync object's full state, about 17 bytes
    /// per hull panel. FishySteamworks tells FishNet that a reliable message may hold 1 MB, so FishNet never splits it,
    /// but Steam refuses any message over 512 KB (k_cbMaxSteamNetworkingSocketsMessageSizeSend). Past roughly 30,000
    /// panels in the world the spawn message is dropped and the joining player loads forever.
    ///
    /// Reliable MTU capped at Steam's limit, so FishNet splits the message itself (the client reassembles split
    /// messages from the server with no size limit). The pieces are queued in the same tick, so Steam's send buffer
    /// (512 KB by default, past which sends fail with k_EResultLimitExceeded) is raised too. Only the host needs it;
    /// a client with it still reads anything Steam can deliver.
    /// </summary>
    internal static class BigShipJoin
    {
        // k_cbMaxSteamNetworkingSocketsMessageSizeSend. FishNet keeps 2 bytes of reserve and FishySteamworks adds a
        // channel byte, so the largest message sent is one byte under the limit.
        private const int SteamMaxMessage = 512 * 1024;
        private const int SendBuffer = 64 * 1024 * 1024;

        private static bool _sendBufferSet;

        internal static void Patch(HarmonyLib.Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(FishySteamworks.FishySteamworks), nameof(FishySteamworks.FishySteamworks.GetMTU)),
                postfix: new HarmonyMethod(typeof(BigShipJoin), nameof(GetMTUPostfix)));
            harmony.Patch(AccessTools.Method(typeof(FishySteamworks.Server.ServerSocket), "StartConnection"),
                prefix: new HarmonyMethod(typeof(BigShipJoin), nameof(StartPrefix)));
            harmony.Patch(AccessTools.Method(typeof(FishySteamworks.Client.ClientSocket), "StartConnection"),
                prefix: new HarmonyMethod(typeof(BigShipJoin), nameof(StartPrefix)));
        }

        private static void GetMTUPostfix(byte channel, ref int __result)
        {
            if (channel == 0 && __result > SteamMaxMessage)
                __result = SteamMaxMessage;
        }

        private static void StartPrefix()
        {
            if (!_sendBufferSet)
            {
                _sendBufferSet = SetGlobal(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize, SendBuffer);
                ShipPerfMod.Log.Msg(_sendBufferSet
                    ? $"Big ship join: Steam send buffer raised to {SendBuffer / (1024 * 1024)} MB."
                    : "Big ship join: could not raise Steam's send buffer, a join may still fail on a very big world.");
            }
            var tm = InstanceFinder.TransportManager;
            if (tm != null)
                ShipPerfMod.Log.Msg($"Big ship join: reliable messages split above {tm.GetLowestMTU(0)} bytes.");
        }

        private static bool SetGlobal(ESteamNetworkingConfigValue key, int value)
        {
            IntPtr arg = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(arg, value);
                return SteamNetworkingUtils.SetConfigValue(key, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global,
                    IntPtr.Zero, ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, arg);
            }
            catch (Exception e)
            {
                ShipPerfMod.Log.Warning($"Big ship join: {e.Message}");
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(arg);
            }
        }
    }
}
