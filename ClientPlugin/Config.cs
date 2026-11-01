using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ClientPlugin.Settings;
using ClientPlugin.Settings.Elements;
using JetBrains.Annotations;

namespace ClientPlugin;

public enum TerrainSdfMode
{
    Native,
    ManagedFloatNotRecommended,
    ManagedDouble
}

public class Config : INotifyPropertyChanged
{
    #region User interface

    public readonly string Title = "LOD Fixes";

    [Separator("Vanilla bug fixes")]
    [Checkbox("Fix distant dig holes (restart)", "Stops edited terrain showing as holes in the distance.")]
    public bool FixOctreeLodRead { get; [UsedImplicitly] set => SetField(ref field, value); } = true;

    [Checkbox("Fix model quality LOD (restart)", "Makes model quality change the LOD distances of all models.")]
    public bool FixNewPipelineLodding { get; [UsedImplicitly] set => SetField(ref field, value); } = true;

    [Checkbox("Fix tree sector leak (restart)", "Stops planet tree sectors leaking around ships.")]
    public bool FixEnvironmentSectorLookup { get; [UsedImplicitly] set => SetField(ref field, value); } = true;

    [Checkbox("Fix fast LOD fades (restart)", "Stops grid edges and projections fading between LODs too fast.")]
    public bool FixRepeatedCullUpdate { get; [UsedImplicitly] set => SetField(ref field, value); } = true;

    [Checkbox("Fix model render slowdown (restart)", "Stops model rendering getting slower with every world load.")]
    public bool FixLodGroupTables { get; [UsedImplicitly] set => SetField(ref field, value); } = true;

    [Checkbox("Fix heightmap culling (restart)", "Speeds up terrain and collision on planets with heightmaps other than 2048 or 8192 pixels, like 4096.")]
    public bool FixHeightmapPruning { get; [UsedImplicitly] set => SetField(ref field, value); } = true;

    [Checkbox("Fast planet map loading (restart)", "Decodes planet height and material maps straight into the game's buffers, faster and without temporary copies.")]
    public bool FastPlanetMapLoading { get; [UsedImplicitly] set => SetField(ref field, value); } = true;

    [Checkbox("Fix terrain raycasts (restart)", "Stops raycasts of camera blocks, building and bullets passing through terrain or hitting it up to meters off, mostly on huge planets.")]
    public bool FixTerrainRaycast { get; [UsedImplicitly] set => SetField(ref field, value); } = true;

    [Checkbox("Fix slow bot navigation (restart)", "Stops bots waiting long for their paths in mountains and on huge planets.")]
    public bool FixNavmeshTerrainRange { get; [UsedImplicitly] set => SetField(ref field, value); } = true;

    [Separator("Only needed on huge planets (524+ km)")]
    [Checkbox("Raise planet size limit", "Allows planets up to 16777 km instead of 8000 km.")]
    public bool RaisePlanetSizeLimit { get; [UsedImplicitly] set => SetField(ref field, value); }

    [Checkbox("Fix raycast cache (restart)", "Stops raycasts hitting the wrong terrain on planets over 8389 km. Needs a restart.")]
    public bool FixRaycastCache { get; [UsedImplicitly] set => SetField(ref field, value); } = true;

    [Checkbox("Fix altitude jitter (restart)", "Stops the altitude of the HUD, scripts and AI jumping near the ground.")]
    public bool FixAltitudeJitter { get; [UsedImplicitly] set => SetField(ref field, value); } = true;

    [Checkbox("Fix ground material speckles (restart)", "Stops grass and soil speckling in the top layer of the ground.")]
    public bool FixMaterialPrecision { get; [UsedImplicitly] set => SetField(ref field, value); } = true;

    [Checkbox("Fix bot navigation (restart)", "Stops wolves, spiders and other bots failing to find paths over the terrain.")]
    public bool FixNavmeshPrecision { get; [UsedImplicitly] set => SetField(ref field, value); } = true;

    [Checkbox("Fix planet edits", "Keeps edited terrain of planets over 524 km intact after reloading or edits in deep space.")]
    public bool FixOctreeLoad { get; [UsedImplicitly] set => SetField(ref field, value); } = true;

    [Checkbox("Fix texture pixelation", "Removes pixelated textures near the ground.")]
    public bool FixTexturePrecision { get; set => SetField(ref field, value); }

    [Checkbox("Disable distance spherize", "Stops terrain beyond 30 km from sinking.")]
    public bool DisableDistanceSpherize { get; set => SetField(ref field, value); }

    [Dropdown(label: "Terrain SDF", description: "How planet terrain shape is computed. Managed is faster than Native, Managed Double also removes surface noise. Rebuilds terrain.")]
    public TerrainSdfMode TerrainSdf { get; set => SetField(ref field, value); } = TerrainSdfMode.ManagedDouble;

    [Checkbox("Fix slope detail clipping", "Stops steep slopes being cut flat in strips. Managed Double only.")]
    public bool FixDetailCull { get; set => SetField(ref field, value); }

    [Checkbox("Smooth slope detail", "Smooths the stair steps of steep slopes, which changes their shape. Managed Double only.")]
    public bool SmoothSlopeDetail { get; set => SetField(ref field, value); }

    [Separator("Vegetation")]
    [Checkbox("Fix vegetation placement", "Stops trees, bushes and forageables floating above or sinking into the ground.")]
    public bool FixVegetationPlacement { get; set => SetField(ref field, value); }

    [Slider(0f, 1f, 0.05f, SliderAttribute.SliderType.Float, "Height variation", "How much of their roots trees may randomly show or bury.")]
    public float VegetationVariation { get; set => SetField(ref field, value); } = 0.5f;

    [Separator("Terrain LOD")]
    [Slider(0f, 14f, 1f, SliderAttribute.SliderType.Integer, "LOD level", "First terrain LOD to scale.")]
    public int TerrainLod { get; set => SetField(ref field, value); } = 8;

    [Slider(1f, 8f, 0.5f, SliderAttribute.SliderType.Float, "Range multiplier", "Scales the view range of that LOD and all coarser ones.")]
    public float TerrainLodRangeScale { get; set => SetField(ref field, value); } = 1f;

    #endregion

    #region Property change notification boilerplate

    public static readonly Config Default = new ();
    public static readonly Config Current = ConfigStorage.Load();

    public event PropertyChangedEventHandler PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    #endregion
}
