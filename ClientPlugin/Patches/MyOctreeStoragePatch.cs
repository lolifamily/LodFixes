using System.Collections.Generic;
using HarmonyLib;
using Sandbox.Engine.Voxels;
using VRage.Voxels;
using VRageMath;

namespace ClientPlugin.Patches;

// MyCellCoord.PackId64 keeps 4 bits of the LOD, so octree cells of LOD 16+ (storages of 2^20+ voxels) get truncated
// keys. Writing and lookup use the same keys, so apart from writes in the storage corner (see MyStorageBasePatch, same
// option) only loading breaks: provider leaves are rebuilt from their keys there and end up in the storage corner. The
// saved format stays the game's, the true LOD is recovered from the tree structure.
[HarmonyPatch(typeof(MyOctreeStorage), "LoadInternal")] // Not nameof: Pulsar does not publicize virtual methods
internal static class MyOctreeStoragePatch
{
    [HarmonyPostfix]
    private static void LoadInternalPostfix(MyOctreeStorage __instance)
    {
        var height = __instance.m_treeHeight;
        if (height < 16 || !Config.Current.FixOctreeLoad)
            return;

        FixProviderLeaves(__instance.m_contentLeaves, __instance.m_contentNodes, height);
        FixProviderLeaves(__instance.m_materialLeaves, __instance.m_materialNodes, height);
    }

    // Walks the tree from the root, so the true LOD of every cell is known, looking cells up with the game's truncated
    // keys like MyOctreeStorage.ReadRange does: a leaf first, otherwise a node with 8 children. Depth first like
    // ReadRange too, so at most 7 siblings per level wait on the stack.
    private static unsafe void FixProviderLeaves(Dictionary<ulong, IMyOctreeLeafNode> leaves, Dictionary<ulong, MyOctreeNode> nodes, int height)
    {
        var stack = stackalloc MyCellCoord[MySparseOctree.EstimateStackSize(height)];
        var count = 0;
        stack[count++] = new MyCellCoord(height, Vector3I.Zero);
        while (count > 0)
        {
            var cell = stack[--count];
            if (leaves.TryGetValue(cell.PackId64(), out var leaf))
            {
                if (cell.Lod >= 16 && leaf is MyProviderLeaf provider)
                    provider.m_cell = new MyCellCoord(cell.Lod + 4, cell.CoordInLod);
                continue;
            }

            if (cell.Lod == 0 || !nodes.ContainsKey(new MyCellCoord(cell.Lod - 1, cell.CoordInLod).PackId64()))
                continue;

            for (var i = 0; i < 8; i++)
            {
                MyOctreeStorage.ComputeChildCoord(i, out var offset);
                stack[count++] = new MyCellCoord(cell.Lod - 1, cell.CoordInLod * 2 + offset);
            }
        }
    }
}
