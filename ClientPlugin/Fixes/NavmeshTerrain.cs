using System;
using JetBrains.Annotations;
using Sandbox.Game.AI.Pathfinding.RecastDetour;
using Sandbox.Game.Entities;
using VRage.Voxels;
using VRageMath;

namespace ClientPlugin.Fixes;

// Terrain of bot navigation meshes (wolves, spiders, humanoid bots): MyNavigationInputMesh meshes it around each navmesh
// tile for Recast, relative to the navmesh center and up against gravity there
internal static class NavmeshTerrain
{
    // Replaces SetTerrainLimits(ref obb) in AddGround. The game swaps the tile height for the height range of the terrain,
    // in mountains and on huge planets far taller than the tile, while Recast keeps only the tile height around the navmesh
    // center. Here the two are intersected in the frame of the center, with a terrain cell to spare on both ends.
    public static bool TerrainLimits(MyNavigationInputMesh mesh, ref MyOrientedBoundingBoxD obb, float border, Vector3D center)
    {
        var tile = obb;
        if (!mesh.SetTerrainLimits(ref obb))
            return false;

        // Up of the center as AddVoxelMesh computes it, without gravity the game's range stays
        Vector3D centerUp = -Vector3.Normalize(mesh.m_navigationArea.GetGravity(center));
        Vector3D up = obb.Orientation.Up;
        var cos = Vector3D.Dot(up, centerUp);
        if (!(cos > 0))
            return true;

        // Heights run along the tile's up from the planet center: height s is s * cos in the frame of the center, the tile's
        // width with its border adds up to spread, and the spare cell covers vertices the game rounds into the tile height
        var planet = mesh.m_planet.PositionComp.GetPosition();
        var spread = (tile.HalfExtent.X + border) * Math.Abs(Vector3D.Dot(obb.Orientation.Right, centerUp))
                     + (tile.HalfExtent.Z + border) * Math.Abs(Vector3D.Dot(obb.Orientation.Forward, centerUp));
        var reach = tile.HalfExtent.Y + spread + MyVoxelConstants.GEOMETRY_CELL_SIZE_IN_VOXELS * MyVoxelConstants.VOXEL_SIZE_IN_METRES;
        var height = Vector3D.Dot(center - planet, centerUp);
        var mid = Vector3D.Dot(obb.Center - planet, up);
        var low = Math.Max(mid - obb.HalfExtent.Y, (height - reach) / cos);
        var high = Math.Min(mid + obb.HalfExtent.Y, (height + reach) / cos);
        if (low > high)
            return false;

        obb.Center = planet + up * ((low + high) / 2);
        obb.HalfExtent.Y = (high - low) / 2;
        return true;
    }

    // Replaces AddMeshTriangles in AddVoxelMesh. The game adds each vertex to its cell origin in float storage space and
    // subtracts the navmesh center there, which far from the storage corner rounds the vertex coarsely. Here the cell origin
    // and the center cancel in double first.
    public static void AddMeshTriangles(MyIsoMesh mesh, [UsedImplicitly] Vector3 offset, Matrix rotation, Matrix ownRotation, WorldVerticesInfo worldVertices, MyVoxelBase voxel, Vector3D center)
    {
        worldVertices.Vertices.EnsureCapacity(mesh.VerticesCount);
        worldVertices.Triangles.EnsureCapacity(mesh.TrianglesCount);
        for (var i = 0; i < mesh.TrianglesCount; i++)
        {
            var triangle = mesh.Triangles[i];
            worldVertices.Triangles.Add(worldVertices.VerticesMaxValue + triangle.V2);
            worldVertices.Triangles.Add(worldVertices.VerticesMaxValue + triangle.V1);
            worldVertices.Triangles.Add(worldVertices.VerticesMaxValue + triangle.V0);
        }

        // ownRotation * (cell origin + vertex) - (center - storage corner) as the game, with the large terms in double
        var origin = (Vector3)(Vector3D.TransformNormal(mesh.PositionOffset, ownRotation) - (center - voxel.PositionLeftBottomCorner));
        for (var j = 0; j < mesh.VerticesCount; j++)
        {
            var position = mesh.Positions[j] * mesh.PositionScale;
            Vector3.Transform(ref position, ref ownRotation, out position);
            position += origin;
            Vector3.Transform(ref position, ref rotation, out position);
            worldVertices.Vertices.Add(position);
        }

        worldVertices.VerticesMaxValue += mesh.VerticesCount;
    }
}
