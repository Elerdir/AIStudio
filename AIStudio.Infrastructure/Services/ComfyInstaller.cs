using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;
using SevenZipExtractor;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Factories;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Core.Services;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Windows-specifická implementace <see cref="IComfyInstaller"/>. Stáhne
/// nejnovější ComfyUI Portable (Windows + NVIDIA build) z GitHubu a rozbalí
/// ho. Po dokončení vrátí cesty k <c>main.py</c> a embedded <c>python.exe</c>.
///
/// Architektonická poznámka: ComfyService a Installer jsou záměrně oddělené —
/// Service řídí běžící proces, Installer řeší jen one-shot instalaci.
///
/// Pro macOS port přijde <c>MacOsComfyInstaller</c> používající <c>git clone</c>
/// + <c>python -m venv</c> + <c>pip install -r requirements.txt</c> (žádné
/// portable buildy pro macOS Apple Silicon neexistují).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsComfyInstaller : IComfyInstaller
{
    private readonly IDownloadService _downloader;
    private readonly IGpuDetector?    _gpuDetector;

    private static readonly HttpClient Http = new()
    {
        // 30 minut hard cap — pro GitHub API a malé ZIPy custom nodů
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders =
        {
            { "User-Agent", "AIStudio/1.0 (https://github.com/aistudio)" }
        }
    };

    public WindowsComfyInstaller(IDownloadService downloader, IGpuDetector? gpuDetector = null)
    {
        _downloader  = downloader;
        _gpuDetector = gpuDetector;
    }

    private const string LatestReleaseUrl =
        "https://api.github.com/repos/comfyanonymous/ComfyUI/releases/latest";

    private const string GgufNodeZipUrl =
        "https://github.com/city96/ComfyUI-GGUF/archive/refs/heads/main.zip";

    private const string GgufNodeFolderName = "ComfyUI-GGUF";

    private const string VhsNodeZipUrl =
        "https://github.com/Kosinkadink/ComfyUI-VideoHelperSuite/archive/refs/heads/main.zip";

    private const string VhsNodeFolderName = "ComfyUI-VideoHelperSuite";

    // FaceDetailer = ComfyUI-Impact-Pack (FaceDetailer node) + ComfyUI-Impact-Subpack
    // (UltralyticsDetectorProvider) + detekční model. Pozor na názvy větví (Main vs main).
    private const string ImpactPackZipUrl =
        "https://github.com/ltdrdata/ComfyUI-Impact-Pack/archive/refs/heads/Main.zip";
    private const string ImpactPackFolderName = "ComfyUI-Impact-Pack";
    private const string ImpactSubpackZipUrl =
        "https://github.com/ltdrdata/ComfyUI-Impact-Subpack/archive/refs/heads/main.zip";
    private const string ImpactSubpackFolderName = "ComfyUI-Impact-Subpack";
    private const string FaceDetectorModelUrl =
        "https://huggingface.co/Bingsu/adetailer/resolve/main/face_yolov8m.pt";

    // RIFE frame interpolation = ComfyUI-Frame-Interpolation (node „RIFE VFI"). RIFE model
    // si node dotáhne sám při prvním běhu, takže instalujeme jen node + jeho requirements.
    private const string FrameInterpZipUrl =
        "https://github.com/Fannovel16/ComfyUI-Frame-Interpolation/archive/refs/heads/main.zip";
    private const string FrameInterpFolderName = "ComfyUI-Frame-Interpolation";

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

        return await DownloadExtractResolveAsync(installDir, progress, ct);
    }

    /// <summary>
    /// Aktualizace na nejnovější portable: stáhne nejnovější build a rozbalí ho <b>přes</b>
    /// stávající instalaci. 7z merge zachová uživatelská data (modely / custom_nodes / output
    /// jsou soubory navíc, které v archivu nejsou) a přepíše jen jádro ComfyUI + python_embeded.
    /// Na rozdíl od <see cref="InstallAsync"/> přeskočí „už nainstalováno" kontrolu.
    /// </summary>
    public Task<(string ComfyUiDir, string PythonPath)> UpdateToLatestAsync(
        string installDir, IProgress<ComfyInstallProgress>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(installDir);
        return DownloadExtractResolveAsync(installDir, progress, ct);
    }

    private async Task<(string ComfyUiDir, string PythonPath)> DownloadExtractResolveAsync(
        string installDir, IProgress<ComfyInstallProgress>? progress, CancellationToken ct)
    {
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

        // ── 2) Stáhni .7z přes IDownloadService (Range request resume support).
        //    Pokud stažení spadne nebo uživatel cancel, .tmp zůstane na disku
        //    a další pokus pokračuje od dosažené pozice.
        var sevenZipPath = Path.Combine(installDir, assetName);

        // Adapter: DownloadProgressInfo → ComfyInstallProgress.
        // Stahování drží 0–80 % celkového progress baru, rozbalování zbylých 20 %.
        var downloadProgress = new Progress<DownloadProgressInfo>(info =>
        {
            var pct = info.Total > 0 ? (int)(info.Downloaded * 80 / info.Total) : 0;
            progress?.Report(new ComfyInstallProgress(
                ComfyInstallStage.Downloading,
                BuildDownloadStatus(info.Downloaded, info.Total, info.BytesPerSecond),
                pct, info.Downloaded, info.Total, info.BytesPerSecond, null));
        });

        try
        {
            await _downloader.DownloadFileAsync(
                assetUrl, sevenZipPath, downloadProgress, apiToken: null, ct);

            // ── 3) Rozbal ─────────────────────────────────────────────────────
            // Extract běží na background threadu — SharpCompress je sync API
            // a my se nechceme blokovat na UI threadu (ač sem nevoláme z UI,
            // installer může být volaný z VM commandu, který používá UI sync ctx).
            await Task.Run(() => Extract7z(sevenZipPath, installDir, progress, ct), ct);

            // ── 3b) Cross-python guard (update přes starší instalaci) ─────────
            // Když nový build nese jinou verzi Pythonu (cu126 = 3.12, aktuální = 3.13),
            // merge nechá v python_embeded smíchané cpXXX .pyd/dll obou verzí a ComfyUI
            // nenastartuje. Detekce: dvě různé python3NN.dll → smaž python_embeded a
            // rozbal archiv znovu (vytvoří ho načisto; archiv je v tuhle chvíli ještě
            // na disku). Pip extra balíky (torch-directml, gguf) se doinstalují při
            // startu ComfyUI přes Ensure* služby.
            if (FindMixedPythonDir(installDir) is { } mixedPythonDir)
            {
                Log.Warning("ComfyInstaller: detekován mix verzí Pythonu v {Dir} — čistá re-extrakce python_embeded",
                            mixedPythonDir);
                progress?.Report(new(ComfyInstallStage.Extracting,
                    "Nová verze má jiný Python — stavím prostředí načisto…", 85, 0, 0, 0, null));

                Directory.Delete(mixedPythonDir, recursive: true);
                await Task.Run(() => Extract7z(sevenZipPath, installDir, progress, ct), ct);
            }
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

    /// <summary>
    /// Najde <c>python_embeded</c> složku, ve které po merge-extrakci skončily DVĚ různé
    /// verze Pythonu (např. <c>python312.dll</c> + <c>python313.dll</c>) — známka toho, že
    /// update přepsal instalaci s jinou verzí Pythonu a prostředí je nekonzistentní.
    /// Vrací cestu ke smíchané složce, jinak null. (Generický <c>python3.dll</c> stub se
    /// nepočítá — verze poznáme jen podle plného <c>python3NN.dll</c>.)
    /// </summary>
    internal static string? FindMixedPythonDir(string installDir)
    {
        try
        {
            if (ResolvePortablePaths(installDir) is not { } paths) return null;
            var pythonDir = Path.GetDirectoryName(paths.PythonPath);
            if (pythonDir is null || !Directory.Exists(pythonDir)) return null;

            var versions = Directory.EnumerateFiles(pythonDir, "python3*.dll")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n is not null && n.Length > "python3".Length &&
                            n["python3".Length..].All(char.IsDigit))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return versions.Count >= 2 ? pythonDir : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ComfyInstaller: kontrola mixu Python verzí selhala");
            return null;
        }
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

        // Standardní cesta — rychlá kontrola. Kořenová složka archivu se mezi releasy
        // jmenuje různě (ComfyUI_windows_portable, ..._nvidia, ..._nvidia_cu126, …),
        // proto bereme installDir + KAŽDOU podsložku začínající na "ComfyUI_windows_portable".
        var fastCandidates = new List<string> { installDir };
        try
        {
            fastCandidates.AddRange(
                Directory.EnumerateDirectories(installDir, "ComfyUI_windows_portable*"));
        }
        catch (Exception ex) { Log.Debug(ex, "ComfyInstaller: enumerace portable podsložek selhala"); }

        foreach (var portable in fastCandidates)
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

        var byName = new Dictionary<string, (string Url, long Size)>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in assets.EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            var url  = a.GetProperty("browser_download_url").GetString() ?? "";
            var size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            if (!string.IsNullOrEmpty(name)) byName[name] = (url, size);
        }

        // Výběr NVIDIA varianty podle generace GPU (viz ComfyPortableAssetPicker):
        // moderní karty (RTX 20+ vč. RTX 50/Blackwell) dostanou bezsufixový build
        // s nejnovější CUDA; GTX 10 a starší legacy cu126 build.
        var legacy = await IsLegacyGpuAsync(ct);
        var bestName = ComfyPortableAssetPicker.PickBest(byName.Keys.ToList(), legacy);
        if (bestName is null) return (null, string.Empty, 0);

        Log.Information("ComfyInstaller: vybraný portable asset = {Name} (legacyGpu={Legacy})",
                        bestName, legacy);
        var best = byName[bestName];
        return (best.Url, bestName, best.Size);
    }

    /// <summary>
    /// True když detekovaná NVIDIA karta patří do legacy generace (GTX 10 a starší) —
    /// pak jí patří cu126 build. Bez detektoru / při chybě vrací false (moderní build
    /// je správný default pro RTX 20+ včetně řady 50).
    /// </summary>
    private async Task<bool> IsLegacyGpuAsync(CancellationToken ct)
    {
        if (_gpuDetector is null) return false;
        try
        {
            var gpu = await _gpuDetector.DetectAsync(ct);
            return ComfyPortableAssetPicker.IsLegacyNvidiaGpu(gpu.Name);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "ComfyInstaller: GPU detekce pro výběr assetu selhala — beru moderní build");
            return false;
        }
    }

    // ── Format helper pro download status (volaný z adapteru v InstallAsync) ──

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
    ///   1) Externí <c>7z.exe</c> — nativní, multithreaded, nejrychlejší.
    ///   2) Bundlovaný <c>7z.dll</c> přes SevenZipExtractor NuGet — nativní rychlost, žádná instalace.
    ///   3) Fallback: SharpCompress — managed .NET, single-threaded, pomalý
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

        try
        {
            Log.Information("ComfyInstaller: zkouším nativní 7z.dll přes SevenZipExtractor");
            ExtractViaNative7z(sevenZipPath, destDir, progress, ct);
            return;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Warning(ex, "ComfyInstaller: nativní extrakce selhala, padám na SharpCompress fallback");
        }

        Log.Warning("ComfyInstaller: 7z.dll nenalezena nebo nefunkční, padám na pomalý SharpCompress fallback.");
        ExtractViaSharpCompress(sevenZipPath, destDir, progress, ct);
    }

    /// <summary>
    /// Rozbalí .7z archiv přes nativní 7z.dll bundlovanou v SevenZipExtractor NuGet balíčku.
    /// Výrazně rychlejší než SharpCompress — žádná instalace ze strany uživatele.
    /// </summary>
    private static void ExtractViaNative7z(
        string                            archivePath,
        string                            destDir,
        IProgress<ComfyInstallProgress>?  progress,
        CancellationToken                 ct)
    {
        ct.ThrowIfCancellationRequested();

        using var archive = new ArchiveFile(archivePath);

        var totalFiles = archive.Entries.Count(e => !e.IsFolder);
        progress?.Report(new(ComfyInstallStage.Extracting,
            $"Rozbaluji {totalFiles} souborů (nativní 7z engine)…", 81, 0, 0, 0, null));

        archive.Extract(destDir, overwrite: true);

        ct.ThrowIfCancellationRequested();
        progress?.Report(new(ComfyInstallStage.Extracting,
            "Rozbalování dokončeno", 94, 0, 0, 0, null));
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
        // SharpCompress 0.48+ — nový factory pattern. SevenZipArchive.Open(string)
        // byl odstraněn (security fix), všechny formáty jdou přes IArchiveFactory.OpenArchive.
        IArchiveFactory factory = new SevenZipFactory();
        using var stream  = File.OpenRead(sevenZipPath);
        using var archive = factory.OpenArchive(stream);

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

    // ── ComfyUI-GGUF custom node ──────────────────────────────────────────────

    public bool IsGgufNodeInstalled(string comfyUiDir)
    {
        if (string.IsNullOrWhiteSpace(comfyUiDir)) return false;

        var nodePath = Path.Combine(comfyUiDir, "custom_nodes", GgufNodeFolderName);
        if (!Directory.Exists(nodePath)) return false;

        // Detekujeme přítomnost typických souborů ComfyUI-GGUF (nodes.py + __init__.py).
        return File.Exists(Path.Combine(nodePath, "nodes.py"))
            || File.Exists(Path.Combine(nodePath, "__init__.py"));
    }

    public async Task EnsureGgufNodeInstalledAsync(
        string                            comfyUiDir,
        string                            pythonExe,
        IProgress<ComfyInstallProgress>?  progress = null,
        CancellationToken                 ct       = default)
    {
        if (IsGgufNodeInstalled(comfyUiDir))
        {
            Log.Information("ComfyInstaller: ComfyUI-GGUF custom node už nainstalovaný");
            return;
        }

        var customNodesDir = Path.Combine(comfyUiDir, "custom_nodes");
        Directory.CreateDirectory(customNodesDir);

        // ── 1) Stáhneme ZIP z GitHubu (~80 KB, žádné API throttling problémy) ───
        progress?.Report(new(ComfyInstallStage.Downloading,
            "Stahuji ComfyUI-GGUF custom node…", 0, 0, 0, 0, null));

        var tempZip = Path.Combine(Path.GetTempPath(), $"ComfyUI-GGUF-{Guid.NewGuid():N}.zip");

        try
        {
            using (var resp = await Http.GetAsync(GgufNodeZipUrl,
                                                   HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(tempZip, FileMode.Create, FileAccess.Write,
                                                      FileShare.None, 81_920, useAsync: true);
                await src.CopyToAsync(dst, ct);
            }

            Log.Information("ComfyInstaller: ComfyUI-GGUF ZIP stažen ({Size} KB)",
                            new FileInfo(tempZip).Length / 1024);

            // ── 2) Rozbalíme do custom_nodes/ComfyUI-GGUF/ ──────────────────────
            // ZIP obsahuje root složku „ComfyUI-GGUF-main" — přesouváme obsah o úroveň níž
            progress?.Report(new(ComfyInstallStage.Extracting,
                "Rozbaluji ComfyUI-GGUF…", 50, 0, 0, 0, null));

            var targetDir = Path.Combine(customNodesDir, GgufNodeFolderName);

            // Pro idempotenci: pokud už cíl existuje (např. částečná předchozí instalace),
            // smažeme ho — chceme čistou instalaci aktuální verze
            if (Directory.Exists(targetDir))
                Directory.Delete(targetDir, recursive: true);

            using (var archive = ZipFile.OpenRead(tempZip))
            {
                // Najdeme prefix root složky (typicky „ComfyUI-GGUF-main/")
                var rootEntry = archive.Entries
                    .FirstOrDefault(e => e.FullName.EndsWith('/') &&
                                         e.FullName.Count(c => c == '/') == 1);
                var rootPrefix = rootEntry?.FullName ?? "";

                Directory.CreateDirectory(targetDir);

                foreach (var entry in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();

                    // Vynech root složku samotnou
                    if (entry.FullName == rootPrefix) continue;

                    // Strip prefix → path relative to repo root
                    var relativePath = entry.FullName.StartsWith(rootPrefix)
                        ? entry.FullName[rootPrefix.Length..]
                        : entry.FullName;
                    if (string.IsNullOrEmpty(relativePath)) continue;

                    var destPath = Path.Combine(targetDir, relativePath);

                    // Adresář
                    if (entry.FullName.EndsWith('/'))
                    {
                        Directory.CreateDirectory(destPath);
                        continue;
                    }

                    // Soubor
                    var destDirName = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(destDirName))
                        Directory.CreateDirectory(destDirName);

                    entry.ExtractToFile(destPath, overwrite: true);
                }
            }

            Log.Information("ComfyInstaller: ComfyUI-GGUF rozbaleno → {Dir}", targetDir);

            // ── 3) Doinstalujeme pip závislost gguf přes embedded Python ────────
            progress?.Report(new(ComfyInstallStage.Finishing,
                "Instaluji pip závislost gguf… (může chvíli trvat)", 80, 0, 0, 0, null));

            await RunPipInstallAsync(pythonExe, "gguf", ct);

            progress?.Report(new(ComfyInstallStage.Done,
                "ComfyUI-GGUF nainstalován", 100, 0, 0, 0, null));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); }
            catch (Exception ex) { Log.Warning(ex, "ComfyInstaller: nelze smazat {Path}", tempZip); }
        }
    }

    // ── ComfyUI-VideoHelperSuite custom node (VHS_VideoCombine → MP4) ──────────

    public bool IsVideoHelperSuiteInstalled(string comfyUiDir)
    {
        if (string.IsNullOrWhiteSpace(comfyUiDir)) return false;
        var nodePath = Path.Combine(comfyUiDir, "custom_nodes", VhsNodeFolderName);
        if (!Directory.Exists(nodePath)) return false;
        return File.Exists(Path.Combine(nodePath, "__init__.py"))
            || Directory.Exists(Path.Combine(nodePath, "videohelpersuite"));
    }

    public async Task EnsureVideoHelperSuiteInstalledAsync(
        string                            comfyUiDir,
        string                            pythonExe,
        IProgress<ComfyInstallProgress>?  progress = null,
        CancellationToken                 ct       = default)
    {
        if (IsVideoHelperSuiteInstalled(comfyUiDir))
        {
            Log.Information("ComfyInstaller: ComfyUI-VideoHelperSuite už nainstalovaný");
            return;
        }

        var customNodesDir = Path.Combine(comfyUiDir, "custom_nodes");
        Directory.CreateDirectory(customNodesDir);
        var targetDir = Path.Combine(customNodesDir, VhsNodeFolderName);

        await DownloadAndExtractGitHubZipAsync(
            VhsNodeZipUrl, targetDir, "ComfyUI-VideoHelperSuite", progress, ct);

        // requirements.txt (imageio-ffmpeg → ffmpeg pro MP4, opencv, …)
        var reqPath = Path.Combine(targetDir, "requirements.txt");
        if (File.Exists(reqPath))
        {
            progress?.Report(new(ComfyInstallStage.Finishing,
                "Instaluji závislosti VideoHelperSuite (vč. ffmpeg)…", 80, 0, 0, 0, null));
            await RunPipInstallAsync(pythonExe, $"-r \"{reqPath}\"", ct);
        }

        progress?.Report(new(ComfyInstallStage.Done,
            "ComfyUI-VideoHelperSuite nainstalován", 100, 0, 0, 0, null));
    }

    // ── FaceDetailer (Impact-Pack + Subpack + detektor) ──────────────────────

    public bool IsFaceDetailerInstalled(string comfyUiDir)
    {
        if (string.IsNullOrWhiteSpace(comfyUiDir)) return false;
        var pack  = Path.Combine(comfyUiDir, "custom_nodes", ImpactPackFolderName);
        var model = Path.Combine(comfyUiDir, "models", "ultralytics", "bbox", "face_yolov8m.pt");
        var packOk = Directory.Exists(pack)
                     && (File.Exists(Path.Combine(pack, "__init__.py")) || Directory.Exists(Path.Combine(pack, "modules")));
        return packOk && File.Exists(model);
    }

    public async Task EnsureFaceDetailerInstalledAsync(
        string                            comfyUiDir,
        string                            pythonExe,
        IProgress<ComfyInstallProgress>?  progress = null,
        CancellationToken                 ct       = default)
    {
        if (IsFaceDetailerInstalled(comfyUiDir))
        {
            Log.Information("ComfyInstaller: FaceDetailer (Impact-Pack) už nainstalovaný");
            return;
        }

        var customNodesDir = Path.Combine(comfyUiDir, "custom_nodes");
        Directory.CreateDirectory(customNodesDir);

        // Impact-Pack (FaceDetailer node)
        var packDir = Path.Combine(customNodesDir, ImpactPackFolderName);
        if (!Directory.Exists(Path.Combine(packDir, "modules")) && !File.Exists(Path.Combine(packDir, "__init__.py")))
            await DownloadAndExtractGitHubZipAsync(ImpactPackZipUrl, packDir, "ComfyUI-Impact-Pack", progress, ct);
        var packReq = Path.Combine(packDir, "requirements.txt");
        if (File.Exists(packReq))
        {
            progress?.Report(new(ComfyInstallStage.Finishing, "Instaluji závislosti Impact-Pack…", 60, 0, 0, 0, null));
            await RunPipInstallAsync(pythonExe, $"-r \"{packReq}\"", ct);
        }

        // Impact-Subpack (UltralyticsDetectorProvider)
        var subDir = Path.Combine(customNodesDir, ImpactSubpackFolderName);
        if (!Directory.Exists(Path.Combine(subDir, "modules")) && !File.Exists(Path.Combine(subDir, "__init__.py")))
            await DownloadAndExtractGitHubZipAsync(ImpactSubpackZipUrl, subDir, "ComfyUI-Impact-Subpack", progress, ct);
        var subReq = Path.Combine(subDir, "requirements.txt");
        if (File.Exists(subReq))
        {
            progress?.Report(new(ComfyInstallStage.Finishing, "Instaluji závislosti Impact-Subpack…", 80, 0, 0, 0, null));
            await RunPipInstallAsync(pythonExe, $"-r \"{subReq}\"", ct);
        }

        // Detekční model obličeje → models/ultralytics/bbox/
        var modelPath = Path.Combine(comfyUiDir, "models", "ultralytics", "bbox", "face_yolov8m.pt");
        if (!File.Exists(modelPath))
        {
            progress?.Report(new(ComfyInstallStage.Downloading, "Stahuji detekční model obličeje (~52 MB)…", 90, 0, 0, 0, null));
            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
            using var resp = await Http.GetAsync(FaceDetectorModelUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(modelPath, FileMode.Create, FileAccess.Write, FileShare.None, 81_920, useAsync: true);
            await src.CopyToAsync(dst, ct);
        }

        progress?.Report(new(ComfyInstallStage.Done, "FaceDetailer nainstalován", 100, 0, 0, 0, null));
    }

    // ── ComfyUI-Frame-Interpolation (RIFE VFI → plynulejší video) ─────────────

    public bool IsFrameInterpolationInstalled(string comfyUiDir)
    {
        if (string.IsNullOrWhiteSpace(comfyUiDir)) return false;
        var nodePath = Path.Combine(comfyUiDir, "custom_nodes", FrameInterpFolderName);
        if (!Directory.Exists(nodePath)) return false;
        return File.Exists(Path.Combine(nodePath, "__init__.py"))
            || Directory.Exists(Path.Combine(nodePath, "vfi_models"));
    }

    public async Task EnsureFrameInterpolationInstalledAsync(
        string                            comfyUiDir,
        string                            pythonExe,
        IProgress<ComfyInstallProgress>?  progress = null,
        CancellationToken                 ct       = default)
    {
        if (IsFrameInterpolationInstalled(comfyUiDir))
        {
            Log.Information("ComfyInstaller: ComfyUI-Frame-Interpolation už nainstalovaný");
            return;
        }

        var customNodesDir = Path.Combine(comfyUiDir, "custom_nodes");
        Directory.CreateDirectory(customNodesDir);
        var targetDir = Path.Combine(customNodesDir, FrameInterpFolderName);

        await DownloadAndExtractGitHubZipAsync(
            FrameInterpZipUrl, targetDir, "ComfyUI-Frame-Interpolation", progress, ct);

        // requirements.txt (cupy/opencv… — RIFE model se dotáhne až za běhu nodu).
        var reqPath = Path.Combine(targetDir, "requirements-no-cupy.txt");
        if (!File.Exists(reqPath)) reqPath = Path.Combine(targetDir, "requirements.txt");
        if (File.Exists(reqPath))
        {
            progress?.Report(new(ComfyInstallStage.Finishing,
                "Instaluji závislosti Frame-Interpolation (RIFE)…", 80, 0, 0, 0, null));
            await RunPipInstallAsync(pythonExe, $"-r \"{reqPath}\"", ct);
        }

        progress?.Report(new(ComfyInstallStage.Done,
            "ComfyUI-Frame-Interpolation nainstalován", 100, 0, 0, 0, null));
    }

    /// <summary>
    /// Stáhne ZIP GitHub repa (archive/refs/heads/main.zip) a rozbalí jeho obsah
    /// (bez root složky „repo-main/") do <paramref name="targetDir"/>. Idempotentní —
    /// existující cíl smaže. Sdílené pro custom nody.
    /// </summary>
    private static async Task DownloadAndExtractGitHubZipAsync(
        string zipUrl, string targetDir, string label,
        IProgress<ComfyInstallProgress>? progress, CancellationToken ct)
    {
        var tempZip = Path.Combine(Path.GetTempPath(), $"{label}-{Guid.NewGuid():N}.zip");
        try
        {
            progress?.Report(new(ComfyInstallStage.Downloading, $"Stahuji {label}…", 0, 0, 0, 0, null));
            using (var resp = await Http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(tempZip, FileMode.Create, FileAccess.Write,
                                                      FileShare.None, 81_920, useAsync: true);
                await src.CopyToAsync(dst, ct);
            }

            progress?.Report(new(ComfyInstallStage.Extracting, $"Rozbaluji {label}…", 50, 0, 0, 0, null));
            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true);
            Directory.CreateDirectory(targetDir);

            using var archive = ZipFile.OpenRead(tempZip);
            var rootEntry = archive.Entries
                .FirstOrDefault(e => e.FullName.EndsWith('/') && e.FullName.Count(c => c == '/') == 1);
            var rootPrefix = rootEntry?.FullName ?? "";

            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.FullName == rootPrefix) continue;

                var relativePath = entry.FullName.StartsWith(rootPrefix)
                    ? entry.FullName[rootPrefix.Length..]
                    : entry.FullName;
                if (string.IsNullOrEmpty(relativePath)) continue;

                var destPath = Path.Combine(targetDir, relativePath);
                if (entry.FullName.EndsWith('/'))
                {
                    Directory.CreateDirectory(destPath);
                    continue;
                }
                var destDirName = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDirName)) Directory.CreateDirectory(destDirName);
                entry.ExtractToFile(destPath, overwrite: true);
            }

            Log.Information("ComfyInstaller: {Label} rozbaleno → {Dir}", label, targetDir);
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); }
            catch (Exception ex) { Log.Warning(ex, "ComfyInstaller: nelze smazat {Path}", tempZip); }
        }
    }

    /// <summary>
    /// Spustí <c>python.exe -s -m pip install &lt;package&gt;</c> na embedded pythonu
    /// ComfyUI Portable. Stdout/stderr loguje pro diagnostiku.
    /// </summary>
    private static async Task RunPipInstallAsync(
        string            pythonExe,
        string            packageName,
        CancellationToken ct)
    {
        if (!File.Exists(pythonExe))
            throw new InvalidOperationException($"Python interpreter nenalezen: {pythonExe}");

        var psi = new ProcessStartInfo
        {
            FileName               = pythonExe,
            // -s = nečíst user site-packages (vyžadováno embedded buildem)
            // --no-warn-script-location = potlačí PATH varování
            Arguments              = $"-s -m pip install --no-warn-script-location {packageName}",
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };

        Log.Information("ComfyInstaller: pip install {Pkg}", packageName);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Nelze spustit pip");

        proc.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data)) Log.Information("[pip] {Line}", e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data)) Log.Warning("[pip] {Line}", e.Data);
        };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // Pip občas zatuhne na DNS / TLS handshake — 5 minut hard cap.
        var done = await Task.Run(() => proc.WaitForExit(5 * 60 * 1000), ct);
        if (!done)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException("pip install timeout (5 min)");
        }

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"pip install skončilo s kódem {proc.ExitCode} — viz log");
    }

    // ── DirectML pro AMD / Intel GPU ─────────────────────────────────────────

    public bool IsDirectMlInstalled(string pythonExe)
    {
        if (string.IsNullOrWhiteSpace(pythonExe) || !File.Exists(pythonExe))
            return false;

        // Embedded Python má site-packages v {pythonRoot}/Lib/site-packages.
        // torch_directml se instaluje jako modul torch_directml/__init__.py.
        var pythonDir = Path.GetDirectoryName(pythonExe);
        if (pythonDir is null) return false;

        var sitePackages = Path.Combine(pythonDir, "Lib", "site-packages");
        if (!Directory.Exists(sitePackages))
        {
            // Některé portable buildy mají site-packages v jiné cestě
            // (python_embeded/Lib/site-packages vs python_embeded/site-packages).
            sitePackages = Path.Combine(pythonDir, "site-packages");
            if (!Directory.Exists(sitePackages)) return false;
        }

        var moduleFile = Path.Combine(sitePackages, "torch_directml", "__init__.py");
        return File.Exists(moduleFile);
    }

    public async Task EnsureDirectMlInstalledAsync(
        string                            comfyUiDir,
        string                            pythonExe,
        IProgress<ComfyInstallProgress>?  progress = null,
        CancellationToken                 ct       = default)
    {
        if (IsDirectMlInstalled(pythonExe))
        {
            Log.Information("ComfyInstaller: torch-directml už nainstalován, přeskakuji");
            progress?.Report(new(ComfyInstallStage.Done, "DirectML je již nainstalován",
                                 100, 0, 0, 0, null));
            return;
        }

        if (!File.Exists(pythonExe))
            throw new InvalidOperationException($"Embedded Python nenalezen: {pythonExe}");

        progress?.Report(new(ComfyInstallStage.Finishing,
            "Instaluji DirectML pro AMD/Intel GPU (~700 MB stažení)…",
            0, 0, 0, 0, null));

        Log.Information("ComfyInstaller: zahajuji pip install torch-directml");

        // torch-directml je velký balíček (vytváří se z PyTorch wheel +
        // DirectML knihovny). 5 min timeout v RunPipInstallAsync to musí stačit.
        await RunPipInstallAsync(pythonExe, "torch-directml", ct);

        if (!IsDirectMlInstalled(pythonExe))
        {
            throw new InvalidOperationException(
                "torch-directml byl nainstalován, ale modul torch_directml " +
                "se nenašel v site-packages. Možná konflikt s existujícím torch.");
        }

        progress?.Report(new(ComfyInstallStage.Done,
            "DirectML nainstalován — ComfyUI poběží s --directml flagem",
            100, 0, 0, 0, null));
        Log.Information("ComfyInstaller: torch-directml úspěšně nainstalován");
    }
}
