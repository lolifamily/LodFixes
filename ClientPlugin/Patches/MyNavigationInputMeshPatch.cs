using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ClientPlugin.Fixes;
using ClientPlugin.Tools;
using HarmonyLib;
using Sandbox.Game.AI.Pathfinding.RecastDetour;
using Sandbox.Game.Entities;
using VRageMath;

namespace ClientPlugin.Patches;

// Bot navmesh vertices taken to the navmesh center in double (see NavmeshTerrain). AddMeshTriangles only gets the offset
// already rounded to float, so its calls in AddVoxelMesh, its only caller, get the voxel map and the center added.
[HarmonyPatch(typeof(MyNavigationInputMesh), nameof(MyNavigationInputMesh.AddVoxelMesh))]
internal static class MyNavigationInputMeshPatch
{
    // Applied at startup only
    private static bool Prepare() => Config.Current.FixNavmeshPrecision;

    // "AddMeshTriangles(mesh, offset, rotation, ownRotation, worldVertices)" becomes
    // "NavmeshTerrain.AddMeshTriangles(mesh, offset, rotation, ownRotation, worldVertices, voxelBase, originPosition)"
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> AddVoxelMeshTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var il = instructions.ToList();
        var voxel = SingleParameter(original, typeof(MyVoxelBase));
        var center = SingleParameter(original, typeof(Vector3D));
        var add = AccessTools.Method(typeof(MyNavigationInputMesh), nameof(MyNavigationInputMesh.AddMeshTriangles));
        var calls = il.FindAllIndex(ci => ci.Calls(add));
        if (calls.Count != 2 || voxel < 0 || center < 0)
            throw new CodeInstructionNotFound($"Expected two calls to {nameof(MyNavigationInputMesh.AddMeshTriangles)}, and one {nameof(MyVoxelBase)} and one {nameof(Vector3D)} parameter in {nameof(MyNavigationInputMesh)}.{nameof(MyNavigationInputMesh.AddVoxelMesh)}");

        // The voxel map and the center go on top of the arguments of each call, from the last one so the indices stay valid
        foreach (var i in Enumerable.Reverse(calls))
        {
            il[i].operand = AccessTools.Method(typeof(NavmeshTerrain), nameof(NavmeshTerrain.AddMeshTriangles));
            il.InsertRange(i, [
                CodeInstruction.LoadArgument(voxel).MoveLabelsFrom(il[i]),
                CodeInstruction.LoadArgument(center)
            ]);
        }

        return il;
    }

    // Argument index of the one parameter of the type, -1 when there is none or more than one
    private static int SingleParameter(MethodBase method, Type type)
    {
        var matches = method.GetParameters().Where(p => p.ParameterType == type).ToList();
        return matches.Count == 1 ? matches[0].Position + (method.IsStatic ? 0 : 1) : -1;
    }
}
