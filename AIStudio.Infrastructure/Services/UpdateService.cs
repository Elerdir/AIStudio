using System.Diagnostics;
using System.Reflection;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using Serilog;
using UpdateHub.Client;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Kontroluje aktualizace přes UpdateHub server (https://updatehub.niderle.cz).
/// Před stažením/spuštěním ověří SHA-256 podpisu — žádný blind exec.
///
/// Server musí vracet manifest pro aplikaci se slugem <c>ai-studio</c>
/// s odpovídající platformou a architekturou. Pokud server neběží nebo
/// klient nemá <see cref="AppSettings.CheckForUpdates"/> = true, vrací null
/// (silent fail — neblokujeme start aplikace).
/// </summary>
public sealed class UpdateService : IUpdateService
{
    /// <summary>
    /// Slug aplikace v UpdateHub administraci — musí přesně odpovídat tomu,
    /// co je registrováno v admin UI updatehub.niderle.cz.
    /// </summary>
    private const string AppSlug = "ai-studio";

    /// <summary>Base URL UpdateHub serveru (production).</summary>
    private const string DefaultServerUrl = "https://updatehub.niderle.cz";

    private readonly HttpClient        _http;
    private readonly IDownloadService  _downloader;
    private readonly ISettingsService  _settings;

    public Version CurrentVersion { get; } =
        Assembly.GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
                .Split('+')[0]   // ořízne git hash suffix (0.1.0+abc1234)
                is string v && System.Version.TryParse(v, out var parsed)
            ? parsed
            : new Version(0, 1, 0);

    public UpdateService(IHttpClientFactory httpFactory,
                         IDownloadService    downloader,
                         ISettingsService    settings)
    {
        _http       = httpFactory.CreateClient("update");
        _downloader = downloader;
        _settings   = settings;
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        // Respektuj uživatelské nastavení — pokud má vypnuté kontroly, nic neděláme.
        // Defaultně OFF dokud nepotvrdíme, že server běží.
        if (!_settings.Settings.CheckForUpdates)
        {
            Log.Debug("UpdateService: kontrola aktualizací je vypnuta (Settings.CheckForUpdates=false)");
            return null;
        }

        var channel = string.IsNullOrWhiteSpace(_settings.Settings.UpdateChannel)
            ? "stable"
            : _settings.Settings.UpdateChannel;

        try
        {
            Log.Information("UpdateService: kontrola {Server} (slug={Slug}, current={Version}, channel={Channel}, platform={Platform}/{Arch})",
                DefaultServerUrl, AppSlug, CurrentVersion, channel,
                UpdateHubClient.CurrentPlatform, UpdateHubClient.CurrentArch);

            using var client = new UpdateHubClient(_http, DefaultServerUrl, AppSlug);
            var result = await client.CheckForUpdateAsync(
                currentVersion: CurrentVersion.ToString(3),
                channel:        channel,
                ct:             ct);

            if (!result.HasUpdate || string.IsNullOrEmpty(result.DownloadUrl))
            {
                Log.Information("UpdateService: verze {Current} je aktuální (latest={Latest})",
                    CurrentVersion, result.LatestVersion);
                return null;
            }

            Log.Information("UpdateService: dostupná verze {Version} ({Url}, sha256={Sha})",
                result.LatestVersion,
                result.DownloadUrl,
                string.IsNullOrEmpty(result.Sha256) ? "?" : result.Sha256[..8] + "…");

            return new UpdateInfo(
                Version:      result.LatestVersion,
                DownloadUrl:  result.DownloadUrl,
                ReleaseNotes: result.ReleaseNotes ?? string.Empty,
                PublishedAt:  default,
                Sha256:       result.Sha256,
                IsMandatory:  result.IsMandatory);
        }
        catch (UpdateHubException ex)
        {
            // Server nedosažitelný / nevrátil platnou odpověď — silent fail.
            // Neblokujeme aplikaci, jen logujeme.
            Log.Warning("UpdateService: UpdateHub nedostupný: {Msg}", ex.Message);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "UpdateService: neočekávaná chyba při kontrole aktualizací");
            return null;
        }
    }

    public async Task DownloadAndInstallAsync(
        UpdateInfo update,
        IProgress<DownloadProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "AIStudio_Update");
        Directory.CreateDirectory(tempDir);

        var fileName      = Path.GetFileName(new Uri(update.DownloadUrl).LocalPath);
        var installerPath = Path.Combine(tempDir, fileName);

        Log.Information("UpdateService: stahuji {Url} → {Path}", update.DownloadUrl, installerPath);

        // Předáme SHA-256 do DownloadService — pokud nesedí, DownloadService
        // vyhodí ChecksumMismatchException a my installer nespouštíme.
        await _downloader.DownloadFileAsync(
            url:            update.DownloadUrl,
            destPath:       installerPath,
            progress:       progress,
            expectedSha256: update.Sha256,
            ct:             ct);

        // Druhá kontrola — paranoidní obrana, kdyby DownloadService chybu nevyhodil
        if (!string.IsNullOrEmpty(update.Sha256))
        {
            if (!UpdateHubClient.VerifySha256(installerPath, update.Sha256))
            {
                Log.Error("UpdateService: SHA-256 nesouhlasí pro {Path} — instalátor BYL SMAZÁN", installerPath);
                try { File.Delete(installerPath); } catch { /* best effort */ }
                throw new InvalidOperationException(
                    "Stažený instalátor neprošel SHA-256 ověřením. " +
                    "Stahování bylo přerušeno z bezpečnostních důvodů.");
            }
            Log.Information("UpdateService: SHA-256 ověřeno OK");
        }
        else
        {
            // Manifest nedodal hash — zalogujeme jako varování, ale neblokujeme
            // (server může být ve stavu kdy hashe ještě nepodává).
            Log.Warning("UpdateService: manifest pro {Version} neobsahuje SHA-256 — instalátor BUDE SPUŠTĚN BEZ OVĚŘENÍ",
                        update.Version);
        }

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
