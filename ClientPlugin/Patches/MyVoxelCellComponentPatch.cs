using ClientPlugin.Fixes;
using HarmonyLib;
using VRageRender;

namespace ClientPlugin.Patches;

[HarmonyPatch(typeof(MyVoxelCellComponent))]
internal static class MyVoxelCellComponentPatch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(MyVoxelCellComponent.CreateRenderableProxyForPart))]
    private static void CreateRenderableProxyForPartPostfix(MyVoxelCellComponent __instance, int lodIndex, int proxyIndex) =>
        VoxelCellFixes.OnProxyCreated(__instance, lodIndex, proxyIndex);

    [HarmonyPostfix]
    [HarmonyPatch(nameof(MyVoxelCellComponent.UpdateParametersFromEntity))]
    private static void UpdateParametersFromEntityPostfix(MyVoxelCellComponent __instance) =>
        VoxelCellFixes.OnParametersUpdated(__instance);
}
