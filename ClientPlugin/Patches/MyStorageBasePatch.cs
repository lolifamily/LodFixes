using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sandbox.Engine.Voxels;
using VRageMath;

namespace ClientPlugin.Patches;

// The other half of the octree key fix, under the same option (see MyOctreeStoragePatch). MyCellCoord.PackId64 keeps
// 4 bits of the LOD, so in an octree of height 16+ the cells of LOD 16 and up, the top of the tree, share their keys with
// the cells 16 LODs down at the same coordinates. Coordinates that small are all in the corner at the storage origin, in
// deep space and not at the planet center, and no cell is there until a write reaches it. A write splits every cell it
// enters into all 8 children, so one within 2^(height - 11) voxels of the origin (512 at 2^24) creates such a cell: the
// one at the origin 16 LODs below the root has the root's key, every read takes it for the root and returns the untouched
// terrain of the data provider, so all edits of the planet vanish. Further in, the write throws on a duplicate key halfway
// through and leaves the tree broken. Explosions do it as well, they cut every voxel map whose box holds them, also where
// there is nothing to cut.
// The keys are saved, and computed in traversals generic over value types, so they stay as they are and writes reaching
// that corner are dropped. Around a planet it is empty space, at least 0.73 R above the highest ground.
[HarmonyPatch]
internal static class MyStorageBasePatch
{
    // Every write of the game goes through here, with one of its two voxel operators: WriteRange (voxel hands, drills,
    // mining) and CutOutSphereFast (explosions). Each value type instantiation has code of its own, so each is patched.
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var method = AccessTools.Method(typeof(MyStorageBase), nameof(MyStorageBase.ExecuteOperationFast));
        return new[] { typeof(MyStorageDataWriteOperator), typeof(MyVoxelGenerator.CutOutSphere) }
            .Select(op => method.MakeGenericMethod(op));
    }

    // Checked on every write, like the load fix on every load
    [HarmonyPrefix]
    private static bool ExecuteOperationFastPrefix(MyStorageBase __instance, ref Vector3I voxelRangeMin, ref Vector3I voxelRangeMax) =>
        !(Config.Current.FixOctreeLoad && __instance is MyOctreeStorage { m_treeHeight: >= 16 } octree &&
          ReachesCorner(octree.m_treeHeight, voxelRangeMin, voxelRangeMax));

    // Whether the range overlaps [0, 2^(height - 11)) on every axis
    private static bool ReachesCorner(int height, Vector3I min, Vector3I max)
    {
        var corner = 1 << (height - 11);
        return min.X < corner && min.Y < corner && min.Z < corner && max.X >= 0 && max.Y >= 0 && max.Z >= 0;
    }
}
