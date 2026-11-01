using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ClientPlugin.Tools;
using HarmonyLib;
using Sandbox.Engine.Voxels;
using VRage.Voxels;

namespace ClientPlugin.Patches;

// The geometry cache keys 8 voxel cells with MyCellCoord.PackId64, which has 20 bits per axis. A 2^24 voxel
// storage (the largest RaisePlanetSizeLimit allows) has 2^21 cells per axis, so cells in its upper half
// overwrite each other's meshes. The keys are in memory only, saves are not affected.
[HarmonyPatch]
internal static class MyVoxelGeometryPatch
{
    // Applied at startup only, the cached keys depend on it
    private static bool Prepare() => Config.Current.FixRaycastCache;

    // Every method keying m_cellsByCoordinate or m_coordinateToMesh. ComputeIsEmptyLookup packs
    // coordinates divided by 4, which fit, so it keeps the game's keys.
    private static IEnumerable<MethodBase> TargetMethods() =>
        new[]
            {
                nameof(MyVoxelGeometry.TryGetCell),
                nameof(MyVoxelGeometry.TryGetMesh),
                nameof(MyVoxelGeometry.SetMesh),
                nameof(MyVoxelGeometry.storage_RangeChanged),
                nameof(MyVoxelGeometry.GetCell)
            }
            .Select(name => AccessTools.Method(typeof(MyVoxelGeometry), name));

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var il = instructions.ToList();
        var packId64 = AccessTools.Method(typeof(MyCellCoord), nameof(MyCellCoord.PackId64));
        var calls = il.FindAllIndex(ci => ci.Calls(packId64));
        if (calls.Count == 0)
            throw new CodeInstructionNotFound($"No call to {nameof(MyCellCoord)}.{nameof(MyCellCoord.PackId64)} in {nameof(MyVoxelGeometry)}.{original.Name}");

        // The MyCellCoord address is already on the stack, which is what a ref parameter takes
        foreach (var i in calls)
            il[i].operand = AccessTools.Method(typeof(MyVoxelGeometryPatch), nameof(CellKey));

        return il;
    }

    // Only LOD 0 cells are stored, so the LOD bits go to the coordinates (21 bits each) and
    // the top bit keeps lookups of other LODs from matching a stored cell, like the game's keys do
    private static ulong CellKey(ref MyCellCoord cell) =>
        (cell.Lod == 0 ? 0UL : 1UL << 63)
        | (ulong)(uint)cell.CoordInLod.X << 42
        | (ulong)(uint)cell.CoordInLod.Y << 21
        | (uint)cell.CoordInLod.Z;
}
