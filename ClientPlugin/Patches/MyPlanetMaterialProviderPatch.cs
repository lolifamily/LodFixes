using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using ClientPlugin.Fixes;
using ClientPlugin.Tools;
using HarmonyLib;
using Sandbox.Engine.Voxels.Planet;
using VRageMath;

namespace ClientPlugin.Patches;

// Planet surface materials in double (see VoxelMaterial). ReadMaterialRange samples each voxel at its center,
// (minInLod + 0.5f) * lod in float storage space, so it passes the voxel corner instead, which float holds exactly.
// GetMaterialForPosition takes that corner from here on: ReadMaterialRange is its only caller in the game (the other one,
// GetMaterialAtPosition, is never called). Patched in place rather than through a reverse patch copy, which Pulsar
// refuses when it precompiles plugins (disabled for now).
[HarmonyPatch(typeof(MyPlanetMaterialProvider))]
internal static class MyPlanetMaterialProviderPatch
{
    // Applied at startup only, the two patches only work together
    private static bool Prepare() => Config.Current.FixMaterialPrecision;

    // "(minInLod + 0.5f) * num" becomes "(minInLod + 0f) * num"
    [HarmonyTranspiler]
    [HarmonyPatch(nameof(MyPlanetMaterialProvider.ReadMaterialRange))]
    private static IEnumerable<CodeInstruction> ReadMaterialRangeTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var il = instructions.ToList();

        var addFloat = AccessTools.Method(typeof(Vector3I), "op_Addition", [typeof(Vector3I), typeof(float)]);
        // ReSharper disable once CompareOfFloatsByEqualityOperator
        var halves = il.FindAllIndex(ci => ci.opcode == OpCodes.Ldc_R4 && (float)ci.operand == 0.5f);
        if (halves.Count != 1 || halves[0] + 1 >= il.Count || !il[halves[0] + 1].Calls(addFloat))
            throw new CodeInstructionNotFound($"Expected one minInLod + 0.5f in {nameof(MyPlanetMaterialProvider)}.{nameof(MyPlanetMaterialProvider.ReadMaterialRange)}");

        il[halves[0]].operand = 0f;
        return il;
    }

    // "GetPositionParams(ref pos, lodSize, out ps)" becomes "VoxelMaterial.Params(this, ref pos, lodSize, out ps)"
    [HarmonyTranspiler]
    [HarmonyPatch(nameof(MyPlanetMaterialProvider.GetMaterialForPosition), [typeof(Vector3), typeof(float), typeof(byte), typeof(bool)], [ArgumentType.Ref, ArgumentType.Normal, ArgumentType.Out, ArgumentType.Normal])]
    private static IEnumerable<CodeInstruction> GetMaterialForPositionTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var il = instructions.ToList();

        var getParams = AccessTools.Method(typeof(MyPlanetMaterialProvider), nameof(MyPlanetMaterialProvider.GetPositionParams));
        var calls = il.FindAllIndex(ci => ci.Calls(getParams));
        if (calls.Count != 1)
            throw new CodeInstructionNotFound($"Expected one call to {nameof(MyPlanetMaterialProvider.GetPositionParams)} in {nameof(MyPlanetMaterialProvider)}.{nameof(MyPlanetMaterialProvider.GetMaterialForPosition)}, found {calls.Count}");

        // The arguments are on the stack, the static method takes the instance as its first one
        il[calls[0]].opcode = OpCodes.Call;
        il[calls[0]].operand = AccessTools.Method(typeof(VoxelMaterial), nameof(VoxelMaterial.Params));
        return il;
    }
}
