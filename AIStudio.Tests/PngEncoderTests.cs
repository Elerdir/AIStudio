using System.Buffers.Binary;
using AIStudio.Core.Services;
using FluentAssertions;

namespace AIStudio.Tests;

public class PngEncoderTests
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Encode_ProducesValidPngStructure(int channels)
    {
        const int w = 3, h = 2;
        var pixels = new byte[w * h * channels];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 7);

        var png = PngEncoder.Encode(pixels, w, h, channels);

        png.AsSpan(0, 8).ToArray().Should().Equal(Signature);

        var chunks = ParseChunks(png);
        chunks[0].Type.Should().Be("IHDR");
        chunks[^1].Type.Should().Be("IEND");
        chunks.Should().Contain(c => c.Type == "IDAT");

        // IHDR: width(4) height(4) depth(1) colortype(1) ...
        var ihdr = chunks[0].Data;
        BinaryPrimitives.ReadUInt32BigEndian(ihdr.AsSpan(0)).Should().Be((uint)w);
        BinaryPrimitives.ReadUInt32BigEndian(ihdr.AsSpan(4)).Should().Be((uint)h);
        ihdr[8].Should().Be(8);                                  // bit depth
        ihdr[9].Should().Be((byte)(channels == 4 ? 6 : 2));      // color type
    }

    [Fact]
    public void Encode_ChunkCrcsAreValid()
    {
        var png = PngEncoder.Encode(new byte[2 * 2 * 3], 2, 2, 3);
        foreach (var c in ParseChunks(png))
            c.CrcOk.Should().BeTrue($"CRC chunku {c.Type} musí sedět");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public void Encode_InvalidChannels_Throws(int channels)
    {
        var act = () => PngEncoder.Encode(new byte[12], 2, 2, channels);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Encode_TooSmallBuffer_Throws()
    {
        var act = () => PngEncoder.Encode(new byte[5], 4, 4, 3);
        act.Should().Throw<ArgumentException>();
    }

    // ── PNG chunk parser (jen pro test) ───────────────────────────────────────
    private sealed record Chunk(string Type, byte[] Data, bool CrcOk);

    private static List<Chunk> ParseChunks(byte[] png)
    {
        var list = new List<Chunk>();
        var p = 8; // skip signature
        while (p + 12 <= png.Length)
        {
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(p));
            var type = System.Text.Encoding.ASCII.GetString(png, p + 4, 4);
            var data = png.AsSpan(p + 8, len).ToArray();
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(p + 8 + len));
            var crcOk = Crc32(png.AsSpan(p + 4, 4 + len)) == crc;
            list.Add(new Chunk(type, data, crcOk));
            p += 12 + len;
        }
        return list;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var c = 0xFFFFFFFFu;
        foreach (var x in bytes)
        {
            c ^= x;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return c ^ 0xFFFFFFFF;
    }
}
