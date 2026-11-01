using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ClientPlugin.Fixes;
using ClientPlugin.Tools;
using HarmonyLib;
using Sandbox.Game.AI.Pathfinding.RecastDetour;
using VRageMath;

namespace ClientPlugin.Patches;

// Bot navmesh ground cut to the heights Recast keeps (see NavmeshTerrain). SetTerrainLimits only gets the tile, so its call
// in AddGround, its only caller, gets the border and the navmesh center added.
[HarmonyPatch(typeof(MyNavigationInputMesh), nameof(MyNavigationInputMesh.AddGround))]
internal static class MyNavigationInputMeshGroundPatch
{
    // Applied at startup only
    private static bool Prepare() => Config.Current.FixNavmeshTerrainRange;

    // "SetTerrainLimits(ref obb)" becomes "NavmeshTerrain.TerrainLimits(this, ref obb, border, originPosition)"
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> AddGroundTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var il = instructions.ToList();
        var border = SingleParameter(original, typeof(float));
        var center = SingleParameter(original, typeof(Vector3D));
        var limits = AccessTools.Method(typeof(MyNavigationInputMesh), nameof(MyNavigationInputMesh.SetTerrainLimits));
        var calls = il.FindAllIndex(ci => ci.Calls(limits));
        if (calls.Count != 1 || border < 0 || center < 0)
            throw new CodeInstructionNotFound($"Expected one call to {nameof(MyNavigationInputMesh.SetTerrainLimits)}, and one float and one {nameof(Vector3D)} parameter in {nameof(MyNavigationInputMesh)}.{nameof(MyNavigationInputMesh.AddGround)}");

        // The border and the center go on top of the instance and the tile address
        var i = calls[0];
        il[i].operand = AccessTools.Method(typeof(NavmeshTerrain), nameof(NavmeshTerrain.TerrainLimits));
        il.InsertRange(i, [
            CodeInstruction.LoadArgument(border).MoveLabelsFrom(il[i]),
            CodeInstruction.LoadArgument(center)
        ]);

        return il;
    }

    // Argument index of the one parameter of the type, -1 when there is none or more than one
    private static int SingleParameter(MethodBase method, Type type)
    {
        var matches = method.GetParameters().Where(p => p.ParameterType == type).ToList();
        return matches.Count == 1 ? matches[0].Position + (method.IsStatic ? 0 : 1) : -1;
    }
}
