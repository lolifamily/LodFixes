using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;

namespace ClientPlugin.Fixes;

// Reads a PNG one row at a time, so a planet map can be decoded straight into the buffer the game keeps (see
// MyPlanetTextureMapProviderPatch). Rows come out unfiltered with the samples as stored in the file, 16 bit ones big
// endian. Interlaced images are not read, the caller checks the layout before reading rows.
//
// Whether a map loads at all is up to the game's decoder (ImageSharp 1.0.0-beta0006): when that one throws, the game
// uses a default map instead. A map that loaded here but not on a vanilla server would give different terrain, so this
// reader fails on every file the game's decoder fails on. Failing on more is fine, the game's loader then runs as usual.
// The game's decoder checks the CRC of IHDR, PLTE and IEND only (not of IDAT), parses tEXt, pHYs and gAMA, and reads up
// to IEND after the image data, so this reader does the same. On .NET, dotnet-compat makes its reads fail at the end of
// the file, so a file without IEND fails there; that is rejected here on .NET Framework too. Chunk layouts it handles
// in odd ways (empty or tiny IDAT chunks, IDAT after other chunks, more image data than rows) are rejected as well.
internal sealed unsafe class PngReader : IDisposable
{
    public const int Grayscale = 0;
    public const int Rgb = 2;
    public const int Rgba = 6;

    public readonly int Width;
    public readonly int Height;
    public readonly int BitDepth;
    public readonly int ColorType;
    public readonly bool Interlaced;

    // tRNS chunk before the image data, which the game's decoder applies as alpha
    public readonly bool HasTransparency;

    // Bytes of a row without its filter type, and of a pixel (at least 1, what the filters step back by)
    private readonly int rowBytes;
    private readonly int pixelBytes;

    private readonly Stream file;
    private readonly ImageData imageData;
    private readonly Stream pixels;
    private readonly byte[] chunkHeader = new byte[8];
    private readonly byte[] chunkData = new byte[8192];
    private readonly byte[] filterType = new byte[1];
    private byte[] row;
    private byte[] previous;
    private int rowsRead;

    // Reads the chunks up to the image data, the stream belongs to the reader from here on
    public PngReader(Stream stream)
    {
        file = stream;
        try
        {
            ReadExactly(file, chunkHeader, 0, 8);
            if (ReadUInt(chunkHeader, 0) != 0x89504E47 || ReadUInt(chunkHeader, 4) != 0x0D0A1A0A)
                throw new InvalidDataException("Not a PNG file");

            var hasHeader = false;
            while (true)
            {
                ReadExactly(file, chunkHeader, 0, 8);
                var length = ChunkLength(chunkHeader);
                var type = ReadUInt(chunkHeader, 4);
                if (type == ChunkIdat)
                {
                    // The game's decoder reads the zlib header from the first IDAT chunk alone
                    if (!hasHeader || length < 2)
                        throw new InvalidDataException("Unsupported PNG image data layout");

                    imageData = new ImageData(file, length);
                    pixels = Inflate(imageData);
                    break;
                }

                if (type == ChunkIend)
                    throw new InvalidDataException("PNG file without image data");

                if (type == ChunkIhdr)
                {
                    if (hasHeader || length != 13)
                        throw new InvalidDataException("Invalid PNG header chunk");

                    ReadChunk(type, length);
                    Width = (int)ReadUInt(chunkData, 0);
                    Height = (int)ReadUInt(chunkData, 4);
                    BitDepth = chunkData[8];
                    ColorType = chunkData[9];
                    if (chunkData[10] != 0 || chunkData[11] != 0 || chunkData[12] > 1)
                        throw new InvalidDataException("Unknown PNG compression, filter or interlace method");

                    Interlaced = chunkData[12] == 1;
                    hasHeader = true;
                    continue;
                }

                if (type == ChunkTrns)
                    HasTransparency = true;

                ReadChunk(type, length);
            }

            var samples = SamplesPerPixel(ColorType, BitDepth);
            if (Width <= 0 || Height <= 0 || samples == 0 || Width > 1 << 24)
                throw new InvalidDataException("Unsupported PNG header");

            rowBytes = (int)(((long)Width * samples * BitDepth + 7) / 8);
            pixelBytes = Math.Max(1, samples * BitDepth / 8);
            row = new byte[rowBytes];
            previous = new byte[rowBytes];
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        pixels?.Dispose();
        file.Dispose();
    }

    // The next row, valid until the next call. Reading the last one also checks the rest of the file.
    public byte[] ReadRow()
    {
        if (Interlaced || rowsRead == Height)
            throw new InvalidOperationException("No more PNG rows to read");

        (row, previous) = (previous, row);
        ReadExactly(pixels, filterType, 0, 1);
        ReadExactly(pixels, row, 0, rowBytes);
        fixed (byte* current = row, prior = previous)
            Unfilter(filterType[0], current, prior, rowBytes, pixelBytes);

        if (++rowsRead == Height)
            ReadTrailer();

        return row;
    }

    // Valid combinations of the PNG specification, which the game's decoder enforces too
    private static int SamplesPerPixel(int colorType, int bitDepth)
    {
        return colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16 ? 1 : 0,
            2 => bitDepth is 8 or 16 ? 3 : 0,
            3 => bitDepth is 1 or 2 or 4 or 8 ? 1 : 0,
            4 => bitDepth is 8 or 16 ? 2 : 0,
            6 => bitDepth is 8 or 16 ? 4 : 0,
            _ => 0
        };
    }

    // The zlib header comes first, DeflateStream reads the deflate data after it. The Adler-32 checksum at the end is
    // not checked, by the game's decoder neither.
    private static Stream Inflate(Stream data)
    {
        var header = new byte[2];
        ReadExactly(data, header, 0, 2);
        if ((header[0] & 0x0F) != 8 || header[0] >> 4 > 7 || (header[1] & 0x20) != 0)
            throw new InvalidDataException("Unsupported zlib stream in PNG image data");

        return new DeflateStream(data, CompressionMode.Decompress);
    }

    // After the last row: the image data has to end there, then the chunks up to IEND, read like the game's decoder
    // reads them
    private void ReadTrailer()
    {
        if (pixels.Read(filterType, 0, 1) != 0)
            throw new InvalidDataException("PNG image data longer than the image");

        imageData.SkipToEnd();
        var length = imageData.NextLength;
        var type = imageData.NextType;
        while (true)
        {
            if (type is ChunkIdat or ChunkIhdr)
                throw new InvalidDataException("Unexpected PNG chunk after the image data");

            ReadChunk(type, length);
            if (type == ChunkIend)
                return;

            ReadExactly(file, chunkHeader, 0, 8);
            length = ChunkLength(chunkHeader);
            type = ReadUInt(chunkHeader, 4);
        }
    }

    // Data and CRC of a chunk other than IDAT, failing where the game's decoder fails on it. The data of IHDR stays in
    // chunkData.
    private void ReadChunk(uint type, int length)
    {
        var crc = 0xFFFFFFFF;
        for (var shift = 24; shift >= 0; shift -= 8)
            crc = CrcTable[(crc ^ (type >> shift)) & 0xFF] ^ (crc >> 8);

        var hasZero = false;
        for (var left = length; left > 0;)
        {
            var count = Math.Min(left, chunkData.Length);
            ReadExactly(file, chunkData, 0, count);
            for (var i = 0; i < count; i++)
            {
                crc = CrcTable[(crc ^ chunkData[i]) & 0xFF] ^ (crc >> 8);
                hasZero |= chunkData[i] == 0;
            }

            left -= count;
        }

        ReadExactly(file, chunkHeader, 0, 4);
        if (type is ChunkIhdr or ChunkPlte or ChunkIend && ~crc != ReadUInt(chunkHeader, 0))
            throw new InvalidDataException("PNG chunk CRC mismatch");

        // What the game's decoder parses: tEXt up to its null separator, the 9 bytes of pHYs, the 4 bytes of gAMA
        var malformed = type switch
        {
            ChunkText => !hasZero,
            ChunkPhys => length < 9,
            ChunkGama => length < 4,
            _ => false
        };
        if (malformed)
            throw new InvalidDataException("Malformed PNG metadata chunk");
    }

    // PNG filters, each byte is predicted from the byte one pixel to the left (a), above (b) and above left (c), which
    // are 0 outside of the image
    private static void Unfilter(byte type, byte* row, byte* prior, int length, int bpp)
    {
        switch (type)
        {
            case 0:
                return;

            case 1:
                for (var i = bpp; i < length; i++)
                    row[i] += row[i - bpp];
                return;

            case 2:
                AddUp(row, prior, length);
                return;

            case 3:
                for (var i = 0; i < bpp; i++)
                    row[i] += (byte)(prior[i] >> 1);
                for (var i = bpp; i < length; i++)
                    row[i] += (byte)((row[i - bpp] + prior[i]) >> 1);
                return;

            case 4 when bpp == 2:
                Paeth2(row, prior, length);
                return;

            case 4:
                for (var i = 0; i < bpp; i++)
                    row[i] += prior[i];
                for (var i = bpp; i < length; i++)
                    row[i] += Paeth(row[i - bpp], prior[i], prior[i - bpp]);
                return;

            default:
                throw new InvalidDataException($"Unknown PNG filter type {type}");
        }
    }

    // Bytewise row += prior, 8 bytes at a time with the carries kept out of the top bit of each byte
    private static void AddUp(byte* row, byte* prior, int length)
    {
        const ulong low = 0x7F7F7F7F7F7F7F7F;
        const ulong high = 0x8080808080808080;
        var i = 0;
        for (; i + 8 <= length; i += 8)
        {
            var x = *(ulong*)(row + i);
            var y = *(ulong*)(prior + i);
            *(ulong*)(row + i) = ((x & low) + (y & low)) ^ ((x ^ y) & high);
        }

        for (; i < length; i++)
            row[i] += prior[i];
    }

    // Paeth on 2 byte pixels (16 bit grayscale, the bulk of heightmap data). The left and above left bytes of both
    // samples stay in registers instead of being read back from the row just written, about a fifth faster. For the
    // first pixel a and c are 0, which gives b like the generic loop.
    private static void Paeth2(byte* row, byte* prior, int length)
    {
        int a0 = 0, a1 = 0, c0 = 0, c1 = 0;
        for (var i = 0; i < length; i += 2)
        {
            int b0 = prior[i], b1 = prior[i + 1];
            a0 = (byte)(row[i] + Paeth(a0, b0, c0));
            a1 = (byte)(row[i + 1] + Paeth(a1, b1, c1));
            row[i] = (byte)a0;
            row[i + 1] = (byte)a1;
            c0 = b0;
            c1 = b1;
        }
    }

    // The neighbor closest to a + b - c, ties going to a, then b. Without branches, the comparisons depend on the image.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Paeth(int a, int b, int c)
    {
        var pa = Abs(b - c);
        var pb = Abs(a - c);
        var pc = Abs(a + b - c - c);
        var nearest = pb < pa ? b : a;
        return (byte)(pc < Math.Min(pa, pb) ? c : nearest);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Abs(int x)
    {
        var sign = x >> 31;
        return (x ^ sign) - sign;
    }

    private static uint ReadUInt(byte[] data, int offset) =>
        (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);

    // Chunk lengths are at most 2^31 - 1
    private static int ChunkLength(byte[] header)
    {
        var length = (int)ReadUInt(header, 0);
        return length < 0 ? throw new InvalidDataException("Invalid PNG chunk length") : length;
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
    {
        if (ReadAtMost(stream, buffer, offset, count) != count)
            throw new EndOfStreamException("PNG file ends early");
    }

    // Fewer bytes than asked for only at the end of the stream
    private static int ReadAtMost(Stream stream, byte[] buffer, int offset, int count)
    {
        var total = 0;
        while (total < count)
        {
            var read = stream.Read(buffer, offset + total, count - total);
            if (read <= 0)
                break;

            total += read;
        }

        return total;
    }

    private static void Skip(Stream stream, long count)
    {
        if (stream.CanSeek)
        {
            if (stream.Position + count > stream.Length)
                throw new EndOfStreamException("PNG file ends early");

            stream.Seek(count, SeekOrigin.Current);
            return;
        }

        var buffer = new byte[(int)Math.Min(count, 64 * 1024)];
        while (count > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(count, buffer.Length));
            if (read <= 0)
                throw new EndOfStreamException("PNG file ends early");

            count -= read;
        }
    }

    private static readonly uint[] CrcTable = CreateCrcTable();

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }

    private const uint ChunkIhdr = 0x49484452;
    private const uint ChunkPlte = 0x504C5445;
    private const uint ChunkIdat = 0x49444154;
    private const uint ChunkIend = 0x49454E44;
    private const uint ChunkTrns = 0x74524E53;
    private const uint ChunkText = 0x74455874;
    private const uint ChunkPhys = 0x70485973;
    private const uint ChunkGama = 0x67414D41;

    // The payloads of consecutive IDAT chunks as one stream, the zlib data of the image. Once they end, the header of
    // the chunk after them. IDAT CRCs are skipped unchecked, like the game's decoder does.
    //
    // The game's decoder reads the image data in pieces of 8 KiB, and a piece spanning two chunk boundaries gets the data
    // after the second one at a wrong offset (ZlibInflateStream.Read adds up the bytes read so far twice): corrupt image
    // data on .NET Framework, an endless loop with dotnet-compat on .NET. So between the first and the last IDAT chunk,
    // every one has to be 8 KiB or more, as libpng writes them.
    private sealed class ImageData(Stream file, int length) : Stream
    {
        private const int MinMiddleChunk = 8192;

        private readonly byte[] header = new byte[8];
        private int remaining = length;
        private int chunkLength = length;
        private bool firstChunk = true;
        private bool end;

        public int NextLength { get; private set; }
        public uint NextType { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0)
                return 0;

            while (remaining == 0)
            {
                if (end)
                    return 0;

                NextChunk();
            }

            var read = file.Read(buffer, offset, Math.Min(count, remaining));
            if (read <= 0)
                throw new EndOfStreamException("PNG file ends early");

            remaining -= read;
            return read;
        }

        // Skips the image data the zlib stream did not need
        public void SkipToEnd()
        {
            while (!end)
            {
                Skip(file, remaining);
                remaining = 0;
                NextChunk();
            }
        }

        // CRC of the chunk just read, then the header of the next one
        private void NextChunk()
        {
            PngReader.ReadExactly(file, header, 0, 4);
            PngReader.ReadExactly(file, header, 0, 8);
            var length = ChunkLength(header);
            var type = ReadUInt(header, 4);
            if (type != ChunkIdat)
            {
                end = true;
                NextLength = length;
                NextType = type;
                return;
            }

            // The game's decoder takes an empty IDAT chunk for the end of the image data
            if (length == 0)
                throw new InvalidDataException("Empty PNG image data chunk");

            if (!firstChunk && chunkLength < MinMiddleChunk)
                throw new InvalidDataException("PNG image data chunks too small for the game's decoder");

            firstChunk = false;
            chunkLength = length;
            remaining = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
