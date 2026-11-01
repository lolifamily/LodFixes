using ClientPlugin.Fixes;
using HarmonyLib;
using Sandbox.Game.Entities;
using VRageMath;

namespace ClientPlugin.Patches;

// Both convert the position relative to the planet center to float before they look up the surface (see HeightmapSurface)
[HarmonyPatch(typeof(MyPlanet))]
internal static class MyPlanetPatch
{
    // Applied at startup only
    private static bool Prepare() => Config.Current.FixAltitudeJitter;

    // The overload taking the position by value calls this one
    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyPlanet.GetClosestSurfacePointGlobal), [typeof(Vector3D)], [ArgumentType.Ref])]
    private static bool GetClosestSurfacePointGlobalPrefix(MyPlanet __instance, ref Vector3D globalPos, ref Vector3D __result)
    {
        __result = HeightmapSurface.ClosestPoint(__instance, globalPos);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyPlanet.GetHeightFromSurface))]
    private static bool GetHeightFromSurfacePrefix(MyPlanet __instance, Vector3D position, ref double __result)
    {
        __result = HeightmapSurface.Distance(__instance.Provider.Shape, position - __instance.WorldMatrix.Translation);
        return false;
    }
}
