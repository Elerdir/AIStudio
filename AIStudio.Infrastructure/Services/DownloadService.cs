using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;

namespace AIStudio.Infrastructure.Services;

public sealed class DownloadService : IDownloadService
{
    private readonly HttpClient _http;
    private const int BufferSize = 81_920; // 80 KB chunks

    public DownloadService(IHttpClientFactory httpFactory)
    {
        _http = httpFactory.CreateClient("download");
    }

    public async Task DownloadFileAsync(
        string url,
        string destPath,
        IProgress<DownloadProgressInfo>? progress = null,
        string? apiToken = null,
        CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(destPath)
                  ?? throw new InvalidOperationException($"Neplatná cesta: {destPath}");
        Directory.CreateDirectory(dir);

        var tmpPath = destPath + ".tmp";

        // ── Resume: zjisti velikost případného dočasného souboru ──────────────
        var existingBytes = 0L;
        if (File.Exists(tmpPath))
            existingBytes = new FileInfo(tmpPath).Length;

        var finalUrl = BuildUrl(url, apiToken);

        HttpResponseMessage response;
        long startAt = existingBytes;

        // Zkus s Range hlavičkou; některé servery (nebo CDN) ji nepodporují
        if (existingBytes > 0)
        {
            using var rangedReq = BuildRequest(finalUrl, apiToken);
            rangedReq.Headers.Range = new RangeHeaderValue(existingBytes, null);
            response = await _http.SendAsync(rangedReq, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable
                || response.StatusCode == HttpStatusCode.OK)
            {
                // Server nepodporuje resume nebo vrátil celý soubor — začni znovu
                Log.Information("Server nepodporuje resume pro {Url}, stahuju od začátku", url);
                response.Dispose();
                startAt = 0;
                existingBytes = 0;

                using var fullReq = BuildRequest(finalUrl, apiToken);
                response = await _http.SendAsync(fullReq, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            else
            {
                Log.Information("Obnovuji stahování {Url} od {Bytes} B", url, existingBytes);
            }
        }
        else
        {
            using var req = BuildRequest(finalUrl, apiToken);
            response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }

        response.EnsureSuccessStatusCode();

        // ── Celková velikost souboru ──────────────────────────────────────────
        // 206 Partial: Content-Length = zbývající bytes; přičti co už máme
        // 200 Full:    Content-Length = celková velikost
        var contentLength = response.Content.Headers.ContentLength ?? -1L;
        var total = response.StatusCode == HttpStatusCode.PartialContent && contentLength >= 0
            ? contentLength + existingBytes
            : contentLength;

        // ── Stream do souboru ─────────────────────────────────────────────────
        var fileMode = startAt > 0 ? FileMode.Append : FileMode.Create;
        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(tmpPath, fileMode, FileAccess.Write,
                                              FileShare.None, BufferSize, useAsync: true);

        var buffer     = new byte[BufferSize];
        var downloaded = startAt;   // započítáme již stažené
        var speedBps   = 0.0;

        var sw            = Stopwatch.StartNew();
        var lastSpeedTime  = sw.Elapsed;
        var lastSpeedBytes = startAt;

        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;

            // Přepočet rychlosti každých ~500 ms
            var now       = sw.Elapsed;
            var sinceLast = now - lastSpeedTime;
            if (sinceLast.TotalSeconds >= 0.5)
            {
                speedBps      = (downloaded - lastSpeedBytes) / sinceLast.TotalSeconds;
                lastSpeedTime  = now;
                lastSpeedBytes = downloaded;
            }

            progress?.Report(new DownloadProgressInfo(downloaded, total, speedBps));
        }

        await dst.FlushAsync(ct);
        dst.Close();
        response.Dispose();

        // ── Atomicky přesuneme tmp → finální soubor ───────────────────────────
        if (File.Exists(destPath))
            File.Delete(destPath);
        File.Move(tmpPath, destPath);

        Log.Information("Staženo {DestPath} ({Total} B)", destPath, downloaded);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HttpRequestMessage BuildRequest(string url, string? apiToken)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);

        // HuggingFace používá Bearer token v hlavičce
        if (!string.IsNullOrEmpty(apiToken) && !url.Contains("civitai.com", StringComparison.OrdinalIgnoreCase))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);

        return req;
    }

    private static string BuildUrl(string url, string? token)
    {
        if (string.IsNullOrEmpty(token)) return url;

        // Civitai přijímá token jako query parametr
        if (url.Contains("civitai.com", StringComparison.OrdinalIgnoreCase))
            return url.Contains('?') ? $"{url}&token={token}" : $"{url}?token={token}";

        return url;
    }
}
