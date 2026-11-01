using System;
using System.Collections.Generic;
using ClientPlugin.Tools;
using HarmonyLib;
using VRage.Render11.GeometryStage2.PrepareGroupPass;
using VRage.Render11.GeometryStage2.PreparePass;
using VRage.Render11.GeometryStage2.Rendering;

namespace ClientPlugin.Patches;

// Empties the table slots a prepare pass used, so the next pass does not have to clear the whole tables (see
// MyInstanceLodGroupCountsPatch). The used slots are exactly those of the instance groups built, one group per slot.
// If this does not run for a pass, the next use of its tables clears them like the game does.
//
// MyPreparePass is generic over structs, so each instantiation has its own code to patch, and render plugins add their
// own (Prism.Render uses one for the GBuffer). So instantiations are patched when the renderer first uses them.
[HarmonyPatch(typeof(MyGeometryRenderer), nameof(MyGeometryRenderer.InitPasses))]
internal static class MyPreparePassPatch
{
    private static readonly Harmony Harmony = new(Plugin.Name);
    private static readonly HashSet<Type> SeenPasses = [];

    private static bool Prepare() => Config.Current.FixLodGroupTables;

    // Render thread, before the passes of the frame run. Last, so the passes other plugins swap in are seen too.
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void InitPassesPostfix(List<IPrepareWork> outPreparePasses)
    {
        foreach (var pass in outPreparePasses)
        {
            var type = pass.GetType();
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(MyPreparePass<,>) && SeenPasses.Add(type))
                PatchPass(type);
        }
    }

    private static void PatchPass(Type type)
    {
        try
        {
            var original = AccessTools.Method(type, nameof(MyPreparePass<,>.PrepareInstanceableGroups));
            Harmony.Patch(original, postfix: new HarmonyMethod(typeof(MyPreparePassPatch), nameof(PrepareInstanceableGroupsPostfix)));
        }
        catch (Exception e)
        {
            // That pass keeps clearing its tables in full
            Log.Error($"Failed to patch {type}: {e}");
        }
    }

    private static void PrepareInstanceableGroupsPostfix(MyRenderData ___m_outputRenderData, ref MyInstanceLodGroupCounts ___m_tmpInstanceLodsCounts, ref MyInstanceLodGroupCounts ___m_tmpInstanceLodsOffsets)
    {
        var counts = ___m_tmpInstanceLodsCounts.m_instancesPerGroup;
        var offsets = ___m_tmpInstanceLodsOffsets.m_instancesPerGroup;
        foreach (var group in ___m_outputRenderData.InstanceLodGroups)
        {
            var slot = ___m_tmpInstanceLodsOffsets.GetDirectOffset(group.LodInstance.UniqueId, group.State);
            counts[slot] = 0;
            offsets[slot] = -1;
        }

        ___m_tmpInstanceLodsCounts.m_usedGroupsCount = 0;
        ___m_tmpInstanceLodsOffsets.m_usedGroupsCount = 0;
    }
}
