using System.Collections.Generic;
using ClientPlugin.Fixes;
using HarmonyLib;
using Sandbox.Engine.Voxels.Planet;
using VRage.Game;

namespace ClientPlugin.Patches;

// Planet height and material maps decoded straight into the game's buffers (see PlanetMaps). A null from there leaves
// the map to the game's own loader. The loaders themselves catch with an exception filter, which Harmony cannot copy
// (it never marks where the handler of a filter starts), so their only call sites are redirected instead.
[HarmonyPatch(typeof(MyPlanetTextureMapProvider))]
internal static class MyPlanetTextureMapProviderPatch
{
    // Applied at startup only
    private static bool Prepare() => Config.Current.FastPlanetMapLoading;

    // One heightmap face. The loop over the faces, its size checks and the loading screen progress stay the game's.
    [HarmonyTranspiler]
    [HarmonyPatch(nameof(MyPlanetTextureMapProvider.GetHeightMap), typeof(string), typeof(MyModContext))]
    private static IEnumerable<CodeInstruction> GetHeightMapTranspiler(IEnumerable<CodeInstruction> instructions) =>
        instructions.MethodReplacer(
            AccessTools.Method(typeof(MyPlanetTextureMapProvider), nameof(MyPlanetTextureMapProvider.GetHeightMap), [typeof(string), typeof(string), typeof(MyModContext)]),
            AccessTools.Method(typeof(MyPlanetTextureMapProviderPatch), nameof(LoadHeightmapFace)));

    // All material maps, the game loads their faces inline. The map cache stays the game's.
    [HarmonyTranspiler]
    [HarmonyPatch(nameof(MyPlanetTextureMapProvider.GetMaps))]
    private static IEnumerable<CodeInstruction> GetMapsTranspiler(IEnumerable<CodeInstruction> instructions) =>
        instructions.MethodReplacer(
            AccessTools.Method(typeof(MyPlanetTextureMapProvider), nameof(MyPlanetTextureMapProvider.GetPlanetMaps)),
            AccessTools.Method(typeof(MyPlanetTextureMapProviderPatch), nameof(LoadMaterialMaps)));

    // Each has the shape of the call it replaces with the provider first, so the arguments on the stack stay as they are
    private static MyHeightmapFace LoadHeightmapFace(MyPlanetTextureMapProvider provider, string folderName, string faceName, MyModContext context) =>
        PlanetMaps.ReadHeightmapFace(MyPlanetTextureMapProvider.GetPath(folderName, faceName, context)) ??
        provider.GetHeightMap(folderName, faceName, context);

    private static void LoadMaterialMaps(MyPlanetTextureMapProvider provider, string folder, MyModContext context, MyPlanetMapTypeSet mapsToUse, out MyCubemap[] maps)
    {
        maps = PlanetMaps.ReadMaterialMaps(folder, context, mapsToUse);
        if (maps == null)
            provider.GetPlanetMaps(folder, context, mapsToUse, out maps);
    }
}
