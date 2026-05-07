using System.Net.Http.Headers;
using System.Text.Json;
using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Civitai REST klient. Doc: <see href="https://github.com/civitai/civitai/wiki/REST-API-Reference"/>.
///
/// Token v <c>AppSettings.CivitaiApiKey</c> jde do Bearer hlavičky pro search/list,
/// ale pro vlastní download Civitai používá jiný mechanismus — přidá ho jako
/// <c>?token=…</c> query param. Bez tokenu se gated/login-walled soubory nestáhnou.
/// </summary>
public sealed class CivitaiClient : ICivitaiClient
{
    private readonly ISettingsService _settings;
    private const    string           BaseUrl = "https://civitai.com/api/v1";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders =
        {
            { "User-Agent", "AIStudio/1.0 (https://github.com/aistudio)" }
        }
    };

    public CivitaiClient(ISettingsService settings)
    {
        _settings = settings;
    }

    public async Task<IReadOnlyList<CivitaiModelInfo>> SearchAsync(
        string?           query     = null,
        CivitaiType       type      = CivitaiType.Any,
        string?           baseModel = null,
        CivitaiSort       sort      = CivitaiSort.MostDownloaded,
        int               limit     = 20,
        bool              nsfw      = false,
        CancellationToken ct        = default)
    {
        // Civitai bere `limit` v rozsahu 1–100. Cap kvůli nezbedným callům.
        limit = Math.Clamp(limit, 1, 100);

        var url = BuildSearchUrl(query, type, baseModel, sort, limit, nsfw);

        try
        {
            using var req  = BuildAuthorizedRequest(HttpMethod.Get, url);
            using var resp = await Http.SendAsync(req, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                Log.Warning("Civitai search HTTP {Code}: {Body}",
                            (int)resp.StatusCode, Trunc(body, 300));
                return Array.Empty<CivitaiModelInfo>();
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("items", out var items))
                return Array.Empty<CivitaiModelInfo>();

            var list = new List<CivitaiModelInfo>(limit);
            foreach (var item in items.EnumerateArray())
            {
                var parsed = ParseModel(item);
                if (parsed is not null) list.Add(parsed);
            }

            Log.Information("Civitai search '{Q}' (type={Type}, base={Base}) → {N} výsledků",
                            query ?? "(top)", type, baseModel ?? "*", list.Count);
            return list;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Civitai search '{Q}' selhal", query);
            return Array.Empty<CivitaiModelInfo>();
        }
    }

    public string BuildAuthorizedDownloadUrl(string downloadUrl)
    {
        var token = _settings.Settings.CivitaiApiKey;
        if (string.IsNullOrWhiteSpace(token)) return downloadUrl;

        // Civitai download endpoint akceptuje token v query parametru. Pokud už
        // tam ?something existuje, přidáme &token=, jinak ?token=.
        var sep = downloadUrl.Contains('?') ? '&' : '?';
        return $"{downloadUrl}{sep}token={Uri.EscapeDataString(token)}";
    }

    public string BuildModelPageUrl(long modelId) =>
        $"https://civitai.com/models/{modelId}";

    // ── Internals ─────────────────────────────────────────────────────────────

    private static string BuildSearchUrl(string? query, CivitaiType type, string? baseModel,
                                          CivitaiSort sort, int limit, bool nsfw)
    {
        var parts = new List<string>
        {
            $"limit={limit}",
            $"sort={Uri.EscapeDataString(SortToString(sort))}",
        };

        // Civitai REST API očekává `nsfw` jako boolean. Sémantika podle Wiki:
        //   • nsfw=false → vrátí jen non-NSFW
        //   • nsfw nepřítomné → vrátí oboje (default)
        //   • nsfw=true → vrátí jen NSFW
        // Když chceme safe-only, posíláme false; když uživatel chce vše,
        // parametr neposíláme vůbec. Předchozí pokus s "X"/"None" způsoboval
        // 400 ZodError protože Civitai validátor vyžaduje boolean.
        if (!nsfw)
            parts.Add("nsfw=false");

        if (!string.IsNullOrWhiteSpace(query))
            parts.Add($"query={Uri.EscapeDataString(query)}");

        if (type != CivitaiType.Any)
            parts.Add($"types={TypeToString(type)}");

        if (!string.IsNullOrWhiteSpace(baseModel))
            parts.Add($"baseModels={Uri.EscapeDataString(baseModel)}");

        return $"{BaseUrl}/models?{string.Join('&', parts)}";
    }

    private static string SortToString(CivitaiSort s) => s switch
    {
        CivitaiSort.HighestRated   => "Highest Rated",
        CivitaiSort.MostDownloaded => "Most Downloaded",
        CivitaiSort.Newest         => "Newest",
        _                          => "Most Downloaded"
    };

    private static string TypeToString(CivitaiType t) => t switch
    {
        CivitaiType.Checkpoint        => "Checkpoint",
        CivitaiType.LORA              => "LORA",
        CivitaiType.LoCon             => "LoCon",
        CivitaiType.TextualInversion  => "TextualInversion",
        CivitaiType.Hypernetwork      => "Hypernetwork",
        CivitaiType.AestheticGradient => "AestheticGradient",
        CivitaiType.Controlnet        => "Controlnet",
        CivitaiType.Poses             => "Poses",
        _                             => "Checkpoint"
    };

    private static CivitaiModelInfo? ParseModel(JsonElement item)
    {
        try
        {
            var id          = item.GetProperty("id").GetInt64();
            var name        = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var modelType   = item.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            var description = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            var nsfw        = item.TryGetProperty("nsfw", out var ns) && ns.ValueKind == JsonValueKind.True;

            var creator = item.TryGetProperty("creator", out var c) &&
                          c.ValueKind == JsonValueKind.Object &&
                          c.TryGetProperty("username", out var cu)
                ? cu.GetString() ?? ""
                : "";

            // stats objekt: { downloadCount, rating, ratingCount, … }
            long downloadCount = 0;
            double rating = 0;
            int ratingCount = 0;
            if (item.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Object)
            {
                if (stats.TryGetProperty("downloadCount", out var dc) && dc.ValueKind == JsonValueKind.Number)
                    downloadCount = dc.GetInt64();
                if (stats.TryGetProperty("rating", out var rt) && rt.ValueKind == JsonValueKind.Number)
                    rating = rt.GetDouble();
                if (stats.TryGetProperty("ratingCount", out var rc) && rc.ValueKind == JsonValueKind.Number)
                    ratingCount = rc.GetInt32();
            }

            // První obrázek první verze = thumbnail. Civitai vrací modelVersions[].images[].
            string? thumb = null;
            var versions = new List<CivitaiModelVersion>();
            if (item.TryGetProperty("modelVersions", out var mvs) && mvs.ValueKind == JsonValueKind.Array)
            {
                foreach (var mv in mvs.EnumerateArray())
                {
                    var version = ParseVersion(mv);
                    if (version is not null) versions.Add(version);

                    if (thumb is null &&
                        mv.TryGetProperty("images", out var imgs) &&
                        imgs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var img in imgs.EnumerateArray())
                        {
                            if (img.TryGetProperty("url", out var iu))
                            {
                                thumb = iu.GetString();
                                break;
                            }
                        }
                    }
                }
            }

            return new CivitaiModelInfo(
                id, name, modelType, description, creator,
                nsfw, downloadCount, rating, ratingCount,
                thumb, versions);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Civitai: parse model item selhal");
            return null;
        }
    }

    private static CivitaiModelVersion? ParseVersion(JsonElement mv)
    {
        try
        {
            var id        = mv.GetProperty("id").GetInt64();
            var name      = mv.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

            // Civitai může mít buď baseModel string, nebo baseModel + baseModelType
            var baseModel = mv.TryGetProperty("baseModel", out var bm) ? bm.GetString() ?? "" : "";
            var pubAt     = mv.TryGetProperty("publishedAt", out var pa) &&
                            pa.ValueKind == JsonValueKind.String &&
                            DateTime.TryParse(pa.GetString(), out var dt)
                ? dt
                : DateTime.MinValue;

            var files = new List<CivitaiModelFile>();
            if (mv.TryGetProperty("files", out var fs) && fs.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in fs.EnumerateArray())
                {
                    var fileInfo = ParseFile(f);
                    if (fileInfo is not null) files.Add(fileInfo);
                }
            }

            return new CivitaiModelVersion(id, name, baseModel, pubAt, files);
        }
        catch
        {
            return null;
        }
    }

    private static CivitaiModelFile? ParseFile(JsonElement f)
    {
        try
        {
            var id      = f.GetProperty("id").GetInt64();
            var name    = f.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var type    = f.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            var sizeKb  = f.TryGetProperty("sizeKB", out var sk) && sk.ValueKind == JsonValueKind.Number
                           ? (long)sk.GetDouble() : 0;
            var primary = f.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True;

            // Civitai vnořuje formát do `metadata.format`. Pro starší entries
            // je `format` na top-level, takže zkusíme oba.
            string format = "";
            if (f.TryGetProperty("metadata", out var meta) &&
                meta.ValueKind == JsonValueKind.Object &&
                meta.TryGetProperty("format", out var mf))
            {
                format = mf.GetString() ?? "";
            }
            else if (f.TryGetProperty("format", out var ff))
            {
                format = ff.GetString() ?? "";
            }

            var downloadUrl = f.TryGetProperty("downloadUrl", out var du) ? du.GetString() ?? "" : "";

            // Soubory bez download URL nemají smysl posílat dál.
            if (string.IsNullOrEmpty(downloadUrl)) return null;

            return new CivitaiModelFile(id, name, type, sizeKb, format, downloadUrl, primary);
        }
        catch
        {
            return null;
        }
    }

    private HttpRequestMessage BuildAuthorizedRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        var token = _settings.Settings.CivitaiApiKey;
        if (!string.IsNullOrWhiteSpace(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private static string Trunc(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
