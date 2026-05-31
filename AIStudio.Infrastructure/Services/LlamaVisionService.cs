using System.Runtime.CompilerServices;
using System.Text;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Serilog;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Vision-language model pro chat (Stage 3) — „vidí" přiložený obrázek a odpoví
/// na otázku / popíše ho. Vlastní malý VLM (Qwen2.5-VL 7B GGUF + mmproj projektor),
/// oddělený od uživatelova chat modelu.
///
/// <para>Běží přes LlamaSharp mtmd API: text model (<see cref="LLamaWeights"/>) +
/// multimodální projektor (<see cref="MtmdWeights"/>) → <see cref="InteractiveExecutor"/>,
/// který umí média nativně. Obrázek se nahraje přes <c>LoadMedia</c> a v promptu
/// se odkáže media markerem; zbytek (encode + decode) řeší executor, my jen
/// streamujeme tokeny stejně jako běžný chat.</para>
///
/// <para>Model se načítá lazy při prvním dotazu a zůstává v paměti. NativeLibraryConfig
/// schválně nenastavujeme — to dělá <see cref="LlamaService"/> (je process-global a
/// druhý set by spadl); LlamaSharp jinak backend autodetekuje.</para>
/// </summary>
public sealed class LlamaVisionService : IVisionService
{
    private readonly IDownloadService _downloader;

    // ggml-org (llama.cpp org) — veřejné, bez tokenu
    private const string ModelFile  = "Qwen2.5-VL-7B-Instruct-Q4_K_M.gguf";
    private const string MmprojFile = "mmproj-Qwen2.5-VL-7B-Instruct-Q8_0.gguf";
    private const string Subdir     = "vision";
    private const string RepoBase   =
        "https://huggingface.co/ggml-org/Qwen2.5-VL-7B-Instruct-GGUF/resolve/main/";

    private readonly SemaphoreSlim _lock = new(1, 1);

    private LLamaWeights? _model;
    private MtmdWeights?  _mtmd;
    private ModelParams?  _modelParams;
    private string        _mediaMarker = "<__media__>";
    private string?       _loadedDir;

    private volatile bool   _isBusy;
    private volatile bool   _isDownloading;
    private volatile string _statusLine = string.Empty;
    private int _runningFlag;

    public LlamaVisionService(IDownloadService downloader) => _downloader = downloader;

    public bool   IsBusy             => _isBusy;
    public bool   IsDownloading      => _isDownloading;
    public string DownloadStatusLine => _statusLine;

    // ── Dostupnost ─────────────────────────────────────────────────────────────

    public bool IsModelAvailable(string modelsDir)
    {
        if (string.IsNullOrWhiteSpace(modelsDir)) return false;
        var dir = Path.Combine(modelsDir, Subdir);
        return File.Exists(Path.Combine(dir, ModelFile))
            && File.Exists(Path.Combine(dir, MmprojFile));
    }

    // ── Stahování ──────────────────────────────────────────────────────────────

    public async Task EnsureModelAsync(string modelsDir, string? hfToken,
        IProgress<DownloadProgressInfo>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(modelsDir)) return;
        if (IsModelAvailable(modelsDir)) return;

        if (Interlocked.CompareExchange(ref _runningFlag, 1, 0) != 0)
        {
            Log.Debug("LlamaVisionService.EnsureModelAsync: stahování již probíhá, přeskakuji");
            return;
        }

        _isDownloading = true;
        try
        {
            var dir = Path.Combine(modelsDir, Subdir);
            Directory.CreateDirectory(dir);

            // mmproj (menší) první, pak model
            await DownloadIfMissingAsync(dir, MmprojFile, "VLM projektor", hfToken, progress, ct);
            await DownloadIfMissingAsync(dir, ModelFile,  "VLM model",     hfToken, progress, ct);
        }
        finally
        {
            _isDownloading = false;
            _statusLine    = string.Empty;
            Interlocked.Exchange(ref _runningFlag, 0);
        }
    }

    private async Task DownloadIfMissingAsync(string dir, string fileName, string label,
        string? hfToken, IProgress<DownloadProgressInfo>? progress, CancellationToken ct)
    {
        var dstPath = Path.Combine(dir, fileName);
        if (File.Exists(dstPath)) return;

        var tmpPath = dstPath + ".tmp";
        _statusLine = $"Stahuji {label}…";
        Log.Information("LlamaVisionService: stahuji {File}", fileName);

        var wrapped = new Progress<DownloadProgressInfo>(info =>
        {
            progress?.Report(info);
            var pct = info.Total > 0 ? (int)(100 * info.Downloaded / info.Total) : 0;
            _statusLine = info.Total > 0
                ? $"Stahuji {label} {pct} % ({info.Downloaded / 1_048_576} / {info.Total / 1_048_576} MB)"
                : $"Stahuji {label}…";
        });

        try
        {
            await _downloader.DownloadFileAsync(RepoBase + fileName, tmpPath, wrapped, apiToken: hfToken, ct: ct);
            if (File.Exists(dstPath)) File.Delete(dstPath);
            File.Move(tmpPath, dstPath);
            Log.Information("LlamaVisionService: {File} stažen", fileName);
        }
        catch (OperationCanceledException)
        {
            TryDeleteTmp(tmpPath);
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LlamaVisionService: stahování {File} selhalo", fileName);
            TryDeleteTmp(tmpPath);
        }
    }

    // ── Inference ──────────────────────────────────────────────────────────────

    public async IAsyncEnumerable<string> DescribeAsync(
        string imagePath, string question, string modelsDir,
        int maxTokens = 512, float temperature = 0.4f,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!File.Exists(imagePath))
        {
            yield return "⚠️ Obrázek nebyl nalezen.";
            yield break;
        }
        if (!IsModelAvailable(modelsDir))
        {
            yield return "⚠️ Vision model není stažen.";
            yield break;
        }

        await _lock.WaitAsync(ct);
        _isBusy = true;
        LLamaContext? context = null;
        try
        {
            await EnsureLoadedAsync(modelsDir, ct);
            if (_model is null || _mtmd is null || _modelParams is null)
            {
                yield return "⚠️ Vision model se nepodařilo načíst.";
                yield break;
            }

            context = _model.CreateContext(_modelParams);
            var executor = new InteractiveExecutor(context, _mtmd, logger: null);

            // Nahraj obrázek — zůstane „pending" pro nejbližší tokenizaci v InferAsync.
            _mtmd.LoadMedia(imagePath);

            var prompt = BuildPrompt(question, _mediaMarker);

            var inferenceParams = new InferenceParams
            {
                MaxTokens        = maxTokens,
                AntiPrompts      = new[] { "<|im_end|>", "<|im_start|>" },
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = Math.Clamp(temperature, 0.0f, 2.0f),
                    TopP        = 0.9f,
                },
            };

            string? errorMsg = null;
            var enumerator = executor.InferAsync(prompt, inferenceParams, ct).GetAsyncEnumerator(ct);
            try
            {
                while (true)
                {
                    bool hasMore;
                    string? token = null;
                    try
                    {
                        hasMore = await enumerator.MoveNextAsync();
                        if (hasMore) token = enumerator.Current;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "LlamaVisionService: inference selhala");
                        errorMsg = "\n\n⚠️ Analýza obrázku selhala.";
                        break;
                    }

                    if (!hasMore) break;
                    if (!string.IsNullOrEmpty(token)) yield return token;
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }

            if (errorMsg is not null) yield return errorMsg;
        }
        finally
        {
            context?.Dispose();
            _mtmd?.ClearMedia();
            _isBusy = false;
            _lock.Release();
        }
    }

    /// <summary>Qwen2.5-VL používá ChatML; media marker patří do user turnu před otázku.</summary>
    private static string BuildPrompt(string question, string marker)
    {
        var sb = new StringBuilder();
        sb.Append("<|im_start|>system\n");
        sb.Append("Jsi vizuální asistent. Odpovídáš přesně a v jazyce uživatele.<|im_end|>\n");
        sb.Append("<|im_start|>user\n");
        sb.Append(marker).Append('\n');
        sb.Append(string.IsNullOrWhiteSpace(question) ? "Popiš tento obrázek." : question.Trim());
        sb.Append("<|im_end|>\n");
        sb.Append("<|im_start|>assistant\n");
        return sb.ToString();
    }

    private async Task EnsureLoadedAsync(string modelsDir, CancellationToken ct)
    {
        var dir = Path.Combine(modelsDir, Subdir);
        if (_model is not null && _loadedDir == dir) return;

        // Pokud byl model načtený z jiné cesty, uvolni ho
        UnloadInternal();

        var modelPath  = Path.Combine(dir, ModelFile);
        var mmprojPath = Path.Combine(dir, MmprojFile);

        var mp = new ModelParams(modelPath)
        {
            ContextSize   = 8192,
            GpuLayerCount = -1,   // offload vše na GPU (24 GB to utáhne); CPU fallback když se nevejde
        };

        Log.Information("LlamaVisionService: načítám VLM {Model} + projektor {Mmproj}", ModelFile, MmprojFile);
        var model = await LLamaWeights.LoadFromFileAsync(mp, ct);

        var mtmdParams = MtmdContextParams.Default();
        mtmdParams.UseGpu = true;
        _mediaMarker = string.IsNullOrEmpty(mtmdParams.MediaMarker) ? _mediaMarker : mtmdParams.MediaMarker;

        var mtmd = MtmdWeights.LoadFromFile(mmprojPath, model, mtmdParams);

        _model       = model;
        _mtmd        = mtmd;
        _modelParams = mp;
        _loadedDir   = dir;

        Log.Information("LlamaVisionService: VLM načten (SupportsVision={Vis})", mtmd.SupportsVision);
    }

    private void UnloadInternal()
    {
        try { _mtmd?.Dispose(); }  catch (Exception ex) { Log.Warning(ex, "LlamaVisionService: mtmd dispose"); }
        try { _model?.Dispose(); } catch (Exception ex) { Log.Warning(ex, "LlamaVisionService: model dispose"); }
        _mtmd = null; _model = null; _modelParams = null; _loadedDir = null;
    }

    private static void TryDeleteTmp(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log.Warning(ex, "LlamaVisionService: nelze smazat {Path}", path); }
    }
}
