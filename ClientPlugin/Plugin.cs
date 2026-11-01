using System.ComponentModel;
using System.Reflection;
using ClientPlugin.Fixes;
using ClientPlugin.Settings;
using ClientPlugin.Settings.Layouts;
using HarmonyLib;
using JetBrains.Annotations;
using Sandbox.Graphics.GUI;
using VRage.Plugins;

// Define assembly version when compiled by Pulsar
#if !LOCAL_BUILD
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
#endif
    
namespace ClientPlugin;

[UsedImplicitly]
public class Plugin : IPlugin
{
    public const string Name = "LodFixes";
    private SettingsGenerator settingsGenerator;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void Init(object gameInstance)
    {
        settingsGenerator = new SettingsGenerator();

        Config.Current.PropertyChanged += OnConfigChanged;

        var harmony = new Harmony(Name);
        harmony.PatchAll(Assembly.GetExecutingAssembly());

        // Init runs before the engine first applies the voxel quality presets
        TerrainLodRanges.Update();
    }

    // Options take effect immediately, the config dialog raises this on the game thread
    private static void OnConfigChanged(object sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Config.FixTexturePrecision):
            case nameof(Config.DisableDistanceSpherize):
                VoxelCellFixes.RequestRefresh();
                break;

            case nameof(Config.TerrainSdf):
            case nameof(Config.SmoothSlopeDetail):
                TerrainSdf.RegenerateTerrain();
                VegetationPlacement.Refresh();
                break;

            // The culls do not move vegetation, only the terrain is rebuilt
            case nameof(Config.FixDetailCull):
                TerrainSdf.RegenerateTerrain();
                break;

            case nameof(Config.FixVegetationPlacement):
            case nameof(Config.VegetationVariation):
                VegetationPlacement.Refresh();
                break;

            case nameof(Config.TerrainLod):
            case nameof(Config.TerrainLodRangeScale):
                TerrainLodRanges.Update();
                break;
        }
    }

    public void Dispose()
    {
        // TODO: Save state and close resources here, called when the game exits (not guaranteed!)
        // IMPORTANT: Do NOT call harmony.UnpatchAll() here! It may break other plugins.
    }

    public void Update()
    {
        // TODO: Put your update code here. It is called on every simulation frame!
    }

    // ReSharper disable once UnusedMember.Global
    public void OpenConfigDialog()
    {
        settingsGenerator.SetLayout<Simple>();
        MyGuiSandbox.AddScreen(settingsGenerator.Dialog);
    }

    //TODO: Uncomment and use this method to load asset files
    /*public void LoadAssets(string folder)
    {

    }*/
}