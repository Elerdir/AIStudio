using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Stáhne nejnovější ComfyUI Portable (Windows + NVIDIA build) z GitHubu
/// a rozbalí ho. Po dokončení vrátí cesty k <c>main.py</c> (pro spuštění)
/// a <c>python.exe</c> (vlastní embedded Python z portable buildu).
///
/// Architektonická poznámka: ComfyService a ComfyInstaller jsou záměrně oddělené
/// — Service umí jen řídit běžící proces, Installer řeší jen one-shot instalaci.
/// </summary>
public sealed class ComfyInstaller : IComfyInstaller
{
    private static readonly HttpClient Http = new()
    {
        // 30 minut hard cap — i 2 GB stažení by mělo být dávno hotové na běžné lince
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders =
        {
            { "User-Agent", "AIStudio/1.0 (https://github.com/aistudio)" }
        }
    };

    private const string LatestReleaseUrl =
        "https://api.github.com/repos/comfyanonymous/ComfyUI/releases/latest";

    public string DefaultInstallDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIStudio", "ComfyUI");

    // ── Detekce existující instalace ──────────────────────────────────────────

    public (string ComfyUiDir, string PythonPath)? DetectExisting(string installDir)
    {
        if (!Directory.Exists(installDir)) return null;

        var paths = ResolvePortablePaths(installDir);
        return paths is { } p && File.Exists(Path.Combine(p.ComfyUiDir, "main.py")) &&
               File.Exists(p.PythonPath)
            ? p
            : null;
    }

    // ── Hlavní instalace ──────────────────────────────────────────────────────

    public async Task<(string ComfyUiDir, string PythonPath)> InstallAsync(
        string                            installDir,
        IProgress<ComfyInstallProgress>?  progress = null,
        CancellationToken                 ct       = default)
    {
        Directory.CreateDirectory(installDir);

        // Pokud je už nainstalováno, nic nestahuj
        if (DetectExisting(installDir) is { } existing)
        {
            Log.Information("ComfyInstaller: existující instalace v {Dir}", installDir);
            progress?.Report(new(ComfyInstallStage.Done, "ComfyUI je již nainstalován",
                                 100, 0, 0, 0, null));
            return existing;
        }

        // ── 1) Najdi latest release URL ───────────────────────────────────────
        progress?.Report(new(ComfyInstallStage.FetchingRelease,
            "Zjišťuji nejnovější verzi…", 0, 0, 0, 0, null));

        var (assetUrl, assetName, assetSize) = await FindLatestPortableAssetAsync(ct);
        if (assetUrl is null)
        {
            throw new InvalidOperationException(
                "Nepodařilo se najít ComfyUI Portable v GitHub releases. " +
                "Zkontroluj internet nebo si stáhni ComfyUI ručně.");
        }

        Log.Information("ComfyInstaller: latest portable = {Name} ({Size} MB)",
                        assetName, assetSize / 1_048_576);

        // ── 2) Stáhni .7z ─────────────────────────────────────────────────────
        var sevenZipPath = Path.Combine(installDir, $"{assetName}.partial");

        try
        {
            await DownloadWithProgressAsync(assetUrl, sevenZipPath, progress, ct);

            // ── 3) Rozbal ─────────────────────────────────────────────────────
            // Extract běží na background threadu — SharpCompress je sync API
            // a my se nechceme blokovat na UI threadu (ač sem nevoláme z UI,
            // installer může být volaný z VM commandu, který používá UI sync ctx).
            await Task.Run(() => Extract7z(sevenZipPath, installDir, progress, ct), ct);
        }
        finally
        {
            // ── 4) Smaž stažený .7z ───────────────────────────────────────────
            // Po úspěchu i selhání — partial soubory jsou bezcenné
            try
            {
                if (File.Exists(sevenZipPath)) File.Delete(sevenZipPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ComfyInstaller: nelze smazat {Path}", sevenZipPath);
            }
        }

        // ── 5) Najdi cesty po rozbalení ───────────────────────────────────────
        progress?.Report(new(ComfyInstallStage.Finishing, "Ověřuji instalaci…",
                             95, 0, 0, 0, null));

        var resolved = ResolvePortablePaths(installDir);
        if (resolved is null ||
            !File.Exists(Path.Combine(resolved.Value.ComfyUiDir, "main.py")) ||
            !File.Exists(resolved.Value.PythonPath))
        {
            throw new InvalidOperationException(
                "Po rozbalení nebyl nalezen main.py nebo python.exe. " +
                $"Zkontroluj obsah {installDir}.");
        }

        progress?.Report(new(ComfyInstallStage.Done, "Hotovo!", 100, 0, 0, 0, null));
        Log.Information("ComfyInstaller: instalace hotova → {Comfy} | {Python}",
                        resolved.Value.ComfyUiDir, resolved.Value.PythonPath);

        return resolved.Value;
    }

    // ── Detekce cest v rozbalené struktuře ────────────────────────────────────

    /// <summary>
    /// Najde v <paramref name="installDir"/> rozbalenou ComfyUI Portable strukturu.
    /// Standardní cesta po rozbalení: <c>installDir/ComfyUI_windows_portable/{ComfyUI,python_embeded}</c>.
    /// Pokud uživatel rozbalil jinou strukturu, projdeme ji rekurzivně (max 3 úrovně).
    /// </summary>
    private static (string ComfyUiDir, string PythonPath)? ResolvePortablePaths(string installDir)
    {
        if (!Directory.Exists(installDir)) return null;

        // Standardní cesta — rychlá kontrola
        foreach (var portable in new[]
        {
            installDir,
            Path.Combine(installDir, "ComfyUI_windows_portable"),
            Path.Combine(installDir, "ComfyUI_windows_portable_nvidia"),
        })
        {
            var comfy  = Path.Combine(portable, "ComfyUI");
            var python = Path.Combine(portable, "python_embeded", "python.exe");
            if (File.Exists(Path.Combine(comfy, "main.py")) && File.Exists(python))
                return (comfy, python);
        }

        // Fallback: rekurzivní hledání — pomalu, ale spolehlivě
        try
        {
            var mainPy = Directory
                .EnumerateFiles(installDir, "main.py", SearchOption.AllDirectories)
                .FirstOrDefault(p => File.Exists(
                    Path.Combine(Path.GetDirectoryName(p)!, "..", "python_embeded", "python.exe")));

            if (mainPy is not null)
            {
                var comfy = Path.GetDirectoryName(mainPy)!;
                var python = Path.GetFullPath(
                    Path.Combine(comfy, "..", "python_embeded", "python.exe"));
                if (File.Exists(python))
                    return (comfy, python);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ComfyInstaller: rekurzivní hledání struktury selhalo");
        }

        return null;
    }

    // ── GitHub API: hledání správného assetu ──────────────────────────────────

    private async Task<(string? Url, string Name, long Size)>
        FindLatestPortableAssetAsync(CancellationToken ct)
    {
        var json = await Http.GetStringAsync(LatestReleaseUrl, ct);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("assets", out var assets) ||
            assets.ValueKind != JsonValueKind.Array)
        {
            return (null, string.Empty, 0);
        }

        // Hledáme asset s "windows_portable" + "nvidia" + ".7z" v názvu.
        // Preferujeme novější CUDA (cu128 > cu126 > bez specifikace).
        var candidates = new List<(string Url, string Name, long Size, int Score)>();

        foreach (var a in assets.EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            var url  = a.GetProperty("browser_download_url").GetString() ?? "";
            var size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;

            if (!name.Contains("windows_portable", StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.Contains("nvidia",            StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.EndsWith(".7z",               StringComparison.OrdinalIgnoreCase)) continue;

            // Skóre podle CUDA verze — vyšší = preferovanější
            var score = 0;
            if      (name.Contains("cu128", StringComparison.OrdinalIgnoreCase)) score = 30;
            else if (name.Contains("cu126", StringComparison.OrdinalIgnoreCase)) score = 20;
            else if (name.Contains("cu124", StringComparison.OrdinalIgnoreCase)) score = 10;

            candidates.Add((url, name, size, score));
        }

        if (candidates.Count == 0) return (null, string.Empty, 0);

        var best = candidates.OrderByDescending(c => c.Score).First();
        return (best.Url, best.Name, best.Size);
    }

    // ── Stahování s progress reportem ─────────────────────────────────────────

    private static async Task DownloadWithProgressAsync(
        string                            url,
        string                            destPath,
        IProgress<ComfyInstallProgress>?  progress,
        CancellationToken                 ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? -1L;

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write,
                                              FileShare.None, 81_920, useAsync: true);

        var buffer     = new byte[81_920];
        long downloaded = 0;

        var sw            = Stopwatch.StartNew();
        var lastTime       = sw.Elapsed;
        var lastBytes      = 0L;
        var bytesPerSecond = 0.0;

        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;

            // Throttle progress reportů — jednou za ~250 ms
            var now       = sw.Elapsed;
            var sinceLast = now - lastTime;
            if (sinceLast.TotalMilliseconds >= 250)
            {
                bytesPerSecond = (downloaded - lastBytes) / sinceLast.TotalSeconds;
                lastTime       = now;
                lastBytes      = downloaded;

                // Stahování drží 0–80 % celkového progress baru, rozbalování zbylých 20 %
                var pct = total > 0 ? (int)(downloaded * 80 / total) : 0;
                progress?.Report(new(
                    ComfyInstallStage.Downloading,
                    BuildDownloadStatus(downloaded, total, bytesPerSecond),
                    pct, downloaded, total, bytesPerSecond, null));
            }
        }

        // Final progress kick — i kdybychom skončili dřív než tick
        progress?.Report(new(
            ComfyInstallStage.Downloading,
            BuildDownloadStatus(downloaded, total, bytesPerSecond),
            80, downloaded, total, bytesPerSecond, null));
    }

    private static string BuildDownloadStatus(long downloaded, long total, double bps)
    {
        var dlMb = downloaded / 1_048_576;
        var head = total > 0
            ? $"Stahuji {dlMb} / {total / 1_048_576} MB"
            : $"Stahuji {dlMb} MB";

        if (bps < 1) return head;
        if (bps < 1_048_576) return $"{head}  ({bps / 1024.0:F0} KB/s)";
        return $"{head}  ({bps / 1_048_576.0:F1} MB/s)";
    }

    // ── Rozbalení .7z ─────────────────────────────────────────────────────────

    /// <summary>
    /// Rozbalí .7z archiv. Pořadí pokusů:
    ///   1) Externí <c>7z.exe</c> — nativní, multithreaded, 5–10× rychlejší.
    ///      Hledáme ho v PATH a na běžných instalačních cestách.
    ///   2) Fallback: SharpCompress — managed .NET, single-threaded, pomalý
    ///      ale 100% spolehlivý (žádná závislost na externích nástrojích).
    /// </summary>
    private static void Extract7z(
        string                            sevenZipPath,
        string                            destDir,
        IProgress<ComfyInstallProgress>?  progress,
        CancellationToken                 ct)
    {
        var sevenZipExe = FindInstalled7Zip();
        if (sevenZipExe is not null)
        {
            Log.Information("ComfyInstaller: nalezen 7-Zip → {Exe}, rozbaluji externě", sevenZipExe);
            ExtractViaExternalSevenZip(sevenZipExe, sevenZipPath, destDir, progress, ct);
            return;
        }

        Log.Warning("ComfyInstaller: 7-Zip nenalezen, padám na pomalý SharpCompress fallback. " +
                    "Pro výrazně rychlejší rozbalování doporučuj nainstalovat 7-Zip.");
        ExtractViaSharpCompress(sevenZipPath, destDir, progress, ct);
    }

    /// <summary>
    /// Najde nainstalovaný 7-Zip CLI. Hledá v běžných cestách + v PATH.
    /// </summary>
    private static string? FindInstalled7Zip()
    {
        // Nejčastější instalační cesty na Windows
        string[] candidates =
        [
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe",
            // Scoop
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                         "scoop", "apps", "7zip", "current", "7z.exe"),
        ];

        foreach (var path in candidates)
        {
            if (File.Exists(path)) return path;
        }

        // PATH lookup — projdeme PATH a zkusíme najít 7z.exe / 7za.exe
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                foreach (var name in new[] { "7z.exe", "7za.exe" })
                {
                    var full = Path.Combine(dir, name);
                    if (File.Exists(full)) return full;
                }
            }
            catch { /* některé PATH zápisy bývají nevalidní */ }
        }

        return null;
    }

    private static readonly Regex SevenZipProgressRegex =
        new(@"^\s*(\d+)%", RegexOptions.Compiled);

    /// <summary>
    /// Spustí 7z.exe jako externí proces. Progress parsujeme z stdout —
    /// 7-Zip s flagem <c>-bsp1</c> vypisuje řádky typu " 73% 12345 - file.py".
    /// </summary>
    private static void ExtractViaExternalSevenZip(
        string                            sevenZipExe,
        string                            archivePath,
        string                            destDir,
        IProgress<ComfyInstallProgress>?  progress,
        CancellationToken                 ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = sevenZipExe,
            // x = extract s plnými cestami, -y = yes na vše, -bsp1 = progress na stdout, -bb1 = názvy souborů
            Arguments              = $"x \"{archivePath}\" -o\"{destDir}\" -y -bsp1",
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Nepodařilo se spustit 7z.exe");

        var lastReportTick = Environment.TickCount64 - 250;

        proc.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;

            var match = SevenZipProgressRegex.Match(e.Data);
            if (!match.Success) return;
            if (!int.TryParse(match.Groups[1].Value, out var pct)) return;

            // Throttle reportů na ~250 ms — 7-Zip jich vypisuje stovky/s
            var now = Environment.TickCount64;
            if (now - lastReportTick < 250 && pct < 100) return;
            lastReportTick = now;

            // Rozbalování drží 80–95 % celkového progress baru
            progress?.Report(new(
                ComfyInstallStage.Extracting,
                $"Rozbaluji ({pct} %)",
                80 + pct * 15 / 100, 0, 0, 0, null));
        };

        proc.BeginOutputReadLine();

        // Polling cyklus s ct check — Process.WaitForExitAsync(ct) by sice fungovalo,
        // ale pokud uživatel zruší, chceme proces tvrdě zabít.
        while (!proc.WaitForExit(500))
        {
            if (ct.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); }
                catch (Exception ex) { Log.Warning(ex, "ComfyInstaller: kill 7z.exe selhalo"); }
                ct.ThrowIfCancellationRequested();
            }
        }

        if (proc.ExitCode != 0)
        {
            var stderr = proc.StandardError.ReadToEnd();
            throw new InvalidOperationException(
                $"7z.exe skončilo s kódem {proc.ExitCode}. " +
                (string.IsNullOrWhiteSpace(stderr) ? "Bez výstupu na stderr." : stderr));
        }
    }

    /// <summary>
    /// Pomalý managed fallback pro uživatele bez nainstalovaného 7-Zipu.
    /// Pozor: na velkých archivech (~50k souborů) může s aktivním Defenderem
    /// trvat hodiny. UI by mělo uživatele upozornit, ať doinstaluje 7-Zip.
    /// </summary>
    private static void ExtractViaSharpCompress(
        string                            sevenZipPath,
        string                            destDir,
        IProgress<ComfyInstallProgress>?  progress,
        CancellationToken                 ct)
    {
        using var archive = SevenZipArchive.Open(sevenZipPath);

        // Spočítáme jen file entries, abychom mohli reportovat smysluplný progress
        var entries        = archive.Entries.Where(e => !e.IsDirectory).ToList();
        var totalEntries   = entries.Count;
        var processed      = 0;
        var lastReportTick = Environment.TickCount64 - 250;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            entry.WriteToDirectory(destDir, new ExtractionOptions
            {
                ExtractFullPath = true,
                Overwrite       = true
            });

            processed++;

            // Throttle reportů — soubory jsou různě velké, hlásit každý je drahé
            var now = Environment.TickCount64;
            if (now - lastReportTick >= 250 || processed == totalEntries)
            {
                lastReportTick = now;
                // Rozbalování drží 80–95 % celkového progress baru
                var pct = 80 + (processed * 15 / Math.Max(1, totalEntries));
                progress?.Report(new(
                    ComfyInstallStage.Extracting,
                    $"Rozbaluji {processed} / {totalEntries} souborů",
                    pct, 0, 0, 0, null));
            }
        }
    }
}
