using HarmonyLib;
using VRage.Render11.GeometryStage2.Lodding;
using VRage.Render11.GeometryStage2.Rendering;
using VRageRender;

namespace ClientPlugin.Patches;

// The UpdateNewLoddingSettings handler passes the global lodding settings to the new pipeline (MyLodStrategy) only
// when the hysteresis ratio changes, which every preset leaves at its default. So model quality never changes the LOD
// distances or fade times of new pipeline models, while the old pipeline follows it.
// SetLoddingSetting runs in that handler on the render thread. Not hooked on OnLoddingSettingChanged, that small method
// may already be inlined into the handler, which is compiled before plugins load.
[HarmonyPatch(typeof(MyGeometryRenderer), nameof(MyGeometryRenderer.SetLoddingSetting))]
internal static class MyGeometryRendererPatch
{
    private static bool Prepare() => Config.Current.FixNewPipelineLodding;

    [HarmonyPostfix]
    private static void SetLoddingSettingPostfix(ref MyNewLoddingSettings settings) =>
        MyLodStrategy.SetSettings(ref settings.Global);
}
