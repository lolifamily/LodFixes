using System;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game.Entities.Planet;

namespace ClientPlugin.Patches;

// Raises the 8000 km planet diameter limit to what the voxel storage can address
[HarmonyPatch(typeof(MyPlanets), nameof(MyPlanets.CanSpawnPlanet))]
internal static class MyPlanetsPatch
{
    // The storage edge is the outer diameter rounded up to a power of two. Beyond 2^24 voxels the octree
    // cell coordinates overflow the 20 bits per axis of MyCellCoord.PackId64.
    private const float MaxOuterDiameter = 1 << 24;

    // Runs on every planet spawn and load, size is the diameter. MAX_PLANET_SIZE is a const compiled into
    // the method, so the diameter is lowered to it instead. Larger planets are rejected by the game as before.
    [HarmonyPrefix]
    private static void CanSpawnPlanetPrefix(MyPlanetGeneratorDefinition planetType, ref float size)
    {
        if (!Config.Current.RaisePlanetSizeLimit)
            return;

        var hillMax = planetType?.HillParams.Max ?? 0f;
        if (size * (1f + hillMax) <= MaxOuterDiameter)
            size = Math.Min(size, MyPlanets.MAX_PLANET_SIZE);
    }
}
