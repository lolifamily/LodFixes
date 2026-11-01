using System;
using System.IO;
using ClientPlugin.Tools;
using Sandbox.Engine.Networking;
using Sandbox.Engine.Voxels.Planet;
using VRage.FileSystem;
using VRage.Game;

namespace ClientPlugin.Fixes;

// Planet height and material maps decoded straight into the buffers the game keeps (see PngReader). The game loads them
// with MyImage.Load, which decodes the whole image into a managed array and copies it into a second one, then copies
// that into the native face: 6 GiB at the peak for each 2 GiB face of a 32764 pixel heightmap. Both loaders return null
// for maps in other layouts and on failures, the game's own code then loads them and gives its usual result.
internal static class PlanetMaps
{
    // MyHeightmapFace computes its buffer size in int, (32765 + 2)^2 * 2 bytes is the most it can hold
    private const int MaxHeightmapSize = 32765;

    // Same for the one byte faces of MyCubemapData
    private const int MaxMaterialMapSize = 46338;

    // One heightmap face, what MyPlanetTextureMapProvider.GetHeightMap(folder, face, context) loads. Grayscale only, the
    // game reads heightmaps as one channel. A 16 bit one with tRNS goes through the RGBA path of the game's decoder, that
    // one is left to it too.
    public static unsafe MyHeightmapFace ReadHeightmapFace(string path)
    {
        PngReader png = null;
        MyHeightmapFace face = null;
        try
        {
            png = Open(path);
            if (png == null || png.Interlaced || png.HasTransparency || png.ColorType != PngReader.Grayscale ||
                png.BitDepth is not (8 or 16) || png.Width != png.Height || png.Width > MaxHeightmapSize)
                return null;

            face = new MyHeightmapFace(png.Width);
            for (var y = 0; y < png.Height; y++)
            {
                fixed (byte* row = png.ReadRow())
                {
                    var target = face.Data + face.GetRowStart(y);
                    if (png.BitDepth == 16)
                        SwapBytes(row, target, png.Width);
                    else
                        for (var x = 0; x < png.Width; x++)
                            target[x] = (ushort)(row[x] << 8);
                }
            }

            var result = face;
            face = null;
            return result;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error($"Could not decode the heightmap {path}, the game loads it instead: {e.Message}");
            return null;
        }
        finally
        {
            png?.Dispose();
            face?.Dispose();
        }
    }

    // All material maps of a planet, what MyPlanetTextureMapProvider.GetPlanetMaps loads: material, biome and ore in the
    // red, green and blue channel of the _mat files. Only when all six are RGB or RGBA PNGs of one size, the game gives
    // other results for anything else (no maps, part of them, an error).
    public static MyCubemap[] ReadMaterialMaps(string folder, MyModContext context, MyPlanetMapTypeSet types)
    {
        if (types == 0)
            return null;

        var readers = new PngReader[6];

        // Face * 3 + channel, null for the channels the planet does not use
        var channels = new MyCubemapData<byte>[6 * 3];
        try
        {
            for (var i = 0; i < 6; i++)
            {
                readers[i] = OpenMaterialMap(Path.Combine(folder, MyCubemapHelpers.GetNameForFace(i)), context);
                if (!IsMaterialMap(readers[i], readers[0]))
                    return null;
            }

            for (var i = 0; i < 6; i++)
                ReadMaterialFace(readers[i], types, channels, i * 3);

            // Like the game: a cubemap per channel loaded, the fourth slot stays empty
            var maps = new MyCubemap[4];
            for (var c = 0; c < 3; c++)
            {
                if (channels[c] != null)
                    maps[c] = new MyCubemap([channels[c], channels[3 + c], channels[6 + c], channels[9 + c], channels[12 + c], channels[15 + c]]);
            }

            channels = null;
            return maps;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error($"Could not decode the material maps of {folder}, the game loads them instead: {e.Message}");
            return null;
        }
        finally
        {
            foreach (var png in readers)
                png?.Dispose();

            if (channels != null)
            {
                foreach (var channel in channels)
                    channel?.Dispose();
            }
        }
    }

    // All faces the same size, each square
    private static bool IsMaterialMap(PngReader png, PngReader first) =>
        png is { Interlaced: false, ColorType: PngReader.Rgb or PngReader.Rgba, BitDepth: 8 or 16 } &&
        png.Width == png.Height && png.Width == first.Width && png.Width <= MaxMaterialMapSize;

    // One face of the material maps into the channels the planet uses (flags 1, 2, 4 for red, green, blue)
    private static unsafe void ReadMaterialFace(PngReader png, MyPlanetMapTypeSet types, MyCubemapData<byte>[] channels, int first)
    {
        for (var c = 0; c < 3; c++)
        {
            if (((int)types & 1 << c) != 0)
                channels[first + c] = new MyCubemapData<byte>(png.Width);
        }

        var samples = png.ColorType == PngReader.Rgba ? 4 : 3;
        for (var y = 0; y < png.Height; y++)
        {
            fixed (byte* row = png.ReadRow())
            {
                for (var c = 0; c < 3; c++)
                {
                    var channel = channels[first + c];
                    if (channel != null)
                        CopyChannel(row, channel.Data + channel.GetRowStart(y), png.Width, samples, c, png.BitDepth);
                }
            }
        }
    }

    // One channel of a row, 16 bit samples rounded to 8 bits the way the game's decoder rounds them
    private static unsafe void CopyChannel(byte* row, byte* target, int width, int samples, int channel, int bitDepth)
    {
        if (bitDepth == 8)
        {
            for (var x = 0; x < width; x++)
                target[x] = row[x * samples + channel];
            return;
        }

        for (var x = 0; x < width; x++)
        {
            var sample = row + (x * samples + channel) * 2;
            target[x] = (byte)(((sample[0] << 8 | sample[1]) * 255 + 32895) >> 16);
        }
    }

    // The file MyPlanetTextureMapProvider.TryGetPlanetTexture loads, null where that is none, not a PNG, or in a legacy
    // workshop archive (which the game reads in a way of its own)
    private static PngReader OpenMaterialMap(string name, MyModContext context)
    {
        var path = MaterialMapPath(name + "_mat", context);
        return path == null || path.Contains(MyWorkshop.WorkshopModSuffix) ? null : Open(path);
    }

    // The mod's own map, otherwise the game's, PNG before DDS
    private static string MaterialMapPath(string name, MyModContext context)
    {
        if (!context.IsBaseGame)
        {
            var mod = Path.Combine(context.ModPathData, "PlanetDataFiles", name);
            if (MyFileSystem.FileExists(mod + ".png"))
                return mod + ".png";

            if (MyFileSystem.FileExists(mod + ".dds"))
                return mod + ".dds";
        }

        var game = Path.Combine(MyFileSystem.ContentPath, MyPlanetTextureMapProvider.PlanetDataFilesPath, name);
        if (MyFileSystem.FileExists(game + ".png"))
            return game + ".png";

        return MyFileSystem.FileExists(game + ".dds") ? game + ".dds" : null;
    }

    // The file opened the way the game opens it, null for a missing file and anything but a PNG
    private static PngReader Open(string path)
    {
        if (path == null || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || !MyFileSystem.FileExists(path))
            return null;

        var stream = MyFileSystem.OpenRead(path);
        return stream == null ? null : new PngReader(new BufferedStream(stream, 1 << 20));
    }

    // Big endian 16 bit samples to the little endian ushorts of a face, 4 at a time
    private static unsafe void SwapBytes(byte* source, ushort* target, int count)
    {
        var x = 0;
        for (; x + 4 <= count; x += 4)
        {
            var v = *(ulong*)(source + 2 * x);
            *(ulong*)(target + x) = (v & 0x00FF00FF00FF00FF) << 8 | (v >> 8) & 0x00FF00FF00FF00FF;
        }

        for (; x < count; x++)
            target[x] = (ushort)(source[2 * x] << 8 | source[2 * x + 1]);
    }
}
