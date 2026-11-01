using System;
using System.Threading;
using BulletXNA;
using ClientPlugin.Tools;
using Sandbox.Engine.Voxels.Planet;
using Sandbox.Game.Entities.Planet;
using Sandbox.Game.World;
using Sandbox.Game.WorldEnvironment;
using VRage.Library.Collections;
using VRage.Library.Utils;
using VRageMath;

namespace ClientPlugin.Fixes;

// Height of the trees, bushes, forageables and boulders of planets. The game puts an item on the heightmap at its float
// position in storage space (MyPlanetStorageProvider.ComputeCombinedMaterialAndSurface): on huge planets 0.5-2 m off and
// more often above the ground than below, and on slopes the downhill side of a wide base floats. Here the item keeps
// everything the game picked from that position (its definition, rotation and, by its index, model), so saves, cut trees
// and multiplayer see the same items. Only its height is recomputed: on the terrain the voxels are built from, lowered on
// slopes until every direction around it stands on the ground as on flat ground, then moved up or down within what its
// model has to spare (see ModelFootprint). It is stored so that the game's float rounding of the position does not lift
// it (see Store).
internal static class VegetationPlacement
{
    // Two salts, different from what the game feeds the same seed (the item choice and its rotation) and from each other
    private const int SaltA = unchecked((int)0x9E3779B9);
    private const int SaltB = unchecked((int)0x85EBCA6B);

    private static int failureLogged;

    // Replaces m_items.Add(item) in MyProceduralLogicalSector.ScanItems, m_totalSpawned is the index the item gets. Runs on
    // the environment threads with the sector locked: a failure keeps the game's position, thrown out of ScanItems it would
    // leave the sector half built.
    public static void Add(MyList<ItemInfo> items, ItemInfo item, MyProceduralLogicalSector sector)
    {
        if (Config.Current.FixVegetationPlacement)
        {
            try
            {
                item.Position = Place(sector, item, sector.m_totalSpawned);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Logged once, this runs for every item of every sector
                if (Interlocked.Exchange(ref failureLogged, 1) == 0)
                    Log.Error($"Vegetation placement failed, the failing items stay where the game puts them: {e}");
            }
        }

        items.Add(item);
    }

    // The item's position relative to the sector's WorldPos, as the game's
    private static Vector3 Place(MyProceduralLogicalSector sector, ItemInfo item, int index)
    {
        if (sector.m_provider.Owner is not MyPlanetEnvironmentComponent environment)
            return item.Position;

        var planet = environment.Planet;
        var shape = planet.Provider?.Shape;
        if (shape?.m_heightmap == null)
            return item.Position;

        // Only the direction of the game's point is used, its error sideways is just another random spot
        var center = planet.WorldMatrix.Translation;
        var up = Vector3D.Normalize(sector.WorldPos + item.Position - center);
        var detail = TerrainSdf.DetailOf(shape);
        var anchor = up * HeightmapSurface.RadiusWithDetail(shape, up, detail);

        var footprint = ModelFootprint.Of(environment, item, index);
        var height = Contact(footprint, shape, detail, anchor, up, item.Rotation) - footprint.ReferenceHeight
                     + footprint.Capacity * Config.Current.VegetationVariation * Variation(item.Position);

        return Store(center + anchor + up * height - sector.WorldPos, up);
    }

    // Where the reference plane of the model goes so that every direction around it stands on the ground as on flat
    // ground, relative to the anchor: per direction the highest the plane can be with one of its feet on the ground, the
    // lowest of those, less the same on flat ground. 0 on flat ground, below it on a slope, above it in a hollow.
    private static double Contact(ModelFootprint footprint, MyPlanetShapeProvider shape, SurfaceDetail detail, Vector3D anchor, Vector3D up, Quaternion rotation)
    {
        var lowest = double.PositiveInfinity;
        foreach (var direction in footprint.Feet)
        {
            var highest = double.NegativeInfinity;
            foreach (var foot in direction)
                highest = Math.Max(highest, Ground(shape, detail, anchor, up, Vector3D.Transform(new Vector3D(foot.X, 0, foot.Z), rotation)) + foot.Depth);

            lowest = Math.Min(lowest, highest);
        }

        return lowest - footprint.Embed;
    }

    // How far the ground is above the plane through the anchor, at an offset from the anchor laid into that plane
    private static double Ground(MyPlanetShapeProvider shape, SurfaceDetail detail, Vector3D anchor, Vector3D up, Vector3D offset)
    {
        var at = anchor + offset - up * Vector3D.Dot(offset, up);
        return HeightmapSurface.RadiusWithDetail(shape, at, detail) - at.Length();
    }

    // -1..1 per item from the game's seed of it, the hash of its float position. The sum of two independent samples is
    // triangular: most items stay close to the ground, few reach the limits of their model.
    private static float Variation(Vector3 gamePosition)
    {
        var seed = gamePosition.GetHashCode();
        return MyHashRandomUtils.UniformFloatFromSeed(seed ^ SaltA) + MyHashRandomUtils.UniformFloatFromSeed(seed ^ SaltB) - 1f;
    }

    // The position as the game keeps it: a float relative to the sector's origin on the cube face plane, about 0.29 R below
    // the ground. That far out, rounding to the nearest float moves a point by up to half a float step per axis (3 cm at
    // 2000 km, 6 cm from 3800 km on), here and once more where AddView moves the item to the origin of a view. So the point
    // is lowered by what AddView's rounding can raise it, and every axis is rounded to the float on the side that does not
    // raise it along up: at most a few millimetres above its height (where AddView's sum crosses a power of two and its
    // step doubles) instead of up to 13 cm.
    private static Vector3 Store(Vector3D offset, Vector3D up)
    {
        var margin = 0.5 * (Step(offset.X) * Math.Abs(up.X) + Step(offset.Y) * Math.Abs(up.Y) + Step(offset.Z) * Math.Abs(up.Z));
        var target = offset - up * margin;
        return new Vector3(Toward(target.X, -up.X), Toward(target.Y, -up.Y), Toward(target.Z, -up.Z));
    }

    // The value as the float on the side of direction: the one below it for a negative direction, above it for a positive
    // one. The nearest for 0, that axis does not move the point along up.
    private static float Toward(double value, double direction)
    {
        var f = (float)value;
        return direction switch
        {
            < 0 when f > value => MathUtil.NextAfter(f, float.NegativeInfinity),
            > 0 when f < value => MathUtil.NextAfter(f, float.PositiveInfinity),
            _ => f
        };
    }

    // Distance from the float of the value to the next float away from 0
    private static double Step(double value)
    {
        var f = Math.Abs((float)value);
        return MathUtil.NextAfter(f, float.PositiveInfinity) - f;
    }

    // Game thread. What the Refresh Sectors button of the environment debug screen does: the sectors close, saving their cut
    // trees and foraged plants, and load again around the camera and grids, placed with the current options. The items keep
    // their identity, so nothing cut comes back.
    public static void Refresh()
    {
        try
        {
            var planets = MySession.Static != null ? MyPlanets.GetPlanets() : null;
            if (planets == null)
                return;

            foreach (var planet in planets)
                planet.Components.Get<MyPlanetEnvironmentComponent>()?.CloseAll();
        }
        catch (Exception e)
        {
            Log.Error($"Failed to refresh the planet vegetation: {e}");
        }
    }
}
