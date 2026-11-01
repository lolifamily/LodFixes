using ClientPlugin.Fixes;
using HarmonyLib;
using Sandbox.Engine.Voxels.Planet;

namespace ClientPlugin.Patches;

[HarmonyPatch(typeof(MyHeightmapFace), nameof(MyHeightmapFace.CreatePruningTree))]
internal static class MyHeightmapFacePatch
{
    // Applied at startup only: heightmaps are built once per world load, and off leaves the game's method unpatched
    private static bool Prepare() => Config.Current.FixHeightmapPruning;

    [HarmonyPrefix]
    private static bool CreatePruningTreePrefix(MyHeightmapFace __instance)
    {
        HeightmapPruning.Build(__instance);
        return false;
    }
}
