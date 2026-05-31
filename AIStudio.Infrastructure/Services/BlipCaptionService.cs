using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Serilog;
using AIStudio.Core.Interfaces;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Default implementace <see cref="ILoraCaptionService"/> — generuje popisky přes
/// HuggingFace <c>transformers</c> BLIP model spuštěný v ComfyUI Python venv.
///
/// <para><b>Proč ne sd-scripts make_captions.py:</b> ten vyžaduje stažení BLIP
/// checkpointu z (často mrtvé) Salesforce URL a bundled BLIP modul s extra deps
/// (timm, fairscale). Místo toho používáme vlastní inline Python skript nad
/// <c>transformers.BlipForConditionalGeneration</c> — model
/// <c>Salesforce/blip-image-captioning-large</c> se auto-stáhne z HuggingFace
/// hubu (~1,9 GB při prvním běhu, pak cache). <c>transformers</c>, <c>torch</c>
/// i <c>PIL</c> jsou v ComfyUI venv vždy dostupné.</para>
///
/// <para>Skript píše každý caption jako <c>.txt</c> sidecar do working dir a
/// zároveň progress řádky <c>CAPTION i/n :: text</c> na stdout (parsujeme pro UI).</para>
/// </summary>
public sealed class BlipCaptionService : ILoraCaptionService
{
    private readonly ILoraTrainerDependencyService _deps;
    private readonly Func<string?> _resolvePythonExe;

    /// <summary>Dočasná pracovní složka pro batch captioning.</summary>
    private static readonly string WorkingDirRoot =
        Path.Combine(AIStudio.Core.Services.AppPaths.AppDataRoot, "captioning");

    /// <summary>Regex pro progress řádek <c>CAPTION 3/22 :: a woman on a beach</c>.</summary>
    private static readonly Regex CaptionLineRegex = new(
        @"^CAPTION\s+(\d+)/(\d+)\s+::\s+(.*)$", RegexOptions.Compiled);

    public bool IsCaptioning { get; private set; }

    public BlipCaptionService(
        ILoraTrainerDependencyService deps,
        Func<string?>                 resolvePythonExe)
    {
        _deps             = deps;
        _resolvePythonExe = resolvePythonExe;
    }

    public async Task<IReadOnlyDictionary<string, string>> CaptionAsync(
        IReadOnlyList<string>       imagePaths,
        string                      style    = "blip",
        IProgress<CaptionProgress>? progress = null,
        CancellationToken           ct       = default)
    {
        if (IsCaptioning)
            throw new InvalidOperationException("Auto-captioning už běží.");

        if (imagePaths.Count == 0) return new Dictionary<string, string>();

        var pythonExe = _resolvePythonExe()
            ?? throw new InvalidOperationException(
                "Python interpreter z ComfyUI nebyl nalezen — dokonči ComfyUI instalaci.");

        IsCaptioning = true;
        string? workDir = null;

        try
        {
            // 1) Nakopírovat obrázky do work dir s deterministickými názvy
            Directory.CreateDirectory(WorkingDirRoot);
            workDir = Path.Combine(WorkingDirRoot, $"batch_{DateTime.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(workDir);

            var nameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < imagePaths.Count; i++)
            {
                var src = imagePaths[i];
                if (!File.Exists(src)) continue;
                var ext  = Path.GetExtension(src);
                var stub = $"{i:D4}";
                File.Copy(src, Path.Combine(workDir, $"{stub}{ext}"));
                nameMap[stub] = src;
            }

            progress?.Report(new CaptionProgress(0, imagePaths.Count, string.Empty,
                "Načítám BLIP model (první běh stahuje ~1,9 GB)…"));

            // 2) Napsat inline Python skript a spustit
            var scriptPath = WriteCaptionScript(workDir);
            var tail = await RunPythonAsync(pythonExe, scriptPath, workDir, imagePaths.Count, progress, ct);

            // 3) Načíst captions z .txt sidecarů
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var txtFile in Directory.EnumerateFiles(workDir, "*.txt"))
            {
                ct.ThrowIfCancellationRequested();
                var stub = Path.GetFileNameWithoutExtension(txtFile);
                if (!nameMap.TryGetValue(stub, out var originalPath)) continue;

                var caption = (await File.ReadAllTextAsync(txtFile, ct)).Trim();
                if (!string.IsNullOrWhiteSpace(caption))
                    result[originalPath] = caption;
            }

            if (result.Count == 0)
            {
                // Skript doběhl ale nic nevyrobil — pošleme tail výstupu, ať uživatel
                // (a my v logu) vidíme proč. Bez tohoto „chvilku něco dělal a nic".
                var tailText = tail.Count > 0
                    ? "\nVýstup skriptu:\n" + string.Join('\n', tail)
                    : string.Empty;
                Log.Warning("BlipCaptionService: 0 popisků vygenerováno.{Tail}", tailText);
                throw new InvalidOperationException(
                    "Captioning doběhl, ale nevygeneroval žádný popisek." + tailText);
            }

            Log.Information("BlipCaptionService: vygenerováno {Done}/{Total} popisků",
                result.Count, imagePaths.Count);
            return result;
        }
        finally
        {
            IsCaptioning = false;
            if (workDir is not null && Directory.Exists(workDir))
            {
                try { Directory.Delete(workDir, recursive: true); }
                catch (Exception ex) { Log.Warning(ex, "BlipCaptionService: úklid {Dir} selhal", workDir); }
            }
        }
    }

    // ── Inline Python skript ────────────────────────────────────────────────────

    /// <summary>
    /// Zapíše self-contained BLIP captioning skript do working dir. Skript:
    /// načte BLIP-large přes transformers, projde všechny obrázky ve složce,
    /// pro každý vygeneruje caption, zapíše <c>.txt</c> sidecar a vytiskne
    /// <c>CAPTION i/n :: text</c> na stdout pro progress.
    /// </summary>
    private static string WriteCaptionScript(string workDir)
    {
        // Cesty v Pythonu: použijeme raw string + forward slashes (Windows je bere taky)
        var workDirPy = workDir.Replace('\\', '/');

        var script = $$"""
            import os, sys, glob
            try:
                import torch
                from PIL import Image
                from transformers import BlipProcessor, BlipForConditionalGeneration
            except Exception as e:
                print("IMPORT_ERROR: " + repr(e), flush=True)
                sys.exit(3)

            WORK = r"{{workDirPy}}"
            EXTS = (".png", ".jpg", ".jpeg", ".webp", ".bmp")

            files = sorted([f for f in os.listdir(WORK)
                            if os.path.splitext(f)[1].lower() in EXTS])
            if not files:
                print("NO_IMAGES", flush=True)
                sys.exit(0)

            device = "cuda" if torch.cuda.is_available() else "cpu"
            print(f"LOADING blip-image-captioning-large on {device}", flush=True)

            model_id = "Salesforce/blip-image-captioning-large"
            processor = BlipProcessor.from_pretrained(model_id)
            model = BlipForConditionalGeneration.from_pretrained(model_id).to(device)
            model.eval()

            total = len(files)
            for i, fn in enumerate(files):
                path = os.path.join(WORK, fn)
                try:
                    img = Image.open(path).convert("RGB")
                    inputs = processor(img, return_tensors="pt").to(device)
                    with torch.no_grad():
                        out = model.generate(**inputs, max_new_tokens=50, num_beams=4)
                    caption = processor.decode(out[0], skip_special_tokens=True).strip()
                except Exception as e:
                    caption = ""
                    print(f"ERR {fn}: {e!r}", flush=True)

                txt = os.path.splitext(path)[0] + ".txt"
                with open(txt, "w", encoding="utf-8") as f:
                    f.write(caption)

                print(f"CAPTION {i+1}/{total} :: {caption}", flush=True)

            print("DONE", flush=True)
            """;

        var scriptPath = Path.Combine(workDir, "_caption.py");
        File.WriteAllText(scriptPath, script, new UTF8Encoding(false));
        return scriptPath;
    }

    // ── Subprocess ──────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<string>> RunPythonAsync(
        string                      pythonExe,
        string                      scriptPath,
        string                      workDir,
        int                         total,
        IProgress<CaptionProgress>? progress,
        CancellationToken           ct)
    {
        void Handle(string line)
        {
            // Chybové/diagnostické řádky na Warning, ať jsou vidět
            if (line.StartsWith("IMPORT_ERROR", StringComparison.Ordinal) ||
                line.StartsWith("ERR ", StringComparison.Ordinal) ||
                line.StartsWith("NO_IMAGES", StringComparison.Ordinal))
                Log.Warning("[caption] {Line}", line);
            else
                Log.Debug("[caption] {Line}", line);

            var m = CaptionLineRegex.Match(line);
            if (m.Success &&
                int.TryParse(m.Groups[1].Value, out var done) &&
                int.TryParse(m.Groups[2].Value, out var tot))
            {
                var caption = m.Groups[3].Value.Trim();
                progress?.Report(new CaptionProgress(done, tot,
                    $"obrázek {done}", Truncate(caption, 80)));
            }
            else if (line.StartsWith("LOADING", StringComparison.Ordinal))
            {
                progress?.Report(new CaptionProgress(0, total, string.Empty,
                    "Načítám BLIP model do paměti…"));
            }
        }

        var result = await ProcessRunner.RunAsync(
            new ProcessRunOptions
            {
                FileName         = pythonExe,
                Arguments        = $"\"{scriptPath}\"",
                WorkingDirectory = workDir,
                TailSize         = 64,
            },
            onLine: Handle,
            ct:     ct);

        if (!result.Success)
        {
            var tailText = result.TailLines.Count > 0 ? "\nVýstup:\n" + result.TailText : string.Empty;
            Log.Warning("BlipCaptionService: skript exit code {Code}.{Tail}", result.ExitCode, tailText);
            throw new InvalidOperationException(
                $"Captioning skript selhal (exit code {result.ExitCode}).{tailText}");
        }

        return result.TailLines;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" :
        s.Length <= max ? s : s[..max] + "…";
}
