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
    /// POZOR: tqdm dává postfix (avr_loss) DOVNITŘ hranatých závorek
    /// (<c>[02:14&lt;04:09, 3.91it/s, avr_loss=0.123]</c>), proto se loss
    /// zachytává před koncovým <c>]</c>, ne za ním.
    /// </summary>
    private static readonly Regex ProgressLineRegex = new(
        @"steps:\s+\d+%.*?\|\s*(\d+)/(\d+)\s*\[(.*?)(?:avr_loss=([0-9.eE+\-]+))?\]",
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

            var (exitCode, tailLog) = await RunTrainingProcessAsync(
                pythonExe, sdScriptsDir, script, args,
                request.Parameters.Steps, sw, progress, ct);

            if (exitCode != 0)
            {
                // Sběr posledních řádků poskytuje konkrétní diagnostický kontext —
                // bez něj byl uživatel slepý („detail v logu" se neukazoval, protože
                // Serilog mlčí na Debug level pro [sd-scripts] řádky).
                var tail = tailLog.Count > 0
                    ? "\n\nPosledních " + tailLog.Count + " řádků výstupu:\n" + string.Join('\n', tailLog)
                    : string.Empty;
                Log.Error("SdScriptsLoraTrainer: training subprocess skončilo s exit kódem {Code}.{Tail}",
                    exitCode, tail);
                return new LoraTrainingResult(false, null,
                    $"sd-scripts skončilo s exit kódem {exitCode}.{tail}",
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

            // Úklid dočasného working dir (kopie datasetu + mezi-checkpointy) —
            // jen po úspěchu. Při chybě ho necháme pro diagnostiku.
            TryCleanupWorkingDir(workingDir);
            workingDir = null;   // ať finally neuklidí podruhé

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
            // Po úspěchu se workingDir uklidil výše (a nastavil na null). Pokud sem
            // dorazí ne-null, znamená to chybu/cancel — dir NEcháme pro diagnostiku
            // (uživatel může chtít kouknout do logu / mezi-checkpointů).
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

        // Fallback trigger token pro fotky bez popisku: použijeme sanitizovaný
        // název LoRA. Bez něj by sd-scripts trénoval na prázdný caption — LoRA by
        // se „rozmazala" do bezpodmínečného výstupu místo konkrétního subjektu.
        // S tímto tokenem uživatel pak v Image Studiu napíše „nikol_vvv" a dostane
        // natrénovanou postavu. (Ručně napsaný / auto-caption popisek má přednost.)
        var fallbackToken = SanitizeForFolderName(r.Name);

        var i = 1;
        foreach (var item in r.Dataset)
        {
            var ext = Path.GetExtension(item.ImagePath);
            var name = $"{i:D4}{ext}";
            File.Copy(item.ImagePath, Path.Combine(datasetDir, name));

            // Trigger token MUSÍ být v KAŽDÉM captionu — jinak LoRA nemá konzistentní
            // spouštěcí slovo. Dřív byl token jen ve fallbacku (prázdný caption),
            // takže fotky s BLIP/ručním popiskem token neměly → natrénovaný subjekt
            // nešel spolehlivě vyvolat (uživatel by v promptu neměl co napsat).
            string caption;
            if (string.IsNullOrWhiteSpace(item.Caption))
            {
                caption = fallbackToken;
            }
            else
            {
                var userCaption = item.Caption.Trim();
                caption = userCaption.StartsWith(fallbackToken, StringComparison.OrdinalIgnoreCase)
                    ? userCaption
                    : $"{fallbackToken}, {userCaption}";
            }

            File.WriteAllText(
                Path.Combine(datasetDir, $"{Path.GetFileNameWithoutExtension(name)}.txt"),
                caption);
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

    /// <summary>Smaže dočasný working dir (best-effort). Nic nehodí — úklid není kritický.</summary>
    private static void TryCleanupWorkingDir(string? workingDir)
    {
        if (string.IsNullOrEmpty(workingDir) || !Directory.Exists(workingDir)) return;
        try
        {
            Directory.Delete(workingDir, recursive: true);
            Log.Debug("SdScriptsLoraTrainer: working dir uklizen {Dir}", workingDir);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SdScriptsLoraTrainer: úklid working dir {Dir} selhal", workingDir);
        }
    }

    // ── Sestavení Python příkazu ─────────────────────────────────────────────

    /// <summary>
    /// Vybere správný sd-scripts entrypoint podle base modelu a sestaví CLI
    /// argumenty. SDXL detection podle filename ("xl"/"sdxl") + filesize > 5 GB.
    ///
    /// <para>Skript se nespouští přímo, ale přes <see cref="SdScriptsLauncher"/>
    /// (<c>python _aistudio_launch.py sdxl_train_network.py …</c>), aby se vyřešil
    /// <c>ModuleNotFoundError: No module named 'library'</c> u ComfyUI embedded
    /// Pythonu — viz dokumentace launcheru.</para>
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
        var scriptPath  = Path.Combine(sdScriptsDir, script);
        var launcherPath = SdScriptsLauncher.EnsureLauncher(sdScriptsDir);

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

        // python _aistudio_launch.py "<cílový skript>" …flagy
        sb.Append('"').Append(launcherPath).Append('"');
        sb.Append(" \"").Append(scriptPath).Append('"');

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

        // Aspect-ratio bucketing — BEZ něj sd-scripts ořízne všechny fotky na
        // čtverec (resolution×resolution), takže portréty/na výšku přijdou o
        // hlavu/nohy a identita se zhorší. S bucketingem se obrázky seskupí dle
        // poměru stran a trénují v nativním tvaru. bucket_no_upscale = malé
        // obrázky se nenafukují (zachová ostrost).
        Arg   ("--enable_bucket");
        Arg   ("--bucket_no_upscale");
        Arg   ("--min_bucket_reso",               "256");
        Arg   ("--max_bucket_reso",               "2048");

        // Fixní seed — reprodukovatelný trénink (stejný dataset + parametry =
        // stejný výsledek). Bez něj je každý běh trochu jiný.
        Arg   ("--seed",                          "42");

        // --sdpa = PyTorch 2.x scaled dot-product attention (memory-efficient).
        // Vestavěné v torch, NEvyžaduje xformers balík (ten není v našich pip
        // dependencích a jeho build na Windows embedded Pythonu je nespolehlivý).
        Arg   ("--sdpa");

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

    /// <summary>Maximální počet stdout řádků v ring bufferu pro diagnostiku.</summary>
    private const int TailBufferSize = 50;

    /// <summary>
    /// Klíčová slova v stdout/stderr, která eskalují řádek z Debug na Warning level.
    /// sd-scripts používá Python logging i print statementy — chyby se hází jako
    /// Tracebacks nebo Errors přes oba kanály.
    /// </summary>
    private static readonly string[] ErrorKeywords =
    {
        "error", "traceback", "exception", "failed", "cuda out of memory",
        "runtimeerror", "valueerror", "filenotfounderror", "importerror",
        "no module named", "assertionerror",
    };

    /// <summary>
    /// Spustí Python subprocess přes <see cref="ProcessRunner"/>, parsuje progress
    /// a eskaluje chybové řádky na Warning level. Tail buffer + UTF-8 + cancellation
    /// řeší ProcessRunner. Vrací (exitCode, tailLines).
    /// </summary>
    private async Task<(int ExitCode, IReadOnlyList<string> Tail)> RunTrainingProcessAsync(
        string                            pythonExe,
        string                            sdScriptsDir,
        string                            scriptPath,
        string                            args,
        int                               totalSteps,
        Stopwatch                         sw,
        IProgress<LoraTrainingProgress>?  progress,
        CancellationToken                 ct)
    {
        void HandleLine(string line)
        {
            OutputLog?.Invoke(line);

            // Eskalace logging level pro chybové řádky — bez toho byl Debug výpis
            // tichý při Serilog Information minimum a uživatel viděl jen „exit code N".
            var lower = line.ToLowerInvariant();
            if (ErrorKeywords.Any(k => lower.Contains(k)))
                Log.Warning("[sd-scripts] {Line}", line);
            else
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

        var result = await ProcessRunner.RunAsync(
            new ProcessRunOptions
            {
                FileName         = pythonExe,
                Arguments        = args,
                WorkingDirectory = sdScriptsDir,
                TailSize         = TailBufferSize,
            },
            onLine: HandleLine,
            ct:     ct);

        return (result.ExitCode, result.TailLines);
    }
}
