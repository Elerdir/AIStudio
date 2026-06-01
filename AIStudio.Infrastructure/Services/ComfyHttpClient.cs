using System.Text;
using System.Text.Json;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using Serilog;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// HTTP klient pro ComfyUI server. Stateless — drží jen <see cref="HttpClient"/>
/// z named factory „comfy". Volání se parametrizují portem, takže service
/// funguje i při změně portu za běhu.
///
/// **Error handling konvence:**
///  • Read metody (GetCheckpoints/GetLoras/IsHealthy) při I/O chybě vrací
///    fallback (empty list / false) + warn log — nikdy nevyhazují.
///  • Mutate metody (QueuePrompt/UploadImage) propagují HTTP errory přes
///    <see cref="InvalidOperationException"/> s lidsky čitelnou hláškou.
///  • <see cref="ComfyExecutionException"/> pro execution errory z ComfyUI
///    (na rozdíl od WebSocket / HTTP chyb).
/// </summary>
public sealed class ComfyHttpClient : IComfyHttpClient
{
    private readonly HttpClient _http;

    public ComfyHttpClient(IHttpClientFactory factory)
    {
        _http = factory.CreateClient("comfy");
    }

    private static string BaseUrl(int port) => $"http://localhost:{port}";

    // ── Health ────────────────────────────────────────────────────────────────

    public async Task<bool> IsHealthyAsync(int port, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            var resp = await _http.GetAsync($"{BaseUrl(port)}/system_stats", cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ── Read API ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> GetCheckpointsAsync(int port, CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync($"{BaseUrl(port)}/object_info/CheckpointLoaderSimple", ct);
            return ExtractStringArrayFromObjectInfo(json, "CheckpointLoaderSimple", "ckpt_name");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "ComfyHttpClient: GetCheckpoints selhalo");
            return Array.Empty<string>();
        }
    }

    public async Task<IReadOnlyList<string>> GetLorasAsync(int port, CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync($"{BaseUrl(port)}/object_info/LoraLoader", ct);
            return ExtractStringArrayFromObjectInfo(json, "LoraLoader", "lora_name");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "ComfyHttpClient: GetLoras selhalo");
            return Array.Empty<string>();
        }
    }

    public async Task<int> GetQueueDepthAsync(int port, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"{BaseUrl(port)}/queue", ct);
            if (!resp.IsSuccessStatusCode) return -1;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var running = doc.RootElement.TryGetProperty("queue_running", out var qr)
                ? qr.GetArrayLength() : 0;
            var pending = doc.RootElement.TryGetProperty("queue_pending", out var qp)
                ? qp.GetArrayLength() : 0;
            return running + pending;
        }
        catch
        {
            return -1;
        }
    }

    public async Task FreeMemoryAsync(int port, CancellationToken ct = default)
    {
        // ComfyUI /free: unload_models uvolní checkpoint/UNET z VRAM, free_memory
        // spustí GC + torch cache flush. Po chat generování tím vrátíme VRAM LLM.
        var body = JsonSerializer.Serialize(new { unload_models = true, free_memory = true });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync($"{BaseUrl(port)}/free", content, ct);
        if (!resp.IsSuccessStatusCode)
            Log.Debug("ComfyHttpClient: /free vrátilo HTTP {Code}", (int)resp.StatusCode);
    }

    // ── Mutate API ────────────────────────────────────────────────────────────

    public async Task<string> QueuePromptAsync(
        int                         port,
        Dictionary<string, object>  workflow,
        string                      clientId,
        CancellationToken           ct = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            prompt    = workflow,
            client_id = clientId,
        });

        using var content  = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync($"{BaseUrl(port)}/prompt", content, ct);

        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(BuildValidationErrorMessage(json));

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("prompt_id").GetString()
               ?? throw new InvalidOperationException("ComfyUI nevratilo prompt_id");
    }

    public async Task<string> UploadImageAsync(int port, string localFilePath, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(localFilePath);
        using var form    = new MultipartFormDataContent();
        using var imgBody = new StreamContent(stream);
        form.Add(imgBody,                    "image", Path.GetFileName(localFilePath));
        form.Add(new StringContent("input"), "type");
        form.Add(new StringContent("true"),  "overwrite");

        using var resp = await _http.PostAsync($"{BaseUrl(port)}/upload/image", form, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"ComfyUI upload selhal (HTTP {(int)resp.StatusCode}): {json}");

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("name").GetString()
               ?? throw new InvalidOperationException("ComfyUI upload nevrátilo název souboru");
    }

    public async Task<ComfyGenerationResult?> FetchHistoryResultAsync(
        int port, string promptId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"{BaseUrl(port)}/history/{promptId}", ct);
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty(promptId, out var entry)) return null;

        // Pokud history hlásí selhání jobu, vyhodíme ComfyExecutionException
        // — polling smyčka pak nepokračuje (čekání by bylo zbytečné).
        if (entry.TryGetProperty("status", out var status))
        {
            var statusStr = status.TryGetProperty("status_str", out var ss) ? ss.GetString() : null;
            var completed = status.TryGetProperty("completed", out var comp) && comp.ValueKind == JsonValueKind.True;
            if (completed && statusStr == "error")
            {
                var errMsg = ExtractExecutionErrorMessage(status);
                throw new ComfyExecutionException(errMsg);
            }
        }

        var images = new List<ComfyImageRef>();
        if (entry.TryGetProperty("outputs", out var outputs))
        {
            foreach (var node in outputs.EnumerateObject())
            {
                if (!node.Value.TryGetProperty("images", out var imgs)) continue;
                foreach (var img in imgs.EnumerateArray())
                {
                    images.Add(new ComfyImageRef(
                        img.GetProperty("filename").GetString() ?? "",
                        img.TryGetProperty("subfolder", out var sf) ? sf.GetString() ?? "" : "",
                        img.TryGetProperty("type", out var t2) ? t2.GetString() ?? "output" : "output"));
                }
            }
        }

        return images.Count > 0 ? new ComfyGenerationResult(promptId, images, DateTime.Now) : null;
    }

    public async Task<byte[]> DownloadImageAsync(
        int port, string filename, string subfolder = "", string type = "output",
        CancellationToken ct = default)
    {
        var url = $"{BaseUrl(port)}/view?" +
                  $"filename={Uri.EscapeDataString(filename)}" +
                  $"&subfolder={Uri.EscapeDataString(subfolder)}" +
                  $"&type={Uri.EscapeDataString(type)}";

        return await _http.GetByteArrayAsync(url, ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// ComfyUI <c>/object_info/{NodeName}</c> vrací schéma uzlu. Pro picker
    /// (ckpt_name / lora_name / vae_name…) jsou hodnoty v
    /// <c>{NodeName}.input.required.{Field}[0]</c> jako pole stringů.
    /// </summary>
    private static IReadOnlyList<string> ExtractStringArrayFromObjectInfo(
        string json, string nodeName, string fieldName)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement
                .GetProperty(nodeName)
                .GetProperty("input")
                .GetProperty("required")
                .GetProperty(fieldName)[0] is { ValueKind: JsonValueKind.Array } arr)
        {
            return arr.EnumerateArray()
                      .Select(e => e.GetString() ?? "")
                      .Where(s => !string.IsNullOrEmpty(s))
                      .OrderBy(s => s)
                      .ToList();
        }
        return Array.Empty<string>();
    }

    /// <summary>
    /// Z 400 odpovědi ComfyUI poskládá lidsky čitelnou hlášku.
    /// Typický payload obsahuje <c>node_errors</c> mapu node_id → list chyb,
    /// kde každá chyba má <c>message</c> a <c>details</c>.
    /// </summary>
    public static string BuildValidationErrorMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var message = root.TryGetProperty("error", out var err) &&
                          err.ValueKind == JsonValueKind.Object &&
                          err.TryGetProperty("message", out var m)
                ? m.GetString() ?? "Workflow validace selhala"
                : "Workflow validace selhala";

            var details = new List<string>();
            if (root.TryGetProperty("node_errors", out var nodeErrors) &&
                nodeErrors.ValueKind == JsonValueKind.Object)
            {
                foreach (var node in nodeErrors.EnumerateObject())
                {
                    if (!node.Value.TryGetProperty("errors", out var errs)) continue;
                    foreach (var e in errs.EnumerateArray())
                    {
                        var nodeMsg = e.TryGetProperty("message", out var em) ? em.GetString() : null;
                        var det     = e.TryGetProperty("details", out var ed) ? ed.GetString() : null;
                        if (!string.IsNullOrEmpty(nodeMsg))
                            details.Add(string.IsNullOrEmpty(det) ? nodeMsg : $"{nodeMsg} — {det}");
                    }
                }
            }

            return details.Count == 0
                ? message
                : $"{message}: {string.Join("; ", details)}";
        }
        catch
        {
            return string.IsNullOrWhiteSpace(json)
                ? "ComfyUI vrátilo prázdnou chybovou odpověď"
                : $"ComfyUI: {json[..Math.Min(json.Length, 300)]}";
        }
    }

    /// <summary>
    /// Extrahuje exception_message z <c>messages[]</c> pole v <c>status</c> entry.
    /// Hledáme <c>execution_error</c> typ; pokud není, vrátíme generickou hlášku.
    /// </summary>
    private static string ExtractExecutionErrorMessage(JsonElement status)
    {
        if (!status.TryGetProperty("messages", out var msgs) || msgs.ValueKind != JsonValueKind.Array)
            return "ComfyUI job selhal (bez detailu)";

        foreach (var m in msgs.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Array || m.GetArrayLength() < 2) continue;
            if (m[0].GetString() != "execution_error") continue;
            if (m[1].ValueKind != JsonValueKind.Object) continue;
            if (!m[1].TryGetProperty("exception_message", out var em)) continue;
            return $"ComfyUI chyba: {em.GetString()}";
        }
        return "ComfyUI job selhal (bez detailu)";
    }
}
