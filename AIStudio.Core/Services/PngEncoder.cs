using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace AIStudio.Core.Services;

/// <summary>
/// Minimální čistý PNG enkodér (RGB/RGBA, 8 bitů/kanál, bez interlace). Slouží k uložení
/// raw pixelového výstupu z vestavěného generátoru (<c>stable-diffusion.cpp</c> vrací
/// <c>sd_image_t</c> = syrový RGB buffer) bez závislosti na SkiaSharp/ImageSharp.
///
/// <para>Čistá funkce (byte[] → byte[]), plně testovatelná. Filtr 0 (none) na každém řádku,
/// IDAT komprimovaný přes <see cref="DeflateStream"/> obalený zlib hlavičkou + Adler-32.</para>
/// </summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    /// <summary>
    /// Zakóduje pixely do PNG. <paramref name="channels"/> = 3 (RGB) nebo 4 (RGBA).
    /// <paramref name="pixels"/> musí mít aspoň <c>width·height·channels</c> bajtů (row-major,
    /// top-down). Vrací kompletní PNG soubor jako byte[].
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height, int channels)
    {
        if (channels is not (3 or 4)) throw new ArgumentOutOfRangeException(nameof(channels), "Podporováno jen RGB (3) nebo RGBA (4).");
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Rozměry musí být kladné.");
        var stride = width * channels;
        if (pixels.Length < (long)stride * height) throw new ArgumentException("Pixelový buffer je menší než width·height·channels.", nameof(pixels));

        // Filtrované řádky (filtr 0 = none na začátku každého řádku).
        var raw = new byte[(stride + 1) * height];
        for (var y = 0; y < height; y++)
        {
            raw[y * (stride + 1)] = 0;
            pixels.Slice(y * stride, stride).CopyTo(raw.AsSpan(y * (stride + 1) + 1));
        }

        using var ms = new MemoryStream();
        ms.Write(Signature);
        WriteChunk(ms, "IHDR", BuildIhdr(width, height, channels));
        WriteChunk(ms, "IDAT", ZlibCompress(raw));
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static byte[] BuildIhdr(int w, int h, int channels)
    {
        var b = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0), (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)h);
        b[8] = 8;                                   // bit depth
        b[9] = (byte)(channels == 4 ? 6 : 2);       // color type: 2 = RGB, 6 = RGBA
        // b[10..12] = 0: compression=deflate, filter=adaptive, interlace=none
        return b;
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x78);
        ms.WriteByte(0x9C);                         // zlib header (default compression)
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(data, 0, data.Length);
        Span<byte> adler = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(adler, Adler32(data));
        ms.Write(adler);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeBytes, data));
        s.Write(crc);
    }

    private static uint Adler32(byte[] d)
    {
        const uint mod = 65521;
        uint a = 1, b = 0;
        foreach (var x in d) { a = (a + x) % mod; b = (b + a) % mod; }
        return (b << 16) | a;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(byte[] a, byte[] b)
    {
        var c = 0xFFFFFFFFu;
        foreach (var x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
