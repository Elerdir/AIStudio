using System.Text;
using AIStudio.Core.Services;
using FluentAssertions;

namespace AIStudio.Tests;

/// <summary>
/// Testy vkládání AI-provenience do PNG metadat. Stačí strukturálně validní PNG
/// (signatura + IHDR + IEND) — AddTextChunks needekóduje obraz, jen manipuluje chunky.
/// </summary>
public class PngTextMetadataTests
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    /// <summary>Minimální strukturálně validní PNG: signatura + prázdné IHDR + IEND.</summary>
    private static byte[] MinimalPng()
    {
        var ms = new MemoryStream();
        ms.Write(Signature);
        // IHDR length=0
        ms.Write(new byte[] { 0, 0, 0, 0 });
        ms.Write(Encoding.ASCII.GetBytes("IHDR"));
        ms.Write(new byte[] { 0, 0, 0, 0 }); // fake CRC
        // IEND length=0
        ms.Write(new byte[] { 0, 0, 0, 0 });
        ms.Write(Encoding.ASCII.GetBytes("IEND"));
        ms.Write(new byte[] { 0, 0, 0, 0 }); // fake CRC
        return ms.ToArray();
    }

    private static bool ContainsAscii(byte[] haystack, string needle)
    {
        var n = Encoding.ASCII.GetBytes(needle);
        for (var i = 0; i + n.Length <= haystack.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < n.Length; j++)
                if (haystack[i + j] != n[j]) { ok = false; break; }
            if (ok) return true;
        }
        return false;
    }

    [Fact]
    public void AddAiProvenance_AddsTextChunks_PreservesSignature()
    {
        var png = MinimalPng();

        var result = PngTextMetadata.AddAiProvenance(png, "FLUX.1 Kontext", "a cat");

        result.Length.Should().BeGreaterThan(png.Length, "přidaly se tEXt chunky");
        result.Take(8).Should().Equal(Signature, "signatura zůstává na začátku");
        ContainsAscii(result, "tEXt").Should().BeTrue();
        ContainsAscii(result, "AI Studio").Should().BeTrue();
        ContainsAscii(result, "FLUX.1 Kontext").Should().BeTrue();
        ContainsAscii(result, "AI-generated").Should().BeTrue();
    }

    [Fact]
    public void AddAiProvenance_EndsWithIend()
    {
        var result = PngTextMetadata.AddAiProvenance(MinimalPng(), "model");
        // IEND musí zůstat posledním chunkem (tEXt se vkládá PŘED něj)
        var tail = result.Skip(result.Length - 8).Take(4).ToArray();
        Encoding.ASCII.GetString(tail).Should().Be("IEND");
    }

    [Fact]
    public void AddTextChunks_NonPng_ReturnsUnchanged()
    {
        var notPng = new byte[] { 1, 2, 3, 4, 5 };
        var result = PngTextMetadata.AddAiProvenance(notPng, "model");
        result.Should().Equal(notPng, "neznámý formát se nesmí rozbít");
    }

    [Fact]
    public void AddTextChunks_EmptyEntries_ReturnsOriginal()
    {
        var png = MinimalPng();
        var result = PngTextMetadata.AddTextChunks(png, new List<(string, string)>());
        result.Should().Equal(png);
    }
}
