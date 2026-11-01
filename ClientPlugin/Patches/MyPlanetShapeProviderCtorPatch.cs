using ClientPlugin.Fixes;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Engine.Voxels.Planet;
using VRageMath;

namespace ClientPlugin.Patches;

// The detail map of steep slopes raises the ground by up to its Scale above the heightmap, but the terrain culls only
// allow for the heightmap. The cell cull of the voxels makes air of anything over a voxel above the top of the cell,
// which cuts the detail flat along the top of every heightmap cell. The block cull (IntersectBoundingBoxInternal, also
// behind Storage.Intersect) subtracts m_detailScale for the detail, but the game never sets that field. This sets it,
// TerrainSdf's cell cull adds it too, the native code's own cell cull still cuts.
[HarmonyPatch(typeof(MyPlanetShapeProvider), MethodType.Constructor, typeof(Vector3), typeof(float), typeof(MyPlanetGeneratorDefinition), typeof(MyHeightCubemap), typeof(MyPlanetTextureMapProvider))]
internal static class MyPlanetShapeProviderCtorPatch
{
    // Always applied, so the option can change at runtime: a loading planet takes it as it is, TerrainSdf.RegenerateTerrain
    // applies a change to the loaded ones
    [HarmonyPostfix]
    private static void ConstructorPostfix(MyPlanetShapeProvider __instance) =>
        __instance.m_detailScale = TerrainSdf.DetailCullScale(__instance);
}
