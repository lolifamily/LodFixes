using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ClientPlugin.Fixes;
using ClientPlugin.Tools;
using HarmonyLib;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using VRage.Game.Models;
using VRageMath;

namespace ClientPlugin.Patches;

// Camera blocks raycast planets without physics through MyVoxelGeometry.Intersect with a float line, then compute the hit
// from that line in float storage space. Both are replaced, so hit or miss and the hit come from one raycast on the camera's
// double ray (see VoxelRaycast). The interface overloads (pitch and yaw, target position) call this one.
[HarmonyPatch(typeof(MyCameraBlock), nameof(MyCameraBlock.Raycast), typeof(double), typeof(Vector3D))]
internal static class MyCameraBlockPatch
{
    // Applied at startup only
    private static bool Prepare() => Config.Current.FixTerrainRaycast;

    // "geometry.Intersect(ref localLine, out result, flags)" becomes "VoxelRaycast.IntersectCamera(geometry, ref localLine,
    // out result, flags, planet, ref ray)", and the hit stored after it "VoxelRaycast.CameraHit(ref ray, ref result)"
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> RaycastTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var il = instructions.ToList();
        var locals = original.GetMethodBody()!.LocalVariables;
        var ray = SingleLocal(locals, typeof(LineD));
        var planet = SingleLocal(locals, typeof(MyPlanet));
        var result = SingleLocal(locals, typeof(MyIntersectionResultLineTriangle));

        var intersect = AccessTools.Method(typeof(MyVoxelGeometry), nameof(MyVoxelGeometry.Intersect));
        var calls = il.FindAllIndex(ci => ci.Calls(intersect));
        var call = calls.Count == 1 ? calls[0] : -1;
        var store = call < 0 ? -1 : il.FindIndex(call, ci => ci.IsStloc() && locals[ci.LocalIndex()].LocalType == typeof(Vector3D));
        if (store < 0 || ray < 0 || planet < 0 || result < 0)
            throw new CodeInstructionNotFound($"Expected one {nameof(MyVoxelGeometry)}.{nameof(MyVoxelGeometry.Intersect)} call followed by a {nameof(Vector3D)} store, and one {nameof(LineD)}, {nameof(MyPlanet)} and {nameof(MyIntersectionResultLineTriangle)} local in {nameof(MyCameraBlock)}.{nameof(MyCameraBlock.Raycast)}");

        // The store comes after the call, so it is edited first. The float hit the game computed is dropped.
        il.InsertRange(store, [
            new CodeInstruction(OpCodes.Pop),
            CodeInstruction.LoadLocal(ray, useAddress: true),
            CodeInstruction.LoadLocal(result, useAddress: true),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(VoxelRaycast), nameof(VoxelRaycast.CameraHit)))
        ]);

        // The arguments of the call are on the stack, the planet and the double ray go on top of them
        il[call].opcode = OpCodes.Call;
        il[call].operand = AccessTools.Method(typeof(VoxelRaycast), nameof(VoxelRaycast.IntersectCamera));
        il.InsertRange(call, [
            CodeInstruction.LoadLocal(planet).MoveLabelsFrom(il[call]),
            CodeInstruction.LoadLocal(ray, useAddress: true)
        ]);

        return il;
    }

    // Index of the one local of the type, -1 when there is none or more than one
    private static int SingleLocal(IList<LocalVariableInfo> locals, Type type)
    {
        var matches = locals.Where(local => local.LocalType == type).ToList();
        return matches.Count == 1 ? matches[0].LocalIndex : -1;
    }
}
