using ClientPlugin.Fixes;
using HarmonyLib;
using Sandbox.Game.Entities.Planet;

namespace ClientPlugin.Patches;

// Measures the models of a planet's vegetation while the planet loads (see ModelFootprints.Preload). Nothing here throws
// short of running out of memory: GetDefinition returns null for what it does not find, Measure turns every other
// failure into None. Anything thrown would fail the planet's init, and with it the whole world load.
[HarmonyPatch(typeof(MyPlanetEnvironmentComponent), nameof(MyPlanetEnvironmentComponent.InitEnvironment))]
internal static class MyPlanetEnvironmentComponentInitPatch
{
    // The only caller, MyPlanet.Init, calls it for an environment definition, which it sets first
    [HarmonyPostfix]
    private static void InitEnvironmentPostfix(MyPlanetEnvironmentComponent __instance)
    {
        if (Config.Current.FixVegetationPlacement)
            ModelFootprints.Static?.Preload(__instance.EnvironmentDefinition);
    }
}
