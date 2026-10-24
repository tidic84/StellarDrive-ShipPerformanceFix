using System.Collections.Generic;
using Core.Utils;
using HarmonyLib;
using Ships.Interface.Model;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Placement;
using Ships.Interface.Services;
using Ships.Interface.Settings;
using Ships.Interface.Values;
using Ships.Rooms;
using UnityEngine;

namespace ShipPerformanceFix
{
    /// <summary>
    /// Two per-panel costs that add up to tens of seconds when a big world is loaded or joined (every hull panel goes
    /// through them, on the host and on each client):
    /// - Part settings lookups: PartSettingsProvider.GetPartSettingById calls EnsureDictIsCached every time, and about
    ///   ten mods hook EnsureDictIsCached to add their parts the first time the list is built. Once the list is built,
    ///   a part in it is returned straight away (UpgradeTable's own part 2100 still goes through its hook).
    /// - Room voxels of a panel: ShipHullPartsVoxelsCache recomputed the panel's rotation for each of its voxels (up to
    ///   ~150); it is computed once per panel, with the same operations.
    /// </summary>
    internal static class ShipLoadSpeed
    {
        private const ushort UpgradeTablePartId = 2100;

        public static bool Enabled = true;

        private static readonly AccessTools.FieldRef<PartSettingsProvider, Dictionary<ushort, PartSettings>> PartsDict =
            AccessTools.FieldRefAccess<PartSettingsProvider, Dictionary<ushort, PartSettings>>("_partsDict");
        private static readonly AccessTools.FieldRef<ShipHullPartsVoxelsCache, Vector3[]> Flat = AccessTools.FieldRefAccess<ShipHullPartsVoxelsCache, Vector3[]>("_flatVoxels");
        private static readonly AccessTools.FieldRef<ShipHullPartsVoxelsCache, Vector3[]> Corner = AccessTools.FieldRefAccess<ShipHullPartsVoxelsCache, Vector3[]>("_cornerVoxels");
        private static readonly AccessTools.FieldRef<ShipHullPartsVoxelsCache, Vector3[]> Slope = AccessTools.FieldRefAccess<ShipHullPartsVoxelsCache, Vector3[]>("_slopeVoxels");
        private static readonly AccessTools.FieldRef<ShipHullPartsVoxelsCache, Vector3[]> SlopeCorner = AccessTools.FieldRefAccess<ShipHullPartsVoxelsCache, Vector3[]>("_slopeCornerVoxels");

        internal static void Patch(HarmonyLib.Harmony harmony)
        {
#if DEV
            // dev: start without it (to compare a load) when UserData/stellar_loadspeed_off exists
            Enabled = !System.IO.File.Exists(System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "stellar_loadspeed_off"));
#endif
            harmony.Patch(AccessTools.Method(typeof(PartSettingsProvider), nameof(PartSettingsProvider.GetPartSettingById)),
                prefix: new HarmonyMethod(typeof(ShipLoadSpeed), nameof(GetPartPrefix)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(ShipHullPartsVoxelsCache), nameof(ShipHullPartsVoxelsCache.GetBlockedOutVoxels)),
                prefix: new HarmonyMethod(typeof(ShipLoadSpeed), nameof(BlockedOutPrefix)));
        }

        private static bool GetPartPrefix(PartSettingsProvider __instance, ushort id, ref PartSettings __result)
        {
            if (!Enabled || id == UpgradeTablePartId) return true;
            var dict = PartsDict(__instance);
            if (dict == null || !dict.TryGetValue(id, out PartSettings settings)) return true;
            __result = settings;
            return false;
        }

        /// <summary>ShipHullPartsVoxelsCache.GetBlockedOutVoxels, with the panel's rotation computed once.</summary>
        private static bool BlockedOutPrefix(ShipHullPartsVoxelsCache __instance, HullMeshSegmentType segmentType, Vector3 offset, HullPartPlacement placement, ref Vector3Int[] __result)
        {
            if (!Enabled) return true;
            Vector3[] voxels;
            switch (segmentType)
            {
                case HullMeshSegmentType.Flat: voxels = Flat(__instance); break;
                case HullMeshSegmentType.Corner: voxels = Corner(__instance); break;
                case HullMeshSegmentType.Slope: voxels = Slope(__instance); break;
                case HullMeshSegmentType.SlopeCorner: voxels = SlopeCorner(__instance); break;
                default: return true;
            }
            if (voxels == null) return true;
            var result = new Vector3Int[voxels.Length];
            float half = ShipRoomsConstants.RoomVoxelResolution / 2f;
            Vector3 shift = Vector3.one * half;
            if (placement.Position.Type == VoxelPartType.Floor) shift += Vector3.down * half;
            else if (placement.Position.Type == VoxelPartType.ForwardWall) shift += Vector3.forward * half;
            else if (placement.Position.Type == VoxelPartType.RightWall) shift += Vector3.right * half;
            Quaternion rotation = placement.Rotation;
            for (int i = 0; i < voxels.Length; i++)
                result[i] = VectorUtils.Vector3RoundToInt(rotation * (voxels[i] + offset) + shift);
            __result = result;
            return false;
        }
    }
}
