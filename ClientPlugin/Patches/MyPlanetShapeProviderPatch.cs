using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using ClientPlugin.Fixes;
using ClientPlugin.Tools;
using HarmonyLib;
using Sandbox.Engine.Voxels.Planet;
using VRage.Voxels;

namespace ClientPlugin.Patches;

[HarmonyPatch(typeof(MyPlanetShapeProvider), nameof(MyPlanetShapeProvider.ReadContentRange))]
internal static class MyPlanetShapeProviderPatch
{
    // Patching VrPlanetShape.ReadContentRange directly is unreliable, the JIT inlines that small
    // wrapper into this method, so its call site is redirected to TerrainSdf instead
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> ReadContentRangeTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var il = instructions.ToList();

        var nativeRead = AccessTools.Method(typeof(VrPlanetShape), nameof(VrPlanetShape.ReadContentRange));
        var calls = il.FindAllIndex(ci => ci.Calls(nativeRead));
        if (calls.Count != 1)
            throw new CodeInstructionNotFound($"Expected one call to {nameof(VrPlanetShape)}.{nameof(VrPlanetShape.ReadContentRange)}, found {calls.Count}");

        // Pass this (the shape provider) as an extra last argument
        var i = calls[0];
        il.Insert(i, new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(il[i]));
        il[i + 1].opcode = OpCodes.Call;
        il[i + 1].operand = AccessTools.Method(typeof(TerrainSdf), nameof(TerrainSdf.ReadContentRange));

        return il;
    }
}
