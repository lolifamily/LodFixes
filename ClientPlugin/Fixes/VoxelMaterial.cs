using System;
using JetBrains.Annotations;
using Sandbox.Engine.Voxels.Planet;
using VRageMath;

namespace ClientPlugin.Fixes;

// Surface material of planet voxels (ores and layers), on the voxel mesher threads through MyPlanetMaterialProviderPatch.
// The game samples each voxel at its center in float storage space and measures it against the heightmap in float. Near
// the ground of huge planets the depth below the surface comes out up to 1.4 m off (0.33 m on average at 6371 km, 0.04 m at
// 2000 km), while the top layer is often one voxel thick: of the voxels in the top 2 m, 16% pick the wrong layer at 6371 km
// and 2% at 2000 km, grass and soil speckle. Here the position stays double up to the heightmap pixel like in TerrainSdf,
// the heightmap patch is the game's.
internal static class VoxelMaterial
{
    // Replaces MyPlanetMaterialProvider.GetPositionParams in GetMaterialForPosition. corner is the min corner of the voxel in
    // storage space, a whole number of voxels below 2^24, which float holds exactly. The game's sample point, the voxel
    // center, is half a voxel further, where float steps by 1 m in the upper half of a 2^24 storage, so the half is added
    // here. The planet center is at a power of two, never at a voxel center, so the distance is never 0.
    // GetMaterialForPosition always passes skipCache false.
    public static void Params(MyPlanetMaterialProvider provider, ref Vector3 corner, float lod, out MyPlanetMaterialProvider.MaterialSampleParams ps, [UsedImplicitly] bool skipCache)
    {
        var shape = provider.m_planetShape;
        Vector3D center = shape.m_translation;
        var p = (Vector3D)corner + 0.5 * lod - center;
        var distance = p.Length();
        var face = TerrainSdf.FaceOf(p.X, p.Y, p.Z);
        TerrainSdf.ProjectToFace(p.X, p.Y, p.Z, face, out var u, out var v);
        TerrainSdf.Pixel(shape, u, v, out var x, out var y, out var s, out var t);
        ref var cell = ref TerrainSdf.CachedCell(shape, face, x, y);

        ps.LodSize = lod;
        ps.DistanceToCenter = (float)distance;
        ps.Gravity = p / distance;
        ps.Face = face;

        // The material maps read the texel the coordinate is in, the game moves the face edge (1) back into the face
        ps.Texcoord = new Vector2(Math.Min((float)u, 0.999999f), Math.Min((float)v, 0.999999f));
        ps.SampledHeight = shape.SampleHeightBicubic(s, t, ref cell.Gz, out ps.Normal) * shape.m_heightRatio + shape.m_minHillHeight;
        ps.SurfaceDepth = (float)(distance - shape.m_radius - ps.SampledHeight) * ps.Normal.Z;
        ps.Latitude = ps.Gravity.Y;

        var longitude = new Vector2(-ps.Gravity.X, -ps.Gravity.Z);
        longitude.Normalize();
        ps.Longitude = -ps.Gravity.X > 0 ? 2 - longitude.Y : longitude.Y;
    }
}
