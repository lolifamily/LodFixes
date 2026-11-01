using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using ClientPlugin.Fixes;
using ClientPlugin.Tools;
using HarmonyLib;
using Sandbox.Game.WorldEnvironment;
using VRage.Library.Collections;

namespace ClientPlugin.Patches;

// ScanItems creates every procedural environment item (trees, bushes, forageables, boulders, bot spawns) and adds it right
// after picking its definition and rotation from its float position. The add is redirected, so the item keeps that
// identity and only its height changes (see VegetationPlacement). Nothing reads the position before the add.
// Always applied, the option is checked for every item, so it applies from the next placement on.
[HarmonyPatch(typeof(MyProceduralLogicalSector), nameof(MyProceduralLogicalSector.ScanItems))]
internal static class MyProceduralLogicalSectorPatch
{
    // "m_items.Add(item)" becomes "VegetationPlacement.Add(m_items, item, this)"
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> ScanItemsTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var il = instructions.ToList();

        var add = AccessTools.Method(typeof(MyList<ItemInfo>), nameof(MyList<>.Add), [typeof(ItemInfo)]);
        var calls = il.FindAllIndex(ci => ci.Calls(add));
        if (calls.Count != 1)
            throw new CodeInstructionNotFound($"Expected one call to {nameof(MyList<>)}.{nameof(MyList<>.Add)} in {nameof(MyProceduralLogicalSector)}.{nameof(MyProceduralLogicalSector.ScanItems)}, found {calls.Count}");

        // The list and the item are on the stack, the sector goes on top of them
        var i = calls[0];
        il.Insert(i, new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(il[i]));
        il[i + 1].opcode = OpCodes.Call;
        il[i + 1].operand = AccessTools.Method(typeof(VegetationPlacement), nameof(VegetationPlacement.Add));

        return il;
    }
}
