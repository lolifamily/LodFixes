using Sandbox.Engine.Voxels.Planet;
using Sandbox.Game.Entities;
using VRageMath;

namespace ClientPlugin.Fixes;

// The heightmap surface of a planet (without the detail map), which the game's surface queries return: the HUD altitude,
// Surface elevation of scripts, AI blocks, drones, the placement of stations. The game converts the position relative to
// the planet center to float before it projects it, so near the ground of huge planets the height steps by the float
// spacing at the radius (0.25 m at 2000 km, 0.5 m at 6371 km), and by up to 4 m on slopes, where the float texture
// coordinates sample the heightmap off to the side. Here the positions, texture coordinates and pixel are double, the
// heightmap patch is the game's own. RadiusWithDetail adds the detail map: the surface of the voxels, for vegetation.
internal static class HeightmapSurface
{
    // MyPlanet.GetClosestSurfacePointGlobal: the surface on the line from the planet center through position
    public static Vector3D ClosestPoint(MyPlanet planet, Vector3D position)
    {
        var center = planet.WorldMatrix.Translation;
        var local = position - center;
        var distance = local.Length();
        var shape = planet.Provider?.Shape;

        // The game returns the planet center as well without a provider, for an invalid position and for the center
        if (shape == null || !(distance > 0) || !local.IsValid())
            return center;

        return center + local * (Radius(shape, local) / distance);
    }

    // MyPlanetShapeProvider.GetDistanceToSurfaceCacheless with the position relative to the planet center in double
    public static double Distance(MyPlanetShapeProvider shape, Vector3D local)
    {
        var distance = local.Length();
        return distance > 0 && local.IsValid() ? distance - Radius(shape, local) : 0;
    }

    // Distance of the voxel surface from the planet center in the direction of local: the heightmap plus the detail map of
    // steep slopes, as TerrainSdf builds the terrain at LOD 0 (detail from TerrainSdf.DetailOf). What vegetation stands on.
    public static double RadiusWithDetail(MyPlanetShapeProvider shape, Vector3D local, SurfaceDetail detail)
    {
        if (shape.m_heightmap == null)
            return shape.m_radius;

        var face = TerrainSdf.FaceOf(local.X, local.Y, local.Z);
        TerrainSdf.ProjectToFace(local.X, local.Y, local.Z, face, out var u, out var v);
        var height = Height(shape, face, u, v, out var normal);

        // The detail is read on the heightmap surface, where it is not faded by the distance from the ground
        var surface = shape.m_radius + (double)height;
        var point = local * (surface / local.Length());
        return surface + TerrainSdf.Detail(shape, detail, point.X, point.Y, point.Z, u, v, surface, height, 1f, normal.Z);
    }

    // Distance of the surface from the planet center in the direction of local
    private static double Radius(MyPlanetShapeProvider shape, Vector3D local)
    {
        // The shapes the game builds to measure planets have no heightmap, its queries return the radius there
        if (shape.m_heightmap == null)
            return shape.m_radius;

        var face = TerrainSdf.FaceOf(local.X, local.Y, local.Z);
        TerrainSdf.ProjectToFace(local.X, local.Y, local.Z, face, out var u, out var v);
        return shape.m_radius + (double)Height(shape, face, u, v, out _);
    }

    // Height of the heightmap patch above the radius at a position on a face
    private static unsafe float Height(MyPlanetShapeProvider shape, int face, double u, double v, out Vector3 normal)
    {
        TerrainSdf.Pixel(shape, u, v, out var x, out var y, out var s, out var t);

        // A cell of its own like GetValueForPositionCacheless: the coefficient cache is per thread, the queries come from
        // any thread. The patch is in heightmap units, float has the precision for that.
        MyPlanetShapeProvider.Cache.Cell cell = default;
        cell.Coord = new Vector3I(x, y, face);
        shape.CalculateCacheCell(shape.m_heightmap.Faces[face], &cell);
        return shape.SampleHeightBicubic(s, t, ref cell.Gz, out normal) * shape.m_heightRatio + shape.m_minHillHeight;
    }
}
