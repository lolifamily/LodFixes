using HarmonyLib;
using Sandbox.Game.Entities.Planet;
using Sandbox.Game.WorldEnvironment;

namespace ClientPlugin.Patches;

// TryGetSector returns false exactly when it finds a physics sector. A LOD 0 clipmap proxy over one then creates a
// second sector with the same id, and the physics one is never closed once its physics is released (entity, Havok body
// and boulders leak). Clients also drop the server's tree removal for sectors that only have physics.
[HarmonyPatch(typeof(MyPlanetEnvironmentComponent), nameof(MyPlanetEnvironmentComponent.TryGetSector))]
internal static class MyPlanetEnvironmentComponentPatch
{
    private static bool Prepare() => Config.Current.FixEnvironmentSectorLookup;

    // The sector is only set when it was found, as a physics or as a held sector
    [HarmonyPostfix]
    private static void TryGetSectorPostfix(ref MyEnvironmentSector environmentSector, ref bool __result)
    {
        if (environmentSector != null)
            __result = true;
    }
}
