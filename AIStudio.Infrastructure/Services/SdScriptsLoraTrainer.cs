using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Core.Services;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Spustí <c>sd-scripts</c> Python skript (<c>sdxl_train_network.py</c> /
/// <c>train_network.py</c> / <c>flux_train_network.py</c>) jako subprocess
/// a parsuje stdout pro průběh tréninku.
///
/// <para>Závislosti zajišťuje <see cref="ILoraTrainerDependencyService"/> —
/// před každým <see cref="TrainAsync"/> ji zavoláme přes
/// <see cref="ILoraTrainerDependencyService.EnsureAllAsync"/>.</para>
///
/// <para>sd-scripts stdout přibližný formát (v0.8.x):
/// <code>
/// steps:  35%|████      | 525/1500 [02:14&lt;04:09, 3.91it/s, avr_loss=0.123]
/// </code>
/// Regex extrahuje aktuální step + loss. ETA si počítáme sami, ne ze stdoutu —
/// sd-scripts ETA bývá zkreslená výpadky disku / cache flushů.</para>
/// </summary>
public sealed class SdScriptsLoraTrainer : ILoraTrainerService
{
    private readonly ILoraTrainerDependencyService _deps;
    private readonly Func<string?> _resolvePythonExe;

    /// <summary>Kde držíme dočasné dataset working dirs (před úklidem).</summary>
    private static readonly string WorkingDirRoot =
        Path.Combine(AppPaths.AppDataRoot, "lora-training");

    /// <summary>
    /// Regex pro progress řádek z sd-scripts tqdm baru.
    /// Matchuje <c>525/1500</c> a optional <c>avr_loss=0.123</c>.
    /// </summary>
    private static readonly Regex ProgressLineRegex = new(
        @"steps:\s+\d+%.*?\|\s*(\d+)/(\d+)\s*\[(.*?)\](?:.*?avr_loss=([0-9.eE+\-]+))?",
        RegexOptions.Compiled);

    /// <summary>
    /// Pokusí se z jednoho řádku stdoutu vyparsovat progress: aktuální step,
    /// celkový počet stepů a optional loss. Vrátí <c>false</c> pokud řádek
    /// nematchuje (= je to obyčejný log line, ne progress bar).
    /// Internal pro unit testy.
    /// </summary>
    internal static bool TryParseProgressLine(
        string line, out int step, out int total, out double? loss)
    {
        step  = 0;
        total = 0;
        loss  = null;

        if (string.IsNullOrEmpty(line)) return false;
        var match = ProgressLineRegex.Match(line);
        if (!match.Success) return false;

        if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer,
                          CultureInfo.InvariantCulture, out step)) return false;
        if (!int.TryParse(match.Groups[2].Value, NumberStyles.Integer,
                          CultureInfo.InvariantCulture, out total)) total = 0;

        if (match.Groups[4].Success &&
            double.TryParse(match.Groups[4].Value, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out var l))
            loss = l;

        return true;
    }

    public bool IsTraining { get; private set; }
    public event Action<string>? OutputLog;

    /// <summary>
    /// Konstruktor — <paramref name="resolvePythonExe"/> je callback typicky
    /// implementovaný jako <c>() =&gt; comfyInstaller.DetectExisting(...).PythonPath</c>.
    /// Důvod factory: ComfyUI cesta se může změnit za běhu (reinstalace) a
    /// nechceme cache.
    /// </summary>
    public SdScriptsLoraTrainer(
        ILoraTrainerDependencyService deps,
        Func<string?>                 resolvePythonExe)
    {
        _deps             = deps;
        _resolvePythonExe = resolvePythonExe;
    }

    public async Task<LoraTrainingResult> TrainAsync(
        LoraTrainingRequest               request,
        IProgress<LoraTrainingProgress>?  progress = null,
        CancellationToken                 ct       = default)
    {
        if (IsTraining)
            return new LoraTrainingResult(false, null, "Trénink už běží.", TimeSpan.Zero);

        var validation = ValidateRequest(request);
        if (validation is not null)
            return new LoraTrainingResult(false, null, validation, TimeSpan.Zero);

        var pythonExe = _resolvePythonExe();
        if (string.IsNullOrEmpty(pythonExe) || !File.Exists(pythonExe))
            return new LoraTrainingResult(false, null,
                "Python interpreter z ComfyUI nebyl nalezen. Nejprve dokonči ComfyUI instalaci v Nastavení.",
                TimeSpan.Zero);

        IsTraining = true;
        var sw = Stopwatch.StartNew();
        string? workingDir = null;

        try
        {
            // ── 1) Zajistit závislosti ────────────────────────────────────────
            progress?.Report(new LoraTrainingProgress(0, request.Parameters.Steps, null,
                sw.Elapsed, null, "Kontroluji pip balíky a sd-scripts…"));
            await _deps.EnsureAllAsync(pythonExe, null, ct);

            var sdScriptsDir = _deps.SdScriptsDirectory
                ?? throw new InvalidOperationException("sd-scripts dir je null i po EnsureAll");

            // ── 2) Dataset working dir ────────────────────────────────────────
            progress?.Report(new LoraTrainingProgress(0, request.Parameters.Steps, null,
                sw.Elapsed, null, "Připravuji dataset…"));
            workingDir = PrepareDatasetWorkingDir(request);

            // ── 3) Sestavit příkaz a spustit ──────────────────────────────────
            var (script, args) = BuildCommand(request, workingDir, sdScriptsDir);
            Log.Information("SdScriptsLoraTrainer: spouštím {Script} args~{ArgsLen} chars", script, args.Length);

            progress?.Report(new LoraTrainingProgress(0, request.Parameters.Steps, null,
                sw.Elapsed, null, "Načítám checkpoint a inicializuji…"));

            var exitCode = await RunTrainingProcessAsync(
                pythonExe, sdScriptsDir, script, args,
                request.Parameters.Steps, sw, progress, ct);

            if (exitCode != 0)
            {
                return new LoraTrainingResult(false, null,
                    $"sd-scripts skončilo s exit kódem {exitCode}. Detail v logu.",
                    sw.Elapsed);
            }

            // ── 4) Najít vygenerovaný LoRA soubor a přesunout do output dir ──
            var producedFile = Path.Combine(workingDir, "output", $"{request.Name}.safetensors");
            if (!File.Exists(producedFile))
            {
                Log.Warning("SdScriptsLoraTrainer: očekávaný výstup {Path} neexistuje", producedFile);
                return new LoraTrainingResult(false, null,
                    "Trénink doběhl, ale výstupní LoRA soubor nebyl nalezen.",
                    sw.Elapsed);
            }

            Directory.CreateDirectory(request.OutputDirectory);
            var finalPath = Path.Combine(request.OutputDirectory, $"{request.Name}.safetensors");

            // Pokud cíl existuje (uživatel trénuje podruhé se stejným názvem),
            // přepíšeme — chování konzistentní s downloads
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(producedFile, finalPath);

            Log.Information("SdScriptsLoraTrainer: hotovo — LoRA uložena do {Path}", finalPath);
            progress?.Report(new LoraTrainingProgress(
                request.Parameters.Steps, request.Parameters.Steps, null,
                sw.Elapsed, TimeSpan.Zero, "Hotovo"));

            return new LoraTrainingResult(true, finalPath, null, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            return new LoraTrainingResult(false, null, "Trénink přerušen uživatelem.", sw.Elapsed);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SdScriptsLoraTrainer: trénink selhal");
            return new LoraTrainingResult(false, null, ex.Message, sw.Elapsed);
        }
        finally
        {
            IsTraining = false;
            // Working dir s mezi-checkpointy nesmazat — uživatel může chtít
            // pokračovat / debugnout. Vyčistíme jen pokud uspěl.
            // (V další iteraci přidáme „Uklidit dočasné soubory" v UI.)
        }
    }

    // ── Validace ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Vrátí <c>null</c> pokud je request validní, jinak chybovou hlášku
    /// (kterou caller dá do <see cref="LoraTrainingResult.ErrorMessage"/>).
    /// Internal pro unit testy přes InternalsVisibleTo.
    /// </summary>
    internal static string? ValidateRequest(LoraTrainingRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Name))
            return "Název LoRA nesmí být prázdný.";
        if (r.Name.Any(c => Path.GetInvalidFileNameChars().Contains(c)))
            return "Název obsahuje znaky, které nejsou dovolené v názvech souborů.";
        if (string.IsNullOrWhiteSpace(r.BaseModelPath) || !File.Exists(r.BaseModelPath))
            return $"Základní model neexistuje: {r.BaseModelPath}";
        if (r.Dataset.Count < 5)
            return $"Příliš málo trénovacích obrázků ({r.Dataset.Count}). Doporučeno 15-30.";
        if (r.Dataset.Count > 200)
            return $"Příliš mnoho obrázků ({r.Dataset.Count}). Doporučený limit je 100, jinak trénink trvá hodiny.";
        foreach (var img in r.Dataset)
        {
            if (!File.Exists(img.ImagePath))
                return $"Trénovací obrázek neexistuje: {img.ImagePath}";
        }
        if (string.IsNullOrWhiteSpace(r.OutputDirectory))
            return "OutputDirectory nesmí být prázdná.";
        return null;
    }

    // ── Dataset příprava ──────────────────────────────────────────────────────

    /// <summary>
    /// Vytvoří working dir struktury, kterou sd-scripts očekává:
    /// <code>
    /// {workingDir}/
    /// ├── img/
    /// │   └── 10_nikol/             ← "{repeats}_{instance_token}"
    /// │       ├── 001.jpg
    /// │       ├── 001.txt           ← caption
    /// │       └── …
    /// └── output/                   ← sem se uloží finální LoRA
    /// </code>
    /// </summary>
    private static string PrepareDatasetWorkingDir(LoraTrainingRequest r)
    {
        Directory.CreateDirectory(WorkingDirRoot);
        var workingDir = Path.Combine(WorkingDirRoot, $"{r.Name}_{DateTime.Now:yyyyMMdd_HHmmss}");

        var imgRoot = Path.Combine(workingDir, "img");
        var outRoot = Path.Combine(workingDir, "output");
        Directory.CreateDirectory(imgRoot);
        Directory.CreateDirectory(outRoot);

        // „repeats" je kolikrát se každý obrázek za epoch opakuje.
        // 10 je rozumný default pro 15-30 fotek. Pro 50+ fotek 5.
        var repeats = r.Dataset.Count <= 30 ? 10 : 5;
        var instanceFolder = $"{repeats}_{SanitizeForFolderName(r.Name)}";
        var datasetDir = Path.Combine(imgRoot, instanceFolder);
        Directory.CreateDirectory(datasetDir);

        var i = 1;
        foreach (var item in r.Dataset)
        {
            var ext = Path.GetExtension(item.ImagePath);
            var name = $"{i:D4}{ext}";
            File.Copy(item.ImagePath, Path.Combine(datasetDir, name));
            File.WriteAllText(
                Path.Combine(datasetDir, $"{Path.GetFileNameWithoutExtension(name)}.txt"),
                item.Caption ?? string.Empty);
            i++;
        }

        return workingDir;
    }

    private static string SanitizeForFolderName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        return sb.ToString().ToLowerInvariant();
    }

    // ── Sestavení Python příkazu ─────────────────────────────────────────────

    /// <summary>
    /// Vybere správný sd-scripts entrypoint podle base modelu a sestaví CLI
    /// argumenty. SDXL detection podle filename ("xl"/"sdxl") + filesize > 5 GB.
    /// </summary>
    private static (string Script, string Args) BuildCommand(
        LoraTrainingRequest r, string workingDir, string sdScriptsDir)
    {
        var baseName = Path.GetFileName(r.BaseModelPath).ToLowerInvariant();
        var isFlux   = baseName.Contains("flux");
        var isSdxl   = !isFlux && (baseName.Contains("xl") || baseName.Contains("sdxl"));

        var script = isFlux  ? "flux_train_network.py"
                   : isSdxl  ? "sdxl_train_network.py"
                             : "train_network.py";
        var scriptPath = Path.Combine(sdScriptsDir, script);

        var imgRoot = Path.Combine(workingDir, "img");
        var outRoot = Path.Combine(workingDir, "output");
        var p       = r.Parameters;

        var sb = new StringBuilder();
        void Arg(string flag, string? value = null)
        {
            sb.Append(' ').Append(flag);
            if (value is not null) sb.Append(' ').Append(value);
        }
        void Quoted(string flag, string path)
        {
            sb.Append(' ').Append(flag).Append(" \"").Append(path).Append('"');
        }

        sb.Append('"').Append(scriptPath).Append('"');

        Quoted("--pretrained_model_name_or_path", r.BaseModelPath);
        Quoted("--train_data_dir",                imgRoot);
        Quoted("--output_dir",                    outRoot);
        Arg   ("--output_name",                   r.Name);
        Arg   ("--save_model_as",                 "safetensors");
        Arg   ("--network_module",                "networks.lora");
        Arg   ("--network_dim",                   p.Rank.ToString(CultureInfo.InvariantCulture));
        Arg   ("--network_alpha",                 p.Alpha.ToString(CultureInfo.InvariantCulture));
        Arg   ("--resolution",                    $"{p.Resolution},{p.Resolution}");
        Arg   ("--train_batch_size",              p.BatchSize.ToString(CultureInfo.InvariantCulture));
        Arg   ("--max_train_steps",               p.Steps.ToString(CultureInfo.InvariantCulture));
        Arg   ("--learning_rate",                 p.LearningRate.ToString("0.######", CultureInfo.InvariantCulture));
        Arg   ("--lr_scheduler",                  "cosine_with_restarts");
        Arg   ("--optimizer_type",                p.Optimizer);
        Arg   ("--save_precision",                "fp16");
        Arg   ("--mixed_precision",               p.MixedPrecisionFp16 ? "fp16" : "no");
        Arg   ("--cache_latents");
        Arg   ("--xformers");

        if (p.GradientCheckpointing) Arg("--gradient_checkpointing");
        if (p.SaveEverySteps > 0)
            Arg("--save_every_n_steps", p.SaveEverySteps.ToString(CultureInfo.InvariantCulture));

        // SDXL/FLUX specifické flagy
        if (isSdxl)
        {
            Arg("--no_half_vae");          // SDXL VAE má známé NaN problémy v fp16 — fp32 je stabilnější
        }

        return (scriptPath, sb.ToString());
    }

    // ── Subprocess + progress parsing ─────────────────────────────────────────

    private async Task<int> RunTrainingProcessAsync(
        string                            pythonExe,
        string                            sdScriptsDir,
        string                            scriptPath,
        string                            args,
        int                               totalSteps,
        Stopwatch                         sw,
        IProgress<LoraTrainingProgress>?  progress,
        CancellationToken                 ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = pythonExe,
            Arguments              = args,
            WorkingDirectory       = sdScriptsDir,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        // PYTHONUNBUFFERED=1 → tqdm progress flushuje real-time, jinak by se output
        // zobrazoval jen po dokončení (buffered).
        psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";

        using var p = new Process { StartInfo = psi };

        void HandleLine(string? line)
        {
            if (string.IsNullOrEmpty(line)) return;

            OutputLog?.Invoke(line);
            Log.Debug("[sd-scripts] {Line}", line);

            if (!TryParseProgressLine(line, out var step, out var total, out var loss)) return;
            if (total <= 0) total = totalSteps;

            var elapsed = sw.Elapsed;
            TimeSpan? eta = null;
            if (step > 0 && total > step)
            {
                var secondsPerStep = elapsed.TotalSeconds / step;
                eta = TimeSpan.FromSeconds(secondsPerStep * (total - step));
            }

            progress?.Report(new LoraTrainingProgress(
                step, total, loss, elapsed, eta,
                $"Krok {step} / {total}" + (loss.HasValue ? $" (loss {loss:F4})" : "")));
        }

        p.OutputDataReceived += (_, e) => HandleLine(e.Data);
        // sd-scripts tqdm bary chodí typicky do stderr — taky parsujeme
        p.ErrorDataReceived  += (_, e) => HandleLine(e.Data);

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        return p.ExitCode;
    }
}
