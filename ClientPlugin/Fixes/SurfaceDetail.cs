using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Sandbox.Engine.Voxels.Planet;

namespace ClientPlugin.Fixes;

// Detail map of steep slopes filtered for the size of the voxels reading it (Config.SmoothSlopeDetail), one per planet.
// The game adds the nearest texel of the detail texture (0.375 m texels on EarthLike) at every LOD: a vertical step at
// every texel edge, which 1 m voxels sample at random and every LOD differently, and a step along every cube edge,
// where each face tiles the texture its own way.
// Here each LOD reads the mip level whose texels are about as wide as its voxels through a cubic B-spline (continuous
// height, slope and curvature, no overshoot, the same mean), and within Band of a cube edge the faces' textures blend.
internal sealed class SurfaceDetail
{
    // Metres past a face edge over which the texture of that face fades out
    private const double Band = 16;

    // Wrapped texels stored around every level, the B-spline reads up to 2 past the texel a point is in
    private const int Pad = 2;

    private const double Log2E = 1.4426950408889634;

    private static readonly ConditionalWeakTable<MyPlanetShapeProvider, Lazy<SurfaceDetail>> Planets = new();

    // [0] is the texture, each next level the 2x2 averages of the one before, all with Pad wrapped texels around
    private readonly float[][] levels;
    private readonly int[] sizes;

    // From the planet's SurfaceDetailSampler: repeats of the texture per face, metres at texel value 1, slope fade
    private readonly double factor;
    private readonly float scale, slopeMid, slopeIn, slopeInRecip, slopeOut, slopeOutRecip;

    // R, log2 of the ground size of a texel at a face center, and R / 2 / Band (see Height)
    private readonly double radius, log2CenterTexel, edgeScale;

    // Null when the planet has no detail map, the game's lookup adds nothing there either.
    // Built on first use by one of the mesher threads, the others wait for it.
    public static SurfaceDetail Of(MyPlanetShapeProvider shape) =>
        shape.m_detail.m_detail == null
            ? null
            : Planets.GetValue(shape, s => new Lazy<SurfaceDetail>(() => new SurfaceDetail(s))).Value;

    // Detail height in metres at p, relative to the planet center (distance = |p|), for voxels lod metres wide, where the
    // heightmap is mapHeight above the radius. The caller has checked that the slope is in the detail range
    // (SurfaceDetailSampler.Matches). 0 where the detail cannot change the content of the voxel.
    public float Height(double x, double y, double z, double distance, float mapHeight, float lod, float normalZ)
    {
        // The detail moves the signed distance by up to scale * normalZ. Voxels further than lod from the ground with and
        // without it are full or empty either way, most of every block on a slope. The margin covers the float rounding
        // of the signed distance, below 0.07 m with up to 1000 km of hills.
        var reach = 1.125 * lod + Math.Abs(scale) * normalZ;
        if (Math.Abs((distance - radius - mapHeight) * normalZ) >= reach)
            return 0;

        double ax = Math.Abs(x), ay = Math.Abs(y), az = Math.Abs(z);
        var m = Math.Max(ax, Math.Max(ay, az));

        // The level whose texels are as wide as the voxels. Texels shrink from the face centers to the cube corners by
        // (m / distance)^1.5, from 0.48 m to 0.21 m on EarthLike.
        var r = m / distance;
        var level = Math.Log(lod / (r * Math.Sqrt(r))) * Log2E - log2CenterTexel;

        double sum = 0, total = 0;
        Blend(ax, x > 0 ? 2 : 3);
        Blend(ay, y > 0 ? 4 : 5);
        Blend(az, z > 0 ? 1 : 0);
        return (float)(sum / total) * SlopeWeight(normalZ) * scale;

        // The face the point is on weighs 1, the others fade out over Band past their edge. (m - c) / m is the distance
        // to that edge in face coordinates (-1..1), next to an edge one of those is R / 2 on the ground.
        void Blend(double c, int face)
        {
            var weight = 1 - (m - c) / m * edgeScale;
            if (weight <= 0)
                return;

            TerrainSdf.ProjectToFace(x, y, z, face, out var u, out var v);
            sum += weight * Sample(u * factor, v * factor, level);
            total += weight;
        }
    }

    // The two levels around level blended, below the first or past the last level that one alone
    private float Sample(double tx, double ty, double level)
    {
        // Only the position within one repeat of the texture
        tx -= Math.Floor(tx);
        ty -= Math.Floor(ty);

        level = Math.Max(0, Math.Min(level, levels.Length - 1));
        var l = (int)level;
        var f = (float)(level - l);
        var value = BSpline(l, tx, ty);
        return f > 0 ? value + (BSpline(l + 1, tx, ty) - value) * f : value;
    }

    // Uniform cubic B-spline over the 4x4 texels around the point. x0 and y0 are -1 to size - 1, so the 4x4 from
    // (x0 - 1, y0 - 1) is always within the Pad texels around the level and nothing wraps here.
    private float BSpline(int level, double tx, double ty)
    {
        var size = sizes[level];
        var texels = levels[level];
        var stride = size + 2 * Pad;

        // Texel centers at whole numbers
        var fx = tx * size - 0.5;
        var fy = ty * size - 0.5;
        var x0 = (int)Math.Floor(fx);
        var y0 = (int)Math.Floor(fy);
        Weights((float)(fx - x0), out var wx0, out var wx1, out var wx2, out var wx3);
        Weights((float)(fy - y0), out var wy0, out var wy1, out var wy2, out var wy3);

        var first = (y0 - 1 + Pad) * stride + x0 - 1 + Pad;
        return wy0 * Row(first) + wy1 * Row(first + stride) + wy2 * Row(first + 2 * stride) + wy3 * Row(first + 3 * stride);

        float Row(int i) => wx0 * texels[i] + wx1 * texels[i + 1] + wx2 * texels[i + 2] + wx3 * texels[i + 3];
    }

    // Height of the B-spline basis at the 4 texels around a point t of the way from the second to the third.
    // They sum to 1 everywhere.
    private static void Weights(float t, out float w0, out float w1, out float w2, out float w3)
    {
        var t2 = t * t;
        var t3 = t2 * t;
        var s = 1 - t;
        w0 = s * s * s / 6;
        w1 = (3 * t3 - 6 * t2 + 4) / 6;
        w2 = (-3 * t3 + 3 * t2 + 3 * t + 1) / 6;
        w3 = t3 / 6;
    }

    // SurfaceDetailSampler.GetValue without the texel (MyPlanetShapeProvider.cs:66). It fades in over the Transition
    // above Slope.Min, and on the steep side subtracts m_in where m_min would fade out towards Slope.Max: on EarthLike
    // it fades in from 30 to 40 degrees, out from 59 to 70, and adds nothing from 70 to 80. Kept as is.
    private float SlopeWeight(float normalZ) => normalZ > slopeMid
        ? Math.Min(Math.Max(1f - (normalZ - slopeOut) * slopeOutRecip, 0f), 1f)
        : Math.Max(Math.Min((normalZ - slopeIn) * slopeInRecip, 1f), 0f);

    private unsafe SurfaceDetail(MyPlanetShapeProvider shape)
    {
        var detail = shape.m_detail;
        factor = detail.Factor;
        scale = detail.Scale;
        slopeMid = detail.m_mid;
        slopeIn = detail.m_in;
        slopeInRecip = detail.m_inRecip;
        slopeOut = detail.m_out;
        slopeOutRecip = detail.m_outRecip;

        // Level 0 is the texture as 0..1, rows of Resolution texels as MyHeightDetailTexture.GetValue reads them (the
        // game takes the image as square, Resolution is its height). A texture that failed to load is a 1x1 zero.
        var texture = detail.m_detail;
        var size = (int)texture.Resolution;
        var level = new float[size * size];
        var data = texture.Data;
        for (var i = 0; i < level.Length; i++)
            level[i] = data[i] * (1f / 255f);

        // Every next level averages 2x2 texels while the size halves evenly: 11 levels at 1024, 4 at 1000 (down to 125).
        // LODs coarser than the last level read the last level.
        var levelList = new List<float[]> { Padded(level, size) };
        var sizeList = new List<int> { size };
        while (size % 2 == 0)
        {
            level = Halved(level, size);
            size /= 2;
            levelList.Add(Padded(level, size));
            sizeList.Add(size);
        }

        levels = levelList.ToArray();
        sizes = sizeList.ToArray();

        // At a face center one unit of face coordinate (0..1) is 2R on the ground and holds factor repeats of the texture
        radius = shape.m_radius;
        log2CenterTexel = Math.Log(2 * radius / (factor * sizes[0])) * Log2E;
        edgeScale = radius / 2 / Band;
    }

    // 2x2 averages of a level of even size
    private static float[] Halved(float[] level, int size)
    {
        var half = size / 2;
        var result = new float[half * half];
        for (var y = 0; y < half; y++)
        {
            for (var x = 0; x < half; x++)
            {
                var i = 2 * y * size + 2 * x;
                result[y * half + x] = (level[i] + level[i + 1] + level[i + size] + level[i + size + 1]) * 0.25f;
            }
        }

        return result;
    }

    // The level with Pad texels around it, taken from the opposite side as the texture repeats
    private static float[] Padded(float[] level, int size)
    {
        var stride = size + 2 * Pad;
        var result = new float[stride * stride];
        for (var y = 0; y < stride; y++)
        {
            var row = Wrap(y - Pad, size) * size;
            for (var x = 0; x < stride; x++)
                result[y * stride + x] = level[row + Wrap(x - Pad, size)];
        }

        return result;
    }

    private static int Wrap(int i, int size) => (i % size + size) % size;
}
