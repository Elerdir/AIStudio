using System.Text;
using System.Text.Json;
using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// LLM-based parser surového popisu na <see cref="ImageIntent"/>. Volá
/// <see cref="ILlamaService"/> s structured output system promptem, vyzobává
/// JSON z odpovědi (ochrana proti tomu, že malý model přidá vysvětlení kolem)
/// a parsuje. Při jakémkoliv selhání vrací fallback intent — nikdy nehází.
/// </summary>
public sealed class ImageIntentParser : IImageIntentParser
{
    private readonly ILlamaService _llama;

    public ImageIntentParser(ILlamaService llama) => _llama = llama;

    public async Task<ImageIntent> ParseAsync(string rawPrompt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawPrompt))
            return DefaultIntent("", "Prázdný prompt");

        if (!_llama.IsLoaded)
        {
            // Pre-condition: caller (ChatPageViewModel) by měl chat LLM načíst PŘED
            // voláním parseru. Když se sem dostaneme, nějak to selhalo — log + fallback.
            // Pro CZ rawPrompt to skoro jistě vyplodí halucinaci na SDXL/FLUX (modely
            // neumí česky), proto explicitní warning v reasoning.
            var looksCzech = ContainsCzechChars(rawPrompt);
            Log.Warning("ImageIntentParser: LLM není načtený, fallback bez překladu " +
                        "(prompt vypadá {Lang})", looksCzech ? "česky — SDXL pravděpodobně halucinuje" : "anglicky");
            return DefaultIntent(rawPrompt, looksCzech
                ? "Bez SmartParseru — popisuj anglicky, jinak SDXL nemusí rozumět"
                : "Bez SmartParseru");
        }

        var systemPrompt = BuildSystemPrompt();
        var userPrompt   = $"Popis: {rawPrompt}\n\nJSON:";

        var raw = new StringBuilder();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));

            await foreach (var token in _llama.ChatAsync(
                new[] { ("system", systemPrompt), ("user", userPrompt) },
                maxTokens:    400,
                temperature:  0.2f,
                ct:           cts.Token))
            {
                raw.Append(token);
                // Hard cap — LLM by nemělo vyhrnout román, ale pojistka
                if (raw.Length > 3000) break;
            }
        }
        catch (OperationCanceledException)
        {
            return DefaultIntent(rawPrompt, "LLM timeout");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ImageIntentParser: LLM volání selhalo");
            return DefaultIntent(rawPrompt, $"LLM error: {ex.Message}");
        }

        var json = ExtractJson(raw.ToString());
        if (json is null)
        {
            Log.Warning("ImageIntentParser: nenašel JSON v odpovědi LLM ({Len} znaků)", raw.Length);
            return DefaultIntent(rawPrompt, "LLM nevrátil JSON");
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var intent = new ImageIntent(
                Kind:           ParseKind(root, "kind"),
                Aspect:         ParseAspect(root, "aspect"),
                Quality:        ParseQuality(root, "quality"),
                EnglishPrompt:  GetStr(root, "english_prompt") ?? rawPrompt,
                NegativePrompt: GetStr(root, "negative_prompt") ?? DefaultNegative(ParseKind(root, "kind")),
                Reasoning:      GetStr(root, "reasoning") ?? "");

            Log.Information("ImageIntentParser: '{Raw}' → kind={Kind} aspect={Aspect} quality={Quality}",
                Trunc(rawPrompt, 80), intent.Kind, intent.Aspect, intent.Quality);

            return intent;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ImageIntentParser: JSON parse selhal — payload: {Json}",
                Trunc(json, 300));
            return DefaultIntent(rawPrompt, "JSON shape mismatch");
        }
    }

    // ── System prompt construction ────────────────────────────────────────────

    private static string BuildSystemPrompt() =>
        "Jsi parser uživatelských popisů obrázků. Vstup je popis (česky nebo anglicky). " +
        "Vrátíš POUZE JSON v přesně tomto schématu — žádný markdown, žádné komentáře, žádný text mimo JSON:\n\n" +
        "{\n" +
        "  \"kind\": \"realistic\" | \"anime\" | \"stylized\" | \"abstract\",\n" +
        "  \"aspect\": \"square\" | \"landscape\" | \"portrait\",\n" +
        "  \"quality\": \"fast\" | \"normal\" | \"hi-res\",\n" +
        "  \"english_prompt\": \"<rozšířený anglický popis pro Stable Diffusion>\",\n" +
        "  \"negative_prompt\": \"<vhodné negative tagy pro daný kind>\",\n" +
        "  \"reasoning\": \"<1 krátká věta proč jsi vybral kind a aspect>\"\n" +
        "}\n\n" +
        "Pravidla pro english_prompt:\n" +
        "- Přelož do angličtiny a rozšiř o 5-10 vizuálních atributů (osvětlení, kompozice, materiály, kvalita).\n" +
        "- Realistic: přidej 'photorealistic, sharp focus, professional photography, natural lighting, 8k'.\n" +
        "- Anime: přidej 'anime style, detailed illustration, vibrant colors' (NIKDY 'photo' nebo 'realistic').\n" +
        "- Stylized: přidej 'digital painting, concept art, cinematic lighting'.\n" +
        "- Abstract: dej volnost, méně konkrétních modifikátorů.\n\n" +
        "Pravidla pro aspect (klíčová slova):\n" +
        "- 'na šířku' / 'landscape' / 'wide' / 'panorama' → landscape\n" +
        "- 'na výšku' / 'portrait' / 'portrét' / 'tall' → portrait\n" +
        "- jinak → square\n\n" +
        "Pravidla pro quality:\n" +
        "- 'rychle' / 'náhled' / 'fast' → fast\n" +
        "- 'vysoké rozlišení' / 'hi-res' / '4k' → hi-res\n" +
        "- jinak → normal\n\n" +
        "Negative prompt: pro realistic typicky 'blurry, low quality, distorted, watermark, text, " +
        "extra fingers, bad anatomy'. Pro anime: 'realistic, photo, 3d render, blurry, low quality'.";

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Najde první dvojici { ... } v textu — chrání proti LLM, který přidá
    /// vysvětlení nebo markdown kolem. Vrací null pokud neexistuje.
    /// </summary>
    private static string? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return null;
        var end = text.LastIndexOf('}');
        if (end <= start) return null;
        return text[start..(end + 1)];
    }

    private static string? GetStr(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static ImageKind ParseKind(JsonElement root, string name) =>
        GetStr(root, name)?.ToLowerInvariant() switch
        {
            "anime"     => ImageKind.Anime,
            "stylized"  => ImageKind.Stylized,
            "abstract"  => ImageKind.Abstract,
            "realistic" => ImageKind.Realistic,
            _           => ImageKind.Auto,
        };

    private static ImageAspect ParseAspect(JsonElement root, string name) =>
        GetStr(root, name)?.ToLowerInvariant() switch
        {
            "landscape" => ImageAspect.Landscape,
            "portrait"  => ImageAspect.Portrait,
            _           => ImageAspect.Square,
        };

    private static ImageQualityHint ParseQuality(JsonElement root, string name) =>
        GetStr(root, name)?.ToLowerInvariant() switch
        {
            "fast"    => ImageQualityHint.Fast,
            "hi-res"  => ImageQualityHint.HighRes,
            "highres" => ImageQualityHint.HighRes,
            _         => ImageQualityHint.Normal,
        };

    private static string DefaultNegative(ImageKind kind) => kind switch
    {
        ImageKind.Anime => "realistic, photo, 3d render, blurry, low quality, watermark",
        _               => "blurry, low quality, distorted, watermark, text, extra fingers, bad anatomy",
    };

    private static ImageIntent DefaultIntent(string rawPrompt, string why) => new(
        Kind:           ImageKind.Auto,
        Aspect:         ImageAspect.Square,
        Quality:        ImageQualityHint.Normal,
        EnglishPrompt:  rawPrompt,
        NegativePrompt: DefaultNegative(ImageKind.Auto),
        Reasoning:      $"Fallback: {why}");

    private static string Trunc(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" :
        s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// Heuristika: obsahuje text některý z typických českých diakritických znaků?
    /// Slouží jen k tomu, abychom v reasoning hint hlásili "popisuj anglicky"
    /// jen když uživatel napsal česky. Není dokonalá (slovenština, polština
    /// detekuje taky), ale pro UX hint stačí.
    /// </summary>
    private static bool ContainsCzechChars(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var ch in s)
        {
            if (ch is 'á' or 'č' or 'ď' or 'é' or 'ě' or 'í' or 'ň' or 'ó' or 'ř'
                   or 'š' or 'ť' or 'ú' or 'ů' or 'ý' or 'ž'
                   or 'Á' or 'Č' or 'Ď' or 'É' or 'Ě' or 'Í' or 'Ň' or 'Ó' or 'Ř'
                   or 'Š' or 'Ť' or 'Ú' or 'Ů' or 'Ý' or 'Ž')
                return true;
        }
        return false;
    }
}
