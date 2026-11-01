using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Tools;
using Sandbox.Game.EntityComponents.Renders;
using VRage.Voxels.Clipmap;

namespace ClientPlugin.Fixes;

// Voxel terrain LOD view ranges through the engine's own clipmap settings, called on the game thread.
// Every source of clipmap settings is replaced: the voxel quality presets, the fallback of unset groups
// and the groups already filled from a preset, so voxel quality changes need no handling here.
internal static class TerrainLodRanges
{
    // The game's values, every update is derived from these
    private static readonly MyVoxelClipmapSettings[] OriginalNormal = MyVoxelClipmapSettingsPresets.NormalSettings.Select(DeepCopy).ToArray();
    private static readonly MyVoxelClipmapSettings[] OriginalPlanet = MyVoxelClipmapSettingsPresets.PlanetSettings.Select(DeepCopy).ToArray();
    private static readonly MyVoxelClipmapSettings OriginalDefault = DeepCopy(MyVoxelClipmapSettings.Default);

    // From Plugin.Init, which runs before the engine first applies the presets, and on config changes
    public static void Update()
    {
        try
        {
            var lod = Config.Current.TerrainLod;
            var scale = Config.Current.TerrainLodRangeScale;

            // Replaced ranges array -> its replacement, the groups share the arrays of the presets they were set from
            var replaced = new Dictionary<int[], MyVoxelClipmapSettings>();
            Replace(MyVoxelClipmapSettingsPresets.NormalSettings, OriginalNormal, lod, scale, replaced);
            Replace(MyVoxelClipmapSettingsPresets.PlanetSettings, OriginalPlanet, lod, scale, replaced);

            var fallback = Scaled(OriginalDefault, lod, scale);
            replaced[MyVoxelClipmapSettings.Default.LodRanges] = fallback;
            MyVoxelClipmapSettings.Default = fallback;

            var groups = MyVoxelClipmapSettings.m_settingsPerGroup;
            foreach (var group in groups.Keys.ToArray())
            {
                if (replaced.TryGetValue(groups[group].LodRanges, out var settings))
                    groups[group] = settings;
            }

            // Same path as a voxel quality change, the clipmaps rebuild on their next idle update
            MyRenderComponentVoxelMap.RefreshClipmapSettings();
        }
        catch (Exception e)
        {
            Log.Error($"Updating terrain LOD ranges failed: {e}");
        }
    }

    private static void Replace(MyVoxelClipmapSettings[] presets, MyVoxelClipmapSettings[] originals, int lod, float scale, Dictionary<int[], MyVoxelClipmapSettings> replaced)
    {
        for (var i = 0; i < presets.Length; i++)
        {
            var settings = Scaled(originals[i], lod, scale);
            replaced[presets[i].LodRanges] = settings;
            presets[i] = settings;
        }
    }

    // Multiplies the range of the given LOD and every coarser one, scale 1 gives the game's ranges.
    // CellSizeLg2 and MinSize are kept: a different LOD count makes a clipmap drop its rings without disposing them.
    private static MyVoxelClipmapSettings Scaled(MyVoxelClipmapSettings settings, int lod, float scale)
    {
        // Below 1 the ranges could lose the ascending order the clipmap requires
        scale = Math.Max(1f, scale);

        var ranges = (int[])settings.LodRanges.Clone();
        for (var i = lod; i < ranges.Length; i++)
            ranges[i] = (int)Math.Min(int.MaxValue, ranges[i] * (double)scale);

        settings.LodRanges = ranges;
        return settings;
    }

    // The ranges array is shared by reference with the groups and the clipmaps
    private static MyVoxelClipmapSettings DeepCopy(MyVoxelClipmapSettings settings)
    {
        settings.LodRanges = (int[])settings.LodRanges.Clone();
        return settings;
    }
}
