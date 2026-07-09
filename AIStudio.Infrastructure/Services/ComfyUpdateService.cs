using System.Diagnostics;
using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Implementace <see cref="IComfyUpdateService"/> — řízená aktualizace ComfyUI na ověřenou
/// verzi přes git. Záměrně <b>nejde na bleeding edge</b>: přepne na konkrétní tag
/// (<c>ComfyVersion.TestedVersion</c>), takže výsledek je zaručeně kompatibilní s AI Studiem.
///
/// <para>Defenzivní: každý krok je obalený, chyby se vrací jako text. Nepřepisuje násilně
/// (žádné <c>checkout -f</c>) — když má uživatel rozdělanou práci, raději ohlásí chybu než
/// aby o ni přišel.</para>
/// </summary>
public sealed class ComfyUpdateService : IComfyUpdateService
{
    private readonly IComfyService    _comfy;
    private readonly ISettingsService _settings;
    private readonly IComfyInstaller? _installer;

    public ComfyUpdateService(IComfyService comfy, ISettingsService settings, IComfyInstaller? installer = null)
    {
        _comfy     = comfy;
        _settings  = settings;
        _installer = installer;
    }

    public bool IsGitRepo(string? comfyUiDir) =>
        !string.IsNullOrWhiteSpace(comfyUiDir) && Directory.Exists(Path.Combine(comfyUiDir, ".git"));

    public bool IsGitAvailable() => ResolveExe(OperatingSystem.IsWindows() ? "git.exe" : "git") is not null;

    public bool IsPortableInstall(string? comfyUiDir) =>
        _installer is not null &&
        !string.IsNullOrWhiteSpace(comfyUiDir) &&
        File.Exists(Path.Combine(comfyUiDir, "main.py")) &&
        !IsGitRepo(comfyUiDir);

    public async Task<ComfyUpdateResult> UpdatePortableToLatestAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var dir = _settings.Settings.ComfyUiDirectory;
        if (string.IsNullOrWhiteSpace(dir) || !File.Exists(Path.Combine(dir, "main.py")))
            return new(false, "ComfyUI není nainstalované (chybí main.py).");
        if (_installer is null)
            return new(false, "Aktualizace portable instalace není na této platformě dostupná.");

        // Standardní portable layout: {installDir}/ComfyUI_windows_portable/ComfyUI → installDir je o 2 výš.
        var installDir = Path.GetFullPath(Path.Combine(dir, "..", ".."));
        if (_installer.DetectExisting(installDir) is null)
            return new(false,
                "ComfyUI je na neobvyklém umístění — automatická aktualizace portable nejde. " +
                "Stáhni nejnovější ComfyUI Portable ručně a rozbal ho přes stávající složku.");

        // 1) Zastav běžící ComfyUI (jinak by extrakce narazila na zamčené soubory).
        progress?.Report("Zastavuji ComfyUI…");
        try { await _comfy.StopAsync(); } catch (Exception ex) { Log.Warning(ex, "ComfyUpdate(portable): StopAsync selhal (pokračuji)"); }

        // 2) Stáhni + rozbal nejnovější portable přes stávající (merge zachová modely/custom_nodes).
        var instProgress = new Progress<ComfyInstallProgress>(p => progress?.Report(p.Message));
        (string ComfyUiDir, string PythonPath) updated;
        try
        {
            updated = await _installer.UpdateToLatestAsync(installDir, instProgress, ct);
        }
        catch (OperationCanceledException) { return new(false, "Aktualizace zrušena."); }
        catch (Exception ex)
        {
            Log.Error(ex, "ComfyUpdate(portable): re-extrakce selhala");
            return new(false, "Aktualizace selhala: " + Short(ex.Message));
        }

        // 3) Ulož nové cesty. KRITICKÉ: nový archiv může mít jiný název kořenové složky
        //    (např. ComfyUI_windows_portable → ..._nvidia_cu126) — bez přepsání settings by
        //    appka dál mířila na starou instalaci a „aktualizace by nic neudělala".
        if (!string.Equals(updated.ComfyUiDir, dir, StringComparison.OrdinalIgnoreCase))
        {
            Log.Information("ComfyUpdate(portable): nová instalace na jiné cestě — {Old} → {New}",
                dir, updated.ComfyUiDir);
            progress?.Report("Nová verze má jinou složku — přenes si prosím modely/custom_nodes ze staré instalace.");
        }
        _settings.Settings.ComfyUiDirectory = updated.ComfyUiDir;
        _settings.Settings.PythonPath       = updated.PythonPath;
        try { await _settings.SaveAsync(); }
        catch (Exception ex) { Log.Warning(ex, "ComfyUpdate(portable): uložení settings selhalo"); }

        progress?.Report("Hotovo.");
        return new(true, "ComfyUI aktualizováno na nejnovější verzi. Spusť ho znovu.");
    }

    public async Task<ComfyUpdateResult> UpdateToVersionAsync(
        string targetVersion, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var dir = _settings.Settings.ComfyUiDirectory;
        if (string.IsNullOrWhiteSpace(dir) || !File.Exists(Path.Combine(dir, "main.py")))
            return new(false, "ComfyUI není nainstalované (chybí main.py).");
        if (!IsGitRepo(dir))
            return new(false, "Tahle instalace ComfyUI není git repozitář — automatická aktualizace přes git není možná.");

        var git = ResolveExe(OperatingSystem.IsWindows() ? "git.exe" : "git");
        if (git is null)
            return new(false, "V systému není 'git' — nainstaluj Git (git-scm.com) a zkus znovu.");

        // 1) Zastav běžící ComfyUI (jinak by git držel zamčené soubory / proces by běžel na staré verzi).
        progress?.Report("Zastavuji ComfyUI…");
        try { await _comfy.StopAsync(); } catch (Exception ex) { Log.Warning(ex, "ComfyUpdate: StopAsync selhal (pokračuji)"); }

        // 2) Stáhni tagy.
        progress?.Report("Stahuji seznam verzí (git fetch)…");
        var fetch = await RunAsync(git, ["fetch", "--tags", "--force"], dir, TimeSpan.FromMinutes(3), ct);
        if (!fetch.Ok)
            return new(false, "git fetch selhal: " + Short(fetch.Error));

        // 3) Přepni na ověřenou verzi — zkus vX.Y.Z, pak X.Y.Z, pak tags/vX.Y.Z.
        progress?.Report($"Přepínám na ověřenou verzi {targetVersion}…");
        string[] candidates = [$"v{targetVersion}", targetVersion, $"tags/v{targetVersion}"];
        (bool Ok, string Error)? checkout = null;
        foreach (var refName in candidates)
        {
            var r = await RunAsync(git, ["checkout", refName], dir, TimeSpan.FromMinutes(2), ct);
            checkout = r;
            if (r.Ok) break;
        }
        if (checkout is null || !checkout.Value.Ok)
            return new(false,
                $"Nepodařilo se přepnout na verzi {targetVersion}. Možná máš v ComfyUI rozdělané změny. " +
                "Detail: " + Short(checkout?.Error ?? ""));

        // 4) Reinstaluj requirements (nové/změněné závislosti). Non-fatal — když selže, ComfyUI
        //    většinou nastartuje, jen případně bez nové knihovny.
        var python = ResolvePython(dir);
        if (python is not null)
        {
            var req = Path.Combine(dir, "requirements.txt");
            if (File.Exists(req))
            {
                progress?.Report("Reinstaluji závislosti (pip)… může chvíli trvat");
                var pip = await RunAsync(python, ["-m", "pip", "install", "-r", req], dir, TimeSpan.FromMinutes(10), ct);
                if (!pip.Ok)
                    Log.Warning("ComfyUpdate: pip install selhal (non-fatal): {Err}", Short(pip.Error));
            }
        }

        progress?.Report("Hotovo.");
        return new(true, $"ComfyUI sladěno na ověřenou verzi {targetVersion}. Spusť ho znovu.");
    }

    // ── Pomocné ────────────────────────────────────────────────────────────────

    private string? ResolvePython(string comfyUiDir)
    {
        var fromSettings = _settings.Settings.PythonPath;
        if (!string.IsNullOrWhiteSpace(fromSettings) && File.Exists(fromSettings)) return fromSettings;

        // Embedded python u Windows portable.
        var embedded = Path.Combine(comfyUiDir, "..", "python_embeded", "python.exe");
        if (File.Exists(embedded)) return Path.GetFullPath(embedded);

        return ResolveExe(OperatingSystem.IsWindows() ? "python.exe" : "python3");
    }

    private static string? ResolveExe(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(full)) return full;
            }
            catch { /* neplatný PATH segment */ }
        }
        return null;
    }

    private static async Task<(bool Ok, string Error)> RunAsync(
        string exe, string[] args, string workingDir, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = exe,
                WorkingDirectory       = workingDir,
                RedirectStandardError  = true,
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) return (false, "proces se nepodařilo spustit");

            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try { await proc.WaitForExitAsync(timeoutCts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (false, $"timeout po {timeout.TotalMinutes:0} min");
            }

            var stderr = await stderrTask;
            var stdout = await stdoutTask;
            return proc.ExitCode == 0
                ? (true, string.Empty)
                : (false, string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string Short(string s) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= 300 ? s.Trim() : s[..300].Trim() + "…");
}
