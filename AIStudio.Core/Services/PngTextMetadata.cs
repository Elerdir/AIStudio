using System.Text;

namespace AIStudio.Core.Services;

/// <summary>
/// Vkládá textové <c>tEXt</c> chunky do PNG bytů — provenience AI obrázků:
/// že jde o AI-generovaný obrázek, jakým modelem a v jaké aplikaci vznikl.
/// Čistá logika nad bytovým polem (manipulace PNG chunků + CRC-32), žádné I/O.
///
/// <para>PNG = 8-bajtová signatura + sekvence chunků (délka|typ|data|CRC).
/// tEXt chunky vkládáme těsně před koncový <c>IEND</c>. Když vstup není validní
/// PNG (ComfyUI by mohl teoreticky vrátit jiný formát), vrátíme byty beze změny.</para>
/// </summary>
public static class PngTextMetadata
{
    private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    /// <summary>Název aplikace zapisovaný do metadat (klíč Software).</summary>
    public const string AppName = "AI Studio";

    /// <summary>
    /// Vrátí byty PNG s doplněnými standardními AI-provenience chunky:
    /// <c>Software=AI Studio</c>, <c>Source=AI-generated</c>, <c>Model=…</c> a
    /// lidský <c>Comment</c>. Když je <paramref name="png"/> jiný formát, vrátí ho beze změny.
    /// </summary>
    public static byte[] AddAiProvenance(byte[] png, string modelName, string? prompt = null)
    {
        var safeModel = string.IsNullOrWhiteSpace(modelName) ? "unknown" : modelName.Trim();

        var entries = new List<(string Keyword, string Text)>
        {
            ("Software", AppName),
            ("Source",   "AI-generated"),
            ("Model",    safeModel),
            ("Comment",  $"AI-generated image. Model: {safeModel}. Created with {AppName}."),
        };
        if (!string.IsNullOrWhiteSpace(prompt))
            entries.Add(("Description", prompt.Trim()));

        return AddTextChunks(png, entries);
    }

    /// <summary>
    /// Vloží tEXt chunky před IEND. Idempotentní jen v tom smyslu, že nepřepisuje
    /// existující — vždy přidává nové (volat jednou při ukládání).
    /// </summary>
    public static byte[] AddTextChunks(byte[] png, IReadOnlyList<(string Keyword, string Text)> entries)
    {
        if (png is null || png.Length < PngSignature.Length || entries is null || entries.Count == 0)
            return png ?? Array.Empty<byte>();

        // Ověř PNG signaturu — jinak vrať beze změny (neznámý formát neriskujeme rozbít)
        for (var i = 0; i < PngSignature.Length; i++)
            if (png[i] != PngSignature[i]) return png;

        // Najdi začátek IEND chunku (4 bajty délky před typem "IEND").
        var iendTypePos = FindChunkType(png, "IEND");
        if (iendTypePos < 0) return png;       // korupce / chybí IEND
        var insertAt = iendTypePos - 4;        // před délkové pole IEND
        if (insertAt < 0) return png;

        using var ms = new MemoryStream(png.Length + entries.Count * 64);
        ms.Write(png, 0, insertAt);
        foreach (var (keyword, text) in entries)
            WriteTextChunk(ms, keyword, text);
        ms.Write(png, insertAt, png.Length - insertAt);
        return ms.ToArray();
    }

    private static void WriteTextChunk(Stream output, string keyword, string text)
    {
        // tEXt data = keyword (Latin-1, 1-79) + 0x00 + text (Latin-1). Non-Latin1
        // znaky nahradíme '?', aby chunk zůstal validní (model names jsou stejně ASCII).
        var latin1 = Encoding.Latin1;
        var kw = latin1.GetBytes(Clamp(keyword, 79));
        var tx = latin1.GetBytes(text ?? string.Empty);

        var data = new byte[kw.Length + 1 + tx.Length];
        Buffer.BlockCopy(kw, 0, data, 0, kw.Length);
        data[kw.Length] = 0x00;
        Buffer.BlockCopy(tx, 0, data, kw.Length + 1, tx.Length);

        var type = Encoding.ASCII.GetBytes("tEXt");

        WriteBigEndian(output, (uint)data.Length);
        output.Write(type, 0, 4);
        output.Write(data, 0, data.Length);

        // CRC-32 přes typ + data
        var crc = Crc32.Compute(type, data);
        WriteBigEndian(output, crc);
    }

    private static string Clamp(string s, int max)
    {
        s = (s ?? string.Empty).Trim();
        return s.Length <= max ? s : s[..max];
    }

    private static void WriteBigEndian(Stream s, uint value)
    {
        s.WriteByte((byte)(value >> 24));
        s.WriteByte((byte)(value >> 16));
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)value);
    }

    /// <summary>Najde pozici 4-bajtového typu chunku (ne délky) podle názvu, nebo -1.</summary>
    private static int FindChunkType(byte[] png, string type)
    {
        var t = Encoding.ASCII.GetBytes(type);
        // Procházíme chunky korektně: od pozice 8 (za signaturou) skáčeme po length+12.
        var pos = PngSignature.Length;
        while (pos + 8 <= png.Length)
        {
            var len = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
            var typePos = pos + 4;
            if (typePos + 4 > png.Length) break;
            if (png[typePos] == t[0] && png[typePos + 1] == t[1] &&
                png[typePos + 2] == t[2] && png[typePos + 3] == t[3])
                return typePos;
            if (len < 0) break;                  // korupce
            pos = typePos + 4 + len + 4;         // typ + data + CRC
        }
        return -1;
    }

    // ── CRC-32 (PNG polynom 0xEDB88320) ──────────────────────────────────────
    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        public static uint Compute(byte[] part1, byte[] part2)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in part1) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            foreach (var b in part2) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
