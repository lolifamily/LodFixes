using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using ClientPlugin.Fixes;
using ClientPlugin.Tools;
using HarmonyLib;
using Sandbox.Engine.Voxels.Planet;
using Sandbox.Game.Gui;
using VRageMath;

namespace ClientPlugin.Patches;

// The HUD altitude, also shown by the artificial horizon of LCDs, converts the position relative to the planet center to
// float right before GetDistanceToSurfaceCacheless (see HeightmapSurface)
[HarmonyPatch(typeof(MyGuiScreenHudSpace), nameof(MyGuiScreenHudSpace.GetHeightAboveSurface))]
internal static class MyGuiScreenHudSpacePatch
{
    // Applied at startup only
    private static bool Prepare() => Config.Current.FixAltitudeJitter;

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> GetHeightAboveSurfaceTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var il = instructions.ToList();
        var toFloat = typeof(Vector3D).GetMethods().Single(m => m.Name == "op_Implicit" && m.ReturnType == typeof(Vector3) && m.GetParameters()[0].ParameterType == typeof(Vector3D));
        var distance = AccessTools.Method(typeof(MyPlanetShapeProvider), nameof(MyPlanetShapeProvider.GetDistanceToSurfaceCacheless));
        var calls = il.FindAllIndex(ci => ci.Calls(distance));
        if (calls.Count != 2 || calls.Any(i => !il[i - 1].Calls(toFloat)))
            throw new CodeInstructionNotFound($"Expected two calls to {nameof(MyPlanetShapeProvider.GetDistanceToSurfaceCacheless)} on a {nameof(Vector3D)} converted to float");

        // The shape and the double position are on the stack, the static method takes them as its arguments
        foreach (var i in Enumerable.Reverse(calls))
        {
            il[i].opcode = OpCodes.Call;
            il[i].operand = AccessTools.Method(typeof(HeightmapSurface), nameof(HeightmapSurface.Distance));
            il[i].MoveLabelsFrom(il[i - 1]);
            il.RemoveAt(i - 1);
        }

        return il;
    }
}
