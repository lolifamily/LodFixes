using System;
using System.Threading;
using VRage.Render11.GeometryStage.Voxel;
using VRage.Render11.Scene;
using VRageMath;
using VRageRender;

namespace ClientPlugin.Fixes;

// Runs on the render thread, except RequestRefresh
internal static class VoxelCellFixes
{
    // Offsets of planets up to ~230 km stay below this period, so they are left unchanged.
    // Larger maps get a seam every 262 km, only for tile sizes that do not divide the period.
    private const double TextureOffsetPeriod = 1 << 18;

    private static int _refreshRequested;

    public static void RequestRefresh() => Interlocked.Exchange(ref _refreshRequested, 1);

    // Re-applies the options to every existing voxel cell without remeshing
    public static void RefreshIfRequested()
    {
        if (Interlocked.Exchange(ref _refreshRequested, 0) == 0)
            return;

        foreach (var actor in MyComponentFactory<MyRenderVoxelActor>.GetAll())
            actor.UpdateActors();
    }

    // The game just created this proxy
    public static void OnProxyCreated(MyVoxelCellComponent cell, int lodIndex, int proxyIndex) =>
        Apply(cell, cell.m_lods[lodIndex].RenderableProxies[proxyIndex], Config.Current);

    // The game just went through every proxy of every LOD, it only skips a cell without LODs
    public static void OnParametersUpdated(MyVoxelCellComponent cell)
    {
        if (cell.m_lods is not { } lods)
            return;

        var config = Config.Current;
        foreach (var lod in lods)
            foreach (var proxy in lod.RenderableProxies)
                Apply(cell, proxy, config);
    }

    private static void Apply(MyVoxelCellComponent cell, MyRenderableProxy proxy, Config config)
    {
        ref var data = ref proxy.VoxelCommonObjectData;

        // The shader adds this offset to the texture coordinates in float, too coarse on huge planets
        data.VoxelOffset = config.FixTexturePrecision ? Wrap(cell.m_offset) : cell.m_offset;

        // The shadow (Depth) proxy keeps its value, the ground thickness emulation in the shader depends on it
        if (config.DisableDistanceSpherize && cell.m_voxel.Spherize && (proxy.DrawSubmesh.Flags & MyDrawSubmesh.MySubmeshFlags.Depth) == 0)
            data.SpherizeTo = data.SpherizeStart = float.NaN;
    }

    private static Vector3 Wrap(Vector3D offset) => new((float)Mod(offset.X), (float)Mod(offset.Y), (float)Mod(offset.Z));

    private static double Mod(double value) => value - Math.Floor(value / TextureOffsetPeriod) * TextureOffsetPeriod;
}
