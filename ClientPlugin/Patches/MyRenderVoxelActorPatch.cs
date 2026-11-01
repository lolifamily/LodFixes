using ClientPlugin.Fixes;
using HarmonyLib;
using VRage.Render11.GeometryStage.Voxel;

namespace ClientPlugin.Patches;

[HarmonyPatch(typeof(MyRenderVoxelActor), nameof(MyRenderVoxelActor.UpdateQueued))]
internal static class MyRenderVoxelActorPatch
{
    // Called once per frame on the render thread
    [HarmonyPostfix]
    private static void UpdateQueuedPostfix() => VoxelCellFixes.RefreshIfRequested();
}
