using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using ClientPlugin.Tools;
using HarmonyLib;
using Sandbox.Engine.Voxels;
using VRage.Voxels;

namespace ClientPlugin.Patches;

// ReadRange descends into a child even when it is a single voxel of the requested LOD. A child node then shifts by -1,
// which C# masks to 31, so it writes nothing and edited terrain turns into holes from LOD 5 on (a node at the storage
// origin floods the whole request instead). The parent already stores the filtered value of each child, the value the
// octree returns at coarser LODs anyway, so that is used once a child is no larger than a voxel. This also skips the
// LOD 4 read inside micro octree leaves, which has the same -1 shift and returns only the first octant.
// The private overload is the only ReadRange declared here, the public ones are declared on MyStorageBase.
[HarmonyPatch(typeof(MyOctreeStorage), nameof(MyOctreeStorage.ReadRange))]
internal static class MyOctreeStorageReadRangePatch
{
    private static bool Prepare() => Config.Current.FixOctreeLodRead;

    // "lodIndex < cell.Lod && node.HasChild(i)" becomes "lodIndex < cell.Lod - 1 && node.HasChild(i)"
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> ReadRangeTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var il = instructions.ToList();

        var hasChild = AccessTools.Method(typeof(MyOctreeNode), nameof(MyOctreeNode.HasChild));
        var lod = AccessTools.Field(typeof(MyCellCoord), nameof(MyCellCoord.Lod));
        var calls = il.FindAllIndex(ci => ci.Calls(hasChild));
        if (calls.Count != 1 || calls[0] < 4 || !il[calls[0] - 4].LoadsField(lod) || !il[calls[0] - 3].Branches(out _))
            throw new CodeInstructionNotFound($"Expected one {nameof(MyCellCoord)}.{nameof(MyCellCoord.Lod)} check before {nameof(MyOctreeNode)}.{nameof(MyOctreeNode.HasChild)} in {nameof(MyOctreeStorage)}.ReadRange");

        il.InsertRange(calls[0] - 3, [
            new CodeInstruction(OpCodes.Ldc_I4_1),
            new CodeInstruction(OpCodes.Sub)
        ]);

        return il;
    }
}
