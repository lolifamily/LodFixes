using ClientPlugin.Fixes;
using HarmonyLib;
using Sandbox.Engine.Voxels;
using VRage.Game.Components;
using VRage.Game.Models;
using VRageMath;

namespace ClientPlugin.Patches;

// Callers passing a float line, like GetPositionOnVoxel of asteroids: the cell walk no longer misses, the line keeps its
// rounding (see VoxelRaycast). Camera blocks call VoxelRaycast with their double ray instead (MyCameraBlockPatch).
[HarmonyPatch(typeof(MyVoxelGeometry), nameof(MyVoxelGeometry.Intersect))]
internal static class MyVoxelGeometryIntersectPatch
{
    // Applied at startup only
    private static bool Prepare() => Config.Current.FixTerrainRaycast;

    [HarmonyPrefix]
    private static bool IntersectPrefix(MyVoxelGeometry __instance, ref Line localLine, out MyIntersectionResultLineTriangle result, IntersectionFlags flags, ref bool __result)
    {
        var line = new LineD(localLine.From, localLine.To);
        __result = VoxelRaycast.Intersect(__instance, ref line, flags, out result);
        return false;
    }
}
