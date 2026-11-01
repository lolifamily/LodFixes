using System.Collections.Concurrent;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Sandbox.Game.WorldEnvironment.Definitions;
using VRage.Game.Components;

namespace ClientPlugin.Fixes;

// Footprints of the models planet environments place, kept for the session like the game keeps planet maps
// (MyHeightMapLoadingSystem). The next session measures the files again, so mods updated in between are read anew.
[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
[UsedImplicitly]
internal class ModelFootprints : MySessionComponentBase
{
    public static ModelFootprints Static { get; private set; }

    private readonly ConcurrentDictionary<string, ModelFootprint> footprints = new();

    public override void LoadData()
    {
        base.LoadData();
        Static = this;
    }

    protected override void UnloadData()
    {
        base.UnloadData();
        Static = null;
    }

    // Measured on first use. Threads asking for the same model at once may each measure it and the first result stored
    // is kept, as MyHeightMapLoadingSystem.Cache does: they are all the same, a footprint is a function of the file.
    public ModelFootprint Get(string model) => footprints.GetOrAdd(model, ModelFootprint.Measure);

    // Every model an environment can show, measured on all cores while its planet loads, so that the first sectors do
    // not measure them, partly on the game thread
    public void Preload(MyWorldEnvironmentDefinition environment) =>
        Parallel.ForEach(ModelFootprint.ModelsOf(environment), model => Get(model));
}
