using System;
using System.Collections.Generic;
using Sandbox.Engine.Voxels.Planet;
using HeightmapLevel = Sandbox.Engine.Voxels.Planet.MyHeightmapFace.HeightmapLevel;
using HeightmapNode = Sandbox.Engine.Voxels.Planet.MyHeightmapFace.HeightmapNode;

namespace ClientPlugin.Fixes;

// Min/max (pruning) tree of a heightmap face, built once per face while a heightmap loads.
// The game builds it only at 8 * 4^k pixels (2048, 8192): it cuts the face into 8 pixel leaves, while QueryHeight and
// GetBounds take leaf i to cover texture coordinates [i / res, (i + 1) / res), and the two line up only at those sizes.
// Other sizes (4096 is common on modded planets) get no tree and the log says "Cannot build prunning tree", then
// QueryHeight and QueryLine report every box and line within the hill range as touching the terrain.
// Here the leaves are built from that texture coordinate range, which works for any size and keeps the game's queries.
internal static class HeightmapPruning
{
    // Replaces MyHeightmapFace.CreatePruningTree. The game's layout: level 0 holds the leaves, every level up has a
    // quarter of the cells per axis, the 1x1 level is Root. Level 0 has 4^k cells per axis, picked for leaves of 4 to
    // 16 pixels around the game's 8, so the levels always divide evenly. At 8 * 4^k pixels (32 and up) this builds
    // exactly the game's tree.
    public static void Build(MyHeightmapFace face)
    {
        var leaves = 1;
        while (face.Resolution > leaves * 2 * HeightmapNode.HEIGHTMAP_LEAF_SIZE)
            leaves *= HeightmapNode.HEIGHTMAP_BRANCH_FACTOR;

        var tree = new List<HeightmapLevel>();
        var level = BuildLeaves(face, leaves);
        while (level.Res > 1)
        {
            tree.Add(level);
            level = Coarsen(level);
        }

        face.Root = level.Nodes[0];
        face.PruningTree = tree.ToArray();
    }

    // Leaf i covers texture coordinates [i / leaves, (i + 1) / leaves) on each axis, pixel j sits at t = j / (size - 1).
    // Like the game's leaves, it takes the pixels within that range plus one on each side: ceil(start) - 1 to
    // floor(end) + 1, which stays within -1 and size. Data holds exactly those rows and columns (the borders are the
    // edges of the neighbouring faces), so every read below is inside it. Neither tree bounds the bicubic terrain
    // exactly, Catmull-Rom overshoots its pixels, and wider leaves would only make the queries cull less.
    private static unsafe HeightmapLevel BuildLeaves(MyHeightmapFace face, int leaves)
    {
        var span = face.Resolution - 1;
        var first = new int[leaves];
        var last = new int[leaves];
        for (var i = 0; i < leaves; i++)
        {
            first[i] = (int)(((long)i * span + leaves - 1) / leaves) - 1;
            last[i] = (int)((long)(i + 1) * span / leaves) + 1;
        }

        // Out of memory is the only way the build can fail, and this is its one big allocation: 8 bytes per leaf,
        // 128 MiB per face at 4096 leaves (faces over 16384 pixels). Not caught, the game's heightmap loading lets
        // out of memory through as well.
        var nodes = new HeightmapNode[leaves * leaves];

        // Pixel (x, y) is at (y + 1) * stride + x + 1
        var stride = face.RowStride;
        var columnMin = new ushort[stride];
        var columnMax = new ushort[stride];
        fixed (ushort* min = columnMin, max = columnMax)
        {
            for (var y = 0; y < leaves; y++)
            {
                ColumnExtremes(face.Data + (first[y] + 1) * stride, stride, last[y] - first[y] + 1, min, max);
                for (var x = 0; x < leaves; x++)
                    nodes[y * leaves + x] = LeafNode(min, max, first[x] + 1, last[x] + 1);
            }
        }

        return new HeightmapLevel { Nodes = nodes, Res = (uint)leaves };
    }

    // Lowest and highest value of each column over a run of rows
    private static unsafe void ColumnExtremes(ushort* row, int stride, int rows, ushort* min, ushort* max)
    {
        for (var x = 0; x < stride; x++)
            min[x] = max[x] = row[x];

        for (var y = 1; y < rows; y++)
        {
            row += stride;
            for (var x = 0; x < stride; x++)
            {
                min[x] = Math.Min(min[x], row[x]);
                max[x] = Math.Max(max[x], row[x]);
            }
        }
    }

    // Node of the column extremes from first to last, scaled to 0..1 like the game's nodes
    private static unsafe HeightmapNode LeafNode(ushort* min, ushort* max, int first, int last)
    {
        int low = ushort.MaxValue, high = 0;
        for (var x = first; x <= last; x++)
        {
            low = Math.Min(low, min[x]);
            high = Math.Max(high, max[x]);
        }

        return new HeightmapNode { Min = low * MyCubemapHelpers.UshortRecip, Max = high * MyCubemapHelpers.UshortRecip };
    }

    // Each node of the next level up covers 4x4 nodes of this one
    private static HeightmapLevel Coarsen(HeightmapLevel level)
    {
        var branch = HeightmapNode.HEIGHTMAP_BRANCH_FACTOR;
        var res = (int)level.Res;
        var coarseRes = res / branch;
        var nodes = new HeightmapNode[coarseRes * coarseRes];
        for (var i = 0; i < nodes.Length; i++)
            nodes[i] = new HeightmapNode { Min = float.PositiveInfinity, Max = float.NegativeInfinity };

        for (var y = 0; y < res; y++)
        {
            for (var x = 0; x < res; x++)
            {
                var child = level.Nodes[y * res + x];
                ref var node = ref nodes[y / branch * coarseRes + x / branch];
                node.Min = Math.Min(node.Min, child.Min);
                node.Max = Math.Max(node.Max, child.Max);
            }
        }

        return new HeightmapLevel { Nodes = nodes, Res = (uint)coarseRes };
    }
}
