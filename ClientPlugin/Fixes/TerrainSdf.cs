using System;
using System.Collections.Generic;
using BulletXNA;
using ClientPlugin.Tools;
using Sandbox.Engine.Voxels;
using Sandbox.Engine.Voxels.Planet;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Planet;
using Sandbox.Game.EntityComponents.Renders;
using Sandbox.Game.World;
using VRage.Voxels;
using VRage.Voxels.Clipmap;
using VRageMath;

namespace ClientPlugin.Fixes;

// Planet terrain content (signed distance field), called on the voxel mesher threads.
// Both managed modes are faster than Native on most modern CPUs: VRage.Native is an SSE-only build,
// while the JIT compiles this code for the CPU it runs on.
internal static class TerrainSdf
{
    // The largest float below 1, the way MyGridIntersection keeps a coordinate inside its cell
    private static readonly float BelowOne = MathUtil.NextAfter(1f, float.NegativeInfinity);

    // Replaces m_nativeShape.ReadContentRange(...) in MyPlanetShapeProvider.ReadContentRange,
    // the arguments are the same plus the shape provider owning the native shape
    public static void ReadContentRange(VrPlanetShape native, byte[] dest, int offset, ref Vector3I strides, ref Vector3I start, ref Vector3I end, float lodVoxelSize, int faceHint, MyPlanetShapeProvider shape)
    {
        switch (Config.Current.TerrainSdf)
        {
            case TerrainSdfMode.ManagedFloatNotRecommended:
                FillFloat(shape, dest, offset, strides, start, end, lodVoxelSize, faceHint);
                break;

            case TerrainSdfMode.ManagedDouble:
                FillDouble(shape, dest, offset, strides, start, end, lodVoxelSize, faceHint);
                break;

            case TerrainSdfMode.Native:
            default:
                native.ReadContentRange(dest, offset, ref strides, ref start, ref end, lodVoxelSize, faceHint);
                break;
        }
    }

    // The game's own managed path (MyPlanetShapeProvider.CalculateDistanceFieldInternal), same precision as native. Its cull
    // trusts the bounds of whoever filled the cache cell, which the game's cached lookups leave out (see CachedCell), so
    // the terrain depends on what the thread ran before.
    private static void FillFloat(MyPlanetShapeProvider shape, byte[] dest, int offset, Vector3I strides, Vector3I start, Vector3I end, float lod, int faceHint)
    {
        shape.PrepareCache();
        var origin = start * lod - shape.m_translation;
        var position = origin;
        for (var z = start.Z; z <= end.Z; z++, position.Z += lod)
        {
            position.Y = origin.Y;
            for (var y = start.Y; y <= end.Y; y++, position.Y += lod)
            {
                position.X = origin.X;
                var index = offset + (y - start.Y) * strides.Y + (z - start.Z) * strides.Z;
                for (var x = start.X; x <= end.X; x++, position.X += lod, index += strides.X)
                {
                    var distance = faceHint == -1
                        ? shape.SignedDistanceLocal(position, lod)
                        : shape.SignedDistanceLocal(position, lod, faceHint);
                    dest[index] = ToContent(distance, lod);
                }
            }
        }
    }

    private static void FillDouble(MyPlanetShapeProvider shape, byte[] dest, int offset, Vector3I strides, Vector3I start, Vector3I end, float lod, int faceHint)
    {
        shape.PrepareCache();
        var detail = DetailOf(shape);
        Vector3D center = shape.m_translation;
        for (var z = start.Z; z <= end.Z; z++)
        {
            var pz = z * (double)lod - center.Z;
            for (var y = start.Y; y <= end.Y; y++)
            {
                var py = y * (double)lod - center.Y;
                var index = offset + (y - start.Y) * strides.Y + (z - start.Z) * strides.Z;
                for (var x = start.X; x <= end.X; x++, index += strides.X)
                {
                    var px = x * (double)lod - center.X;
                    dest[index] = ToContent(SignedDistanceDouble(shape, px, py, pz, lod, faceHint, detail), lod);
                }
            }
        }
    }

    // MyPlanetShapeProvider.SignedDistanceLocal with the position kept in double until it is a heightmap pixel.
    // Near the surface a float position has steps of about R / 2^24 (0.1-0.5 m on 2000-6371 km planets). In the distance
    // from the planet center they show as noise on flat ground, in the position on the heightmap as steps on slopes
    // (the position step times the slope).
    // The double projection costs about 10% more than a float one (a double division per voxel), the bulk of the work
    // is the float code shared with ManagedFloatNotRecommended (bicubic heightmap patch, detail map unless smoothed).
    private static float SignedDistanceDouble(MyPlanetShapeProvider shape, double x, double y, double z, float lod, int faceHint, SurfaceDetail detail)
    {
        var distance = Math.Sqrt(x * x + y * y + z * z);
        if (!(distance > 0.1 && distance >= shape.InnerRadius - lod))
            return -lod;

        if (distance > shape.OuterRadius + lod)
            return float.PositiveInfinity;

        var face = faceHint == -1 ? FaceOf(x, y, z) : faceHint;
        ProjectToFace(x, y, z, face, out var u, out var v);

        var height = GetHeight(shape, face, u, v, lod, distance, out var normal);
        height += Detail(shape, detail, x, y, z, u, v, distance, height, lod, normal.Z);
        return ((float)(distance - shape.m_radius) - height) * normal.Z;
    }

    // The detail map the terrain is built with in the current mode: the filtered one of SmoothSlopeDetail in Managed Double,
    // otherwise null, the game's lookup. Vegetation stands on the same terrain (see HeightmapSurface.RadiusWithDetail).
    internal static SurfaceDetail DetailOf(MyPlanetShapeProvider shape) =>
        Config.Current.TerrainSdf == TerrainSdfMode.ManagedDouble && Config.Current.SmoothSlopeDetail ? SurfaceDetail.Of(shape) : null;

    // What the terrain culls leave above the heightmap for the detail map, MyPlanetShapeProvider.m_detailScale, which the
    // game reads but never sets: the detail map's Scale with FixDetailCull (0 on planets without one), otherwise the game's 0
    internal static float DetailCullScale(MyPlanetShapeProvider shape) =>
        Config.Current.FixDetailCull ? Math.Max(shape.m_detail.Scale, 0f) : 0f;

    // Height the detail map adds at a point where the heightmap is height above the radius and has a slope of normalZ,
    // 0 outside the slopes it covers
    internal static float Detail(MyPlanetShapeProvider shape, SurfaceDetail detail, double x, double y, double z, double u, double v, double distance, float height, float lod, float normalZ)
    {
        if (!shape.m_detail.Matches(normalZ))
            return 0;

        if (detail != null)
            return detail.Height(x, y, z, distance, height, lod, normalZ);

        // The game's detail, the nearest texel of the face the point is on. Only the position within one repeat of the
        // detail map is used. From 1 - 2^-25 up a double rounds to 1f, a column or row past the texture, which the lookup
        // reads unchecked, so it stays below 1: the last texel, the one the double is in.
        var dx = u * shape.m_detail.Factor;
        var dy = v * shape.m_detail.Factor;
        return shape.m_detail.GetValue(Math.Min((float)(dx - Math.Floor(dx)), BelowOne), Math.Min((float)(dy - Math.Floor(dy)), BelowOne), normalZ);
    }

    // The face MyCubemapHelpers.CalculateSampleTexcoord picks, ties included
    internal static int FaceOf(double x, double y, double z)
    {
        double ax = Math.Abs(x), ay = Math.Abs(y), az = Math.Abs(z);
        if (ax > ay && ax > az)
            return x > 0 ? 2 : 3;

        if (ax <= ay && ay > az)
            return y > 0 ? 4 : 5;

        return z > 0 ? 1 : 0;
    }

    // MyCubemapHelpers.CalculateTexcoordForFace in double: the position on the face, 0..1 on both axes
    internal static void ProjectToFace(double x, double y, double z, int face, out double u, out double v)
    {
        double scale;
        switch (face)
        {
            case 2:
                scale = 1 / Math.Abs(x);
                u = -z * scale;
                v = -y * scale;
                break;
            case 3:
                scale = 1 / Math.Abs(x);
                u = z * scale;
                v = -y * scale;
                break;
            case 4:
                scale = 1 / Math.Abs(y);
                u = -x * scale;
                v = -z * scale;
                break;
            case 5:
                scale = 1 / Math.Abs(y);
                u = x * scale;
                v = -z * scale;
                break;
            case 1:
                scale = 1 / Math.Abs(z);
                u = x * scale;
                v = -y * scale;
                break;
            default: // 0
                scale = 1 / Math.Abs(z);
                u = -x * scale;
                v = -y * scale;
                break;
        }

        u = (u + 1) * 0.5;
        v = (v + 1) * 0.5;
    }

    // The heightmap patch at a position on the face (x, y) and the position within it (s, t). The last patch of a row or
    // column ends at the face edge (u or v of 1), where the game clamps the texture coordinate to 0.999999 instead. On
    // huge planets that moves the sample meters into the face (2 m at 2000 km, 6.5 m at 6371 km), a seam of wrong heights
    // one voxel wide along every cube edge.
    internal static void Pixel(MyPlanetShapeProvider shape, double u, double v, out int x, out int y, out float s, out float t)
    {
        var last = shape.m_mapResolutionMinusOne;
        var mapX = u * last;
        var mapY = v * last;
        x = Math.Min((int)mapX, last - 1);
        y = Math.Min((int)mapY, last - 1);
        s = (float)(mapX - x);
        t = (float)(mapY - y);
    }

    // MyPlanetShapeProvider.GetValueForPositionInternal from the position on the face in double, so the pixel and the
    // position within it are exact. The Bezier cull compares in double too: the game compares the float distance from
    // the planet center, off by up to R / 2^24, which culls voxels next to the surface.
    private static float GetHeight(MyPlanetShapeProvider shape, int face, double u, double v, float lod, double distance, out Vector3 normal)
    {
        Pixel(shape, u, v, out var x, out var y, out var s, out var t);

        // Bilinear branch, the distance is not used there
        if (lod >= shape.m_pixelSize)
            return shape.SampleHeightBilinear(shape.m_heightmap.Faces[face], lod, s, t, x, y, out normal) * shape.m_heightRatio + shape.m_minHillHeight;

        ref var cell = ref CachedCell(shape, face, x, y);

        // Same reference as the signed distance: R + minHill, not the rounded float InnerRadius.
        // Above the patch the detail map can raise the ground by up to m_detailScale, 0 unless FixDetailCull sets it.
        var ratio = (distance - shape.m_radius - shape.m_minHillHeight) * shape.m_heightRatioRecip;
        var above = (lod + shape.m_detailScale) * shape.m_heightRatioRecip;
        var below = lod * shape.m_heightRatioRecip;
        if (ratio > cell.Max + above)
        {
            normal = Vector3.Backward;
            return float.NegativeInfinity;
        }

        if (ratio < cell.Min - below)
        {
            normal = Vector3.Backward;
            return float.PositiveInfinity;
        }

        return shape.SampleHeightBicubic(s, t, ref cell.Gz, out normal) * shape.m_heightRatio + shape.m_minHillHeight;
    }

    // The coefficient cache cell of a heightmap pixel, in the game's per thread cache (MyPlanetShapeProvider.PrepareCache
    // first). Always with the bounds GetHeight culls by. The game's own cached lookups (vegetation queries, materials) fill
    // cells of the same cache without them, exactly Min 0 and Max 1, on the same worker threads, which would turn that cull
    // off for the pixel depending on what the thread ran before. Such a cell is computed again, one whose bounds really are
    // 0 and 1 only costs that.
    internal static unsafe ref MyPlanetShapeProvider.Cache.Cell CachedCell(MyPlanetShapeProvider shape, int face, int x, int y)
    {
        var coord = new Vector3I(x, y, face);
        ref var cell = ref MyPlanetShapeProvider.m_cache.Cells[MyPlanetShapeProvider.m_cache.CellCoord(ref coord)];
        // ReSharper disable once CompareOfFloatsByEqualityOperator
        if (cell.Coord == coord && cell is not { Min: 0f, Max: 1f }) return ref cell;
        
        cell.Coord = coord;
        fixed (MyPlanetShapeProvider.Cache.Cell* pointer = &cell)
            shape.CalculateCacheCell(shape.m_heightmap.Faces[face], pointer, compouteBounds: true);

        return ref cell;
    }

    // Same mapping as the game: signed distance -lod..+lod becomes content 255..0
    private static byte ToContent(float distance, float lod) =>
        (byte)((MathHelper.Clamp(-distance / lod, -1f, 1f) * 0.5f + 0.5f) * 255f);

    // Applies the detail cull option to the existing planets and regenerates their meshes, collision and raycast cache, so a
    // change of the terrain options applies everywhere (game thread). Edited cells keep the content of the options they were
    // edited with, that is voxel data, not a cache.
    public static void RegenerateTerrain()
    {
        try
        {
            var planets = MySession.Static != null ? MyPlanets.GetPlanets() : null;
            if (planets == null)
                return;

            var sections = new List<MyVoxelPhysics>();
            foreach (var planet in planets)
            {
                // The culls read it on every query, the rebuild below makes the whole planet use it
                if (planet.Provider?.Shape is { } shape)
                    shape.m_detailScale = DetailCullScale(shape);

                ((planet.Render as MyRenderComponentVoxelMap)?.Clipmap as MyVoxelClipmap)?.InvalidateAll();

                // The raycast cache (MyVoxelGeometry) only listens to the storage's RangeChanged, raising that would also
                // mark the planet as edited. Over the full range its handler clears the whole cache and does nothing else.
                var storage = planet.Storage;
                storage?.GetGeometry()?.storage_RangeChanged(Vector3I.Zero, storage.Size - 1, MyStorageDataTypeFlags.Content);

                if (planet.m_physicsShapes == null)
                    continue;

                sections.Clear();
                planet.m_physicsShapes.GetValues(sections);
                foreach (var section in sections)
                    section?.RefreshPhysics(planet.Storage);
            }
        }
        catch (Exception e)
        {
            Log.Error($"Failed to regenerate the planet terrain: {e}");
        }
    }
}
