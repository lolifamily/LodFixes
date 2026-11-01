using HarmonyLib;
using VRage.Render11.Scene.Components;
using VRageRender;

namespace ClientPlugin.Patches;

// MyCullProxy.Updated makes the first cull query of a frame update an old pipeline object, but every shadow pass that
// drops the proxy (no shadow casting, or not drawn far in cascades 4+) clears it, so the next pass updates the object
// again. Near the camera that is 4-6 updates a frame, and fades and LOD transitions advance on each one. The game
// records the frame in m_cullUpdatedFrame but never checks it.
[HarmonyPatch(typeof(MyRenderableComponent), "UpdateAfterCull")] // Not nameof: Pulsar does not publicize virtual methods
internal static class MyRenderableComponentPatch
{
    private static bool Prepare() => Config.Current.FixRepeatedCullUpdate;

    // The callers lock the cull proxy, so this does not race with the update that sets m_cullUpdatedFrame
    [HarmonyPrefix]
    private static bool UpdateAfterCullPrefix(MyRenderableComponent __instance) =>
        __instance.m_cullUpdatedFrame != MyCommon.FrameCounter;
}
