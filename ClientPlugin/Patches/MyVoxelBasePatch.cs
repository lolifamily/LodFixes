using ClientPlugin.Fixes;
using HarmonyLib;
using Sandbox.Game.Entities;
using VRage.Game.Components;
using VRage.Game.Models;
using VRageMath;

namespace ClientPlugin.Patches;

// Raycasts of voxel maps (MyEntities raycasts, building, bullets) with the line in double up to the hit (see VoxelRaycast)
[HarmonyPatch(typeof(MyVoxelBase), nameof(MyVoxelBase.GetIntersectionWithLine), [typeof(LineD), typeof(MyIntersectionResultLineTriangleEx?), typeof(IntersectionFlags)], [ArgumentType.Ref, ArgumentType.Out, ArgumentType.Normal])]
internal static class MyVoxelBasePatch
{
    // Applied at startup only
    private static bool Prepare() => Config.Current.FixTerrainRaycast;

    // The overload returning only the point calls this one
    [HarmonyPrefix]
    private static bool GetIntersectionWithLinePrefix(MyVoxelBase __instance, ref LineD worldLine, out MyIntersectionResultLineTriangleEx? t, IntersectionFlags flags, ref bool __result)
    {
        __result = VoxelRaycast.IntersectWorld(__instance, ref worldLine, flags, out t);
        return false;
    }
}
