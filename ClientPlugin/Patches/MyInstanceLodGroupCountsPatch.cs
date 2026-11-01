using HarmonyLib;
using VRage.Render11.GeometryStage2.PreparePass;

namespace ClientPlugin.Patches;

// Every prepare pass groups the visible LODs with two tables indexed by the LOD instance id, and the game clears both
// tables in full on every pass of every frame. The ids are never reused, so the tables, and the clearing with them,
// grow with every model load, including all models of every world loaded. Here a table is only cleared when the last
// pass using it did not empty its used slots itself (MyPreparePassPatch marks that with m_usedGroupsCount = 0).
[HarmonyPatch(typeof(MyInstanceLodGroupCounts))]
internal static class MyInstanceLodGroupCountsPatch
{
    private static bool Prepare() => Config.Current.FixLodGroupTables;

    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyInstanceLodGroupCounts.PrepareByZeroes))]
    private static bool PrepareByZeroesPrefix(ref MyInstanceLodGroupCounts __instance, int maxLodId)
    {
        PrepareTable(ref __instance, maxLodId, 0);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyInstanceLodGroupCounts.PrepareByNegativeOnes))]
    private static bool PrepareByNegativeOnesPrefix(ref MyInstanceLodGroupCounts __instance, int maxLodId)
    {
        PrepareTable(ref __instance, maxLodId, -1);
        return false;
    }

    // The highest id only grows, so the slots used last time are always within the current size
    private static void PrepareTable(ref MyInstanceLodGroupCounts table, int maxLodId, int empty)
    {
        var size = (maxLodId + 1) * MyInstanceLodGroupCounts.m_instanceLodStatesCount;
        var slots = table.m_instancesPerGroup;
        if (slots == null || slots.Length < size)
        {
            // Headroom, so models loading in over several frames do not reallocate it on every frame
            slots = table.m_instancesPerGroup = new int[size + size / 2];
            Fill(slots, empty, slots.Length);
        }
        else if (table.m_usedGroupsCount != 0)
        {
            Fill(slots, empty, size);
        }

        table.m_maxPossibleLodId = maxLodId;
        table.m_usedGroupsCount = size;
    }

    private static void Fill(int[] slots, int value, int count)
    {
        for (var i = 0; i < count; i++)
            slots[i] = value;
    }
}
