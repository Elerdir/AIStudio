using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using Serilog;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Kontroluje GitHub Releases API pro novější verzi AI Studia a
/// spustí stažený installer (Inno Setup self-extractor).
/// GitHub repo se nastavuje přes konstanty níže.
/// </summary>
public sealed class UpdateService : IUpdateService
{
    private const string GitHubOwner = "oksystem";
    private const string GitHubRepo  = "aistudio";
    private const string ApiUrl      = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";

    private readonly HttpClient        _http;
    private readonly IDownloadService  _downloader;

    public Version CurrentVersion { get; } =
        Assembly.GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
                .Split('+')[0]   // ořízne git hash suffix (0.1.0+abc1234)
                is string v && System.Version.TryParse(v, out var parsed)
            ? parsed
            : new Version(0, 1, 0);

    public UpdateService(IHttpClientFactory httpFactory, IDownloadService downloader)
    {
        _http       = httpFactory.CreateClient("update");
        _downloader = downloader;
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            Log.Information("UpdateService: kontrola GitHub releases {Url}", ApiUrl);
            var json = await _http.GetStringAsync(ApiUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString() ?? "";
            var versionStr = tagName.TrimStart('v');

            if (!System.Version.TryParse(versionStr, out var remoteVersion))
            {
                Log.Warning("UpdateService: nepodařilo se parsovat verzi '{Tag}'", tagName);
                return null;
            }

            if (remoteVersion <= CurrentVersion)
            {
                Log.Information("UpdateService: verze {Current} je aktuální (remote={Remote})",
                    CurrentVersion, remoteVersion);
                return null;
            }

            // Najdi Windows installer asset (.exe)
            var downloadUrl = string.Empty;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(downloadUrl))
            {
                Log.Warning("UpdateService: nenalezen .exe asset v release {Tag}", tagName);
                return null;
            }

            var notes = root.TryGetProperty("body", out var body)
                ? body.GetString() ?? string.Empty
                : string.Empty;

            DateTimeOffset publishedAt = default;
            if (root.TryGetProperty("published_at", out var pub) &&
                DateTimeOffset.TryParse(pub.GetString(), out var dt))
                publishedAt = dt;

            Log.Information("UpdateService: dostupná verze {Version} ({Url})", versionStr, downloadUrl);
            return new UpdateInfo(versionStr, downloadUrl, notes, publishedAt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "UpdateService: chyba při kontrole aktualizací");
            return null;
        }
    }

    public async Task DownloadAndInstallAsync(
        UpdateInfo update,
        IProgress<DownloadProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        var tempDir     = Path.Combine(Path.GetTempPath(), "AIStudio_Update");
        Directory.CreateDirectory(tempDir);

        var fileName    = Path.GetFileName(new Uri(update.DownloadUrl).LocalPath);
        var installerPath = Path.Combine(tempDir, fileName);

        Log.Information("UpdateService: stahuji {Url} → {Path}", update.DownloadUrl, installerPath);
        await _downloader.DownloadFileAsync(update.DownloadUrl, installerPath, progress, ct: ct);

        Log.Information("UpdateService: spouštím installer {Path}", installerPath);

        // /SILENT — bez průvodce, /CLOSEAPPLICATIONS — zavře naši aplikaci
        Process.Start(new ProcessStartInfo
        {
            FileName        = installerPath,
            Arguments       = "/SILENT /CLOSEAPPLICATIONS",
            UseShellExecute = true,
        });

        // Dej instalátoru chvilku nastartovat, pak sami ukončíme
        await Task.Delay(1500, ct);
        Environment.Exit(0);
    }
}
