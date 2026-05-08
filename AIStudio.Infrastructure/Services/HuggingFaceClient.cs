using System.Net.Http.Json;
using System.Text.Json;
using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// TenkĂ˝ HTTP klient nad <c>https://huggingface.co/api</c>. UmĂ­ vyhledat
/// modely podle dotazu a vylistovat soubory v repu. Ĺ˝ĂˇdnĂ˝ token nepotĹ™ebuje
/// pro veĹ™ejnĂ© modely; gated modely vyĹľadujĂ­ HF token (pĹ™edĂˇme pĹ™es Settings).
/// </summary>
public sealed class HuggingFaceClient : IHuggingFaceClient
{
    private readonly ISettingsService   _settings;
    private readonly IHttpClientFactory _httpFactory;
    private const    string             BaseUrl = "https://huggingface.co";

    public HuggingFaceClient(ISettingsService settings, IHttpClientFactory httpFactory)
    {
        _settings    = settings;
        _httpFactory = httpFactory;
    }

    public async Task<IReadOnlyList<HfModelInfo>> SearchGgufModelsAsync(
        string            query,
        int               limit = 20,
        CancellationToken ct    = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<HfModelInfo>();

        // filter=gguf zfiltruje repos, kterĂ© obsahujĂ­ GGUF soubory.
        // sort=downloads + direction=-1 = nejstahovanÄ›jĹˇĂ­ prvnĂ­.
        var url = $"{BaseUrl}/api/models" +
                  $"?search={Uri.EscapeDataString(query)}" +
                  $"&filter=gguf" +
                  $"&sort=downloads&direction=-1" +
                  $"&limit={limit}";

        try
        {
            using var http = _httpFactory.CreateClient("huggingface");
            using var req  = BuildAuthorizedRequest(HttpMethod.Get, url);
            using var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var list = new List<HfModelInfo>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var id = item.TryGetProperty("id",        out var idEl) ? idEl.GetString() ?? "" : "";
                var dl = item.TryGetProperty("downloads", out var dlEl) ? dlEl.GetInt64()       : 0;
                var lk = item.TryGetProperty("likes",     out var lkEl) ? lkEl.GetInt64()       : 0;
                var lm = item.TryGetProperty("lastModified", out var lmEl) &&
                         lmEl.ValueKind == JsonValueKind.String &&
                         DateTime.TryParse(lmEl.GetString(), out var dt)
                    ? dt
                    : DateTime.MinValue;

                if (string.IsNullOrEmpty(id)) continue;
                list.Add(new HfModelInfo(id, dl, lk, lm));
            }

            Log.Information("HF search '{Query}' â†’ {Count} vĂ˝sledkĹŻ", query, list.Count);
            return list;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HF search '{Query}' selhal", query);
            throw;
        }
    }

    public async Task<IReadOnlyList<HfFileInfo>> ListGgufFilesAsync(
        string            repoId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repoId)) return Array.Empty<HfFileInfo>();

        var url = $"{BaseUrl}/api/models/{repoId}/tree/main";

        try
        {
            using var http = _httpFactory.CreateClient("huggingface");
            using var req  = BuildAuthorizedRequest(HttpMethod.Get, url);
            using var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var list = new List<HfFileInfo>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var tEl) ? tEl.GetString() : null;
                if (type != "file") continue;

                var path = item.TryGetProperty("path", out var pEl) ? pEl.GetString() ?? "" : "";
                var size = item.TryGetProperty("size", out var sEl) ? sEl.GetInt64()       : 0;

                if (!path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new HfFileInfo(path, size));
            }

            // ĹadĂ­me podle velikosti vzestupnÄ› â€” uĹľivatel obvykle hledĂˇ menĹˇĂ­ kvantizaci napĹ™ed
            return list.OrderBy(f => f.Size).ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HF tree '{Repo}' selhal", repoId);
            throw;
        }
    }

    public string BuildDownloadUrl(string repoId, string filePath) =>
        $"{BaseUrl}/{repoId}/resolve/main/{filePath}";

    public string BuildModelPageUrl(string repoId) =>
        $"{BaseUrl}/{repoId}";

    public async Task<IReadOnlyList<HfModelInfo>> SearchModelsAsync(
        string?           query     = null,
        string?           filter    = null,
        string?           task      = null,
        string            sort      = "downloads",
        int               direction = -1,
        int               limit     = 20,
        CancellationToken ct        = default)
    {
        var parts = new List<string>
        {
            $"limit={Math.Clamp(limit, 1, 100)}",
            $"sort={Uri.EscapeDataString(sort)}",
            $"direction={direction}"
        };

        if (!string.IsNullOrWhiteSpace(query))
            parts.Add($"search={Uri.EscapeDataString(query)}");

        if (!string.IsNullOrWhiteSpace(filter))
            parts.Add($"filter={Uri.EscapeDataString(filter)}");

        if (!string.IsNullOrWhiteSpace(task))
            parts.Add($"pipeline_tag={Uri.EscapeDataString(task)}");

        var url = $"{BaseUrl}/api/models?{string.Join('&', parts)}";

        try
        {
            using var http = _httpFactory.CreateClient("huggingface");
            using var req  = BuildAuthorizedRequest(HttpMethod.Get, url);
            using var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var list = new List<HfModelInfo>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var id = item.TryGetProperty("id",        out var idEl) ? idEl.GetString() ?? "" : "";
                var dl = item.TryGetProperty("downloads", out var dlEl) ? dlEl.GetInt64()       : 0;
                var lk = item.TryGetProperty("likes",     out var lkEl) ? lkEl.GetInt64()       : 0;
                var lm = item.TryGetProperty("lastModified", out var lmEl) &&
                         lmEl.ValueKind == JsonValueKind.String &&
                         DateTime.TryParse(lmEl.GetString(), out var dt)
                    ? dt
                    : DateTime.MinValue;

                if (string.IsNullOrEmpty(id)) continue;
                list.Add(new HfModelInfo(id, dl, lk, lm));
            }

            Log.Information("HF search query='{Q}' filter='{F}' task='{T}' â†’ {Count}",
                            query ?? "*", filter ?? "*", task ?? "*", list.Count);
            return list;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HF search query='{Q}' filter='{F}' task='{T}' selhal",
                        query, filter, task);
            return Array.Empty<HfModelInfo>();
        }
    }

    public async Task<string> GetModelDescriptionAsync(string repoId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repoId)) return string.Empty;

        var url = $"{BaseUrl}/api/models/{repoId}";
        try
        {
            using var http = _httpFactory.CreateClient("huggingface");
            using var req  = BuildAuthorizedRequest(HttpMethod.Get, url);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return string.Empty;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            // Preferujeme cardData.summary (krĂˇtkĂ˝ vĂ˝tah), pokud autor doplnil.
            if (doc.RootElement.TryGetProperty("cardData", out var card) &&
                card.ValueKind == JsonValueKind.Object &&
                card.TryGetProperty("summary", out var sum) &&
                sum.ValueKind == JsonValueKind.String)
            {
                var s = sum.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }

            // Fallback: tags joined â€” mĂˇlo, ale lepĹˇĂ­ neĹľ nic.
            if (doc.RootElement.TryGetProperty("tags", out var tags) &&
                tags.ValueKind == JsonValueKind.Array)
            {
                var tagList = tags.EnumerateArray()
                                  .Select(t => t.GetString())
                                  .Where(s => !string.IsNullOrEmpty(s))
                                  .Take(8)
                                  .ToArray();
                if (tagList.Length > 0)
                    return string.Join(" Â· ", tagList!);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HF GetModelDescriptionAsync '{Repo}' selhal", repoId);
        }
        return string.Empty;
    }

    /// <summary>
    /// Pokud mĂˇ uĹľivatel v NastavenĂ­ HF token, pĹ™idĂˇ ho jako Bearer Auth header â€”
    /// umoĹľnĂ­ stahovĂˇnĂ­ gated modelĹŻ (Llama, Gemmaâ€¦). Bez tokenu jen veĹ™ejnĂ©.
    /// </summary>
    private HttpRequestMessage BuildAuthorizedRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        var token = _settings.Settings.HuggingFaceToken;
        if (!string.IsNullOrWhiteSpace(token))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return req;
    }
}
