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

public sealed class LlamaService : ILlamaService
{
    private readonly IGpuDetector?          _gpuDetector;
    private readonly ISystemMonitorService? _monitor;

    /// <summary>
    /// True jakmile jsme zavolali <see cref="NativeLibraryConfig"/>.With* metodu.
    /// LLamaSharp NativeLibraryConfig je process-global a musí se nastavit
    /// PŘED prvním <see cref="LLamaWeights.LoadFromFileAsync"/>. Druhé volání
    /// na různém backendu už nepřepne — proto lazy init při prvním LoadModelAsync.
    /// </summary>
    private bool _backendInitialized;

    private LLamaWeights? _model;
    private ModelParams?  _modelParams;   // uloženo pro tvorbu čerstvého kontextu při každé generaci

    private readonly SemaphoreSlim _lock = new(1, 1);

    public string? LoadedModelName  { get; private set; }
    public string  BackendInfo      { get; private set; } = string.Empty;
    public Gpu?    DetectedGpu      { get; private set; }
    public bool    IsLoaded         => _model is not null;
    public bool    IsLoadingModel   { get; private set; }
    public bool    UseGpu           { get; set; } = true;

    public event Action<string>? StatusChanged;

    /// <summary>
    /// DI registruje s <see cref="IGpuDetector"/> pokud je dostupný (Windows).
    /// Bez detektoru padáme zpět na původní hard-coded CUDA inicializaci —
    /// kompatibilní s aktuálním NVIDIA-only buildem.
    ///
    /// <see cref="ISystemMonitorService"/> je optional — slouží pro diagnostiku
    /// při načítání modelu (VRAM/RAM delta před a po loadu). Bez něj loader
    /// funguje, jen je v logu méně detailů.
    /// </summary>
    public LlamaService(IGpuDetector? gpuDetector = null, ISystemMonitorService? monitor = null)
    {
        _gpuDetector = gpuDetector;
        _monitor     = monitor;
    }

    /// <summary>
    /// Lazy inicializace LLamaSharp backendu. Voláme z <see cref="LoadModelAsync"/>
    /// před prvním LoadFromFileAsync — později už <see cref="NativeLibraryConfig"/>
    /// nepřijímá změny.
    ///
    /// Aktuální stav (Phase A.2): NVIDIA → WithCuda, ostatní → CUDA fallback s warn.
    /// Vulkan a Metal native libs zatím nejsou v projektu — přidají se v Phase B/C.
    /// Pak se sem doplní WithVulkan() / WithMetal() větve.
    /// </summary>
    private async Task EnsureBackendInitializedAsync(CancellationToken ct)
    {
        if (_backendInitialized) return;

        // Detekce GPU — pokud máme detector (Windows), zjistíme vendor;
        // bez detektoru zachováme původní chování (zkusit CUDA).
        if (_gpuDetector is not null)
        {
            try
            {
                DetectedGpu = await _gpuDetector.DetectAsync(ct);
                Log.Information("LlamaService: detekovaná GPU {Vendor} {Name} (backend {Backend}, {VramGb:F1} GB VRAM)",
                                DetectedGpu.Vendor, DetectedGpu.Name, DetectedGpu.Backend, DetectedGpu.VramGb);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "LlamaService: GPU detekce selhala — používám CUDA fallback");
            }
        }

        switch (DetectedGpu?.Backend)
        {
            case GpuBackend.Cuda:
                TrySetBackend(() => NativeLibraryConfig.All.WithCuda(), "CUDA");
                break;

            case GpuBackend.Vulkan:
                // Phase B.1 zapojeno — LLamaSharp.Backend.Vulkan v csproj.
                // Funguje na NVIDIA / AMD / Intel kartách. Pro AMD/Intel je to
                // jediná dostupná akcelerace pro LLM inference na Windows.
                TrySetBackend(() => NativeLibraryConfig.All.WithVulkan(), "Vulkan");
                break;

            case GpuBackend.Metal:
                // Phase C.2 zapojeno — LLamaSharp.Backend.Metal je v csproj
                // pod IsOSPlatform('OSX') guardu. Protože extension metoda
                // WithMetal() existuje jen když je Backend.Metal balík přítomen
                // (na Windows ne), voláme ji přes reflection. Na non-macOS
                // se sem stejně nedostaneme — IGpuDetector vrací Apple jen
                // na Apple Silicon, kde Metal balík v csproj je.
                TrySetBackend(InvokeWithMetal, "Metal");
                break;

            case GpuBackend.Cpu:
            case GpuBackend.DirectMl:
                // Žádné With* volání = LLamaSharp default CPU.
                Log.Information("LlamaService: poběží na CPU (žádný kompatibilní GPU backend)");
                break;

            case null:
            default:
                // Bez detektoru — zachovat původní chování (CUDA + tichý fallback)
                TrySetBackend(() => NativeLibraryConfig.All.WithCuda(), "CUDA (default)");
                break;
        }

        _backendInitialized = true;
    }

    private static void TrySetBackend(Action setter, string label)
    {
        try
        {
            setter();
            Log.Information("LlamaService: nastaven {Label} backend", label);
        }
        catch (Exception ex)
        {
            // Native lib nedostupná (např. CUDA runtime chybí) — LLamaSharp si
            // tiše stáhne CPU lib. To je v pořádku, jen to zalogujeme.
            Log.Warning(ex, "LlamaService: {Label} init selhalo, propadne se na CPU", label);
        }
    }

    /// <summary>
    /// Volá <c>NativeLibraryConfig.All.WithMetal()</c> přes reflection, protože
    /// extension metoda existuje pouze když je v projektu balík
    /// <c>LLamaSharp.Backend.Metal</c> (conditional pod IsOSPlatform('OSX')).
    /// Na Windows / Linux buildu by přímé volání nezkompilovalo.
    ///
    /// Pokud reflection nenajde <c>WithMetal</c> (např. starší verze nebo
    /// balík chybí v aktuálním buildu), vyhodí <see cref="InvalidOperationException"/>;
    /// <see cref="TrySetBackend"/> ji zalogguje jako warning a propadne na CPU.
    /// </summary>
    private static void InvokeWithMetal()
    {
        var configContainer = typeof(NativeLibraryConfig)
            .GetProperty("All", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            ?.GetValue(null)
            ?? throw new InvalidOperationException("NativeLibraryConfig.All nedostupné");

        var withMetal = configContainer.GetType().GetMethod("WithMetal",
                            new[] { typeof(bool) })
                     ?? configContainer.GetType().GetMethod("WithMetal", Type.EmptyTypes)
                     ?? throw new InvalidOperationException(
                            "WithMetal API není dostupné — chybí balík LLamaSharp.Backend.Metal " +
                            "(očekáváno na macOS Apple Silicon buildu).");

        // Bool overload preferujeme — zachová symetrii s WithCuda(true) / WithVulkan(true).
        var args = withMetal.GetParameters().Length == 1
            ? new object[] { true }
            : Array.Empty<object>();
        withMetal.Invoke(configContainer, args);
    }

    // ── Načítání modelu ────────────────────────────────────────────────────────

    public async Task LoadModelAsync(string modelPath, string modelName,
                                     int gpuLayers = -1, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            IsLoadingModel = true;
            StatusChanged?.Invoke($"Načítám {modelName}…");

            await DisposeModelAsync();

            // ── 1) Backend init ───────────────────────────────────────────────
            await EnsureBackendInitializedAsync(ct);

            // ── 2) Diagnostika: file size + VRAM snapshot před loadem ─────────
            long modelSizeBytes = 0;
            try { modelSizeBytes = new FileInfo(modelPath).Length; } catch { /* nelze získat — diag bude méně přesný */ }

            var vramBeforeGb = _monitor?.Current?.VramUsedGb ?? 0;
            var ramBeforeGb  = _monitor?.Current?.RamUsedGb  ?? 0;

            Log.Information("LlamaService.LoadModel: {Name} | path={Path} | velikost={SizeMB} MB | " +
                            "GPU={UseGpu} | gpuLayers={Layers} | VRAM před={VramGb:F1} GB | RAM před={RamGb:F1} GB",
                            modelName, modelPath, modelSizeBytes / 1_048_576,
                            UseGpu, gpuLayers, vramBeforeGb, ramBeforeGb);

            // ── 3) Vlastní load ───────────────────────────────────────────────
            var parameters = new ModelParams(modelPath)
            {
                ContextSize    = 4096,
                GpuLayerCount  = UseGpu ? gpuLayers : 0,   // -1 = vše na GPU, 0 = CPU
                FlashAttention = true,
            };

            try
            {
                _model         = await LLamaWeights.LoadFromFileAsync(parameters, ct);
                _modelParams   = parameters;
                LoadedModelName = modelName;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // LLamaWeights.LoadFromFileAsync hodí native exception různých typů:
                // - "unknown model architecture" (nová verze GGUF, např. Qwen3 Next, Llama 4)
                // - "tensor 'x' not found" (nekompletní download, broken soubor)
                // - "tensor type not implemented" (nepodporovaná kvantizace IQ1_S apod.)
                // - "image model detected" (uživatel omylem dal FLUX checkpoint do chat)
                //
                // Native errors jdou většinou přes RuntimeException s message
                // obsahujícím anglický text — heuristikou je převedeme na user-friendly
                // hint v češtině.
                var hint = ClassifyLoadError(ex, modelName, modelSizeBytes);
                Log.Warning(ex, "LlamaService.LoadModel: {Name} selhalo — {Hint}", modelName, hint);
                StatusChanged?.Invoke($"❌ {modelName}: {hint}");
                throw new AIStudio.Core.Models.ModelLoadFailedException(modelName, modelPath, hint, ex);
            }

            // ── 4) Diagnostika: zjisti, jestli skutečně skončil na GPU ────────
            // llama_supports_gpu_offload() vrací true pokud native DLL byl
            // zkompilován s CUDA/Vulkan/Metal — ale neříká, jestli ten konkrétní
            // model skončil v GPU. Pro to měříme VRAM delta vs očekávaná velikost.
            var gpuSupported = false;
            try { gpuSupported = NativeApi.llama_supports_gpu_offload(); }
            catch { /* starší verze LLamaSharp nebo jiný název API */ }

            // VRAM ticker v SystemMonitorService aktualizuje každé 2,5 s; po LoadFromFileAsync
            // dáme mu max 3,5 s na refresh, jinak diagnostika bude vrácet "nezměřeno".
            double vramAfterGb = vramBeforeGb;
            double ramAfterGb  = ramBeforeGb;
            if (_monitor is not null)
            {
                try { await Task.Delay(3500, ct); } catch (OperationCanceledException) { }
                vramAfterGb = _monitor.Current?.VramUsedGb ?? vramBeforeGb;
                ramAfterGb  = _monitor.Current?.RamUsedGb  ?? ramBeforeGb;
            }
            var vramDeltaGb = vramAfterGb - vramBeforeGb;
            var ramDeltaGb  = ramAfterGb  - ramBeforeGb;

            // Heuristika: model měl skončit na GPU pokud:
            //   1) UseGpu = true, gpuSupported = true
            //   2) VRAM stoupla aspoň o 50 % velikosti modelu (zbytek je v RAM kvůli
            //      mmap / kv cache / sdíleným weights — to je normální).
            // Pokud UseGpu=true, gpuSupported=true, ale VRAM stoupla < 10 % velikosti =
            // GPU offload selhal (např. nedostatek VRAM → llama.cpp tiše propadl na CPU).
            var expectedVramGb     = modelSizeBytes / 1_073_741_824.0;
            var vramOffloadSuccess = vramDeltaGb >= expectedVramGb * 0.5;
            var probablyFellToCpu  = UseGpu && gpuSupported && _monitor is not null
                                     && expectedVramGb > 0
                                     && vramDeltaGb < expectedVramGb * 0.1;

            // BackendInfo zachovává stávající smluvu pro UI badge:
            //   - "GPU" = uživatel chce GPU, support exists, VRAM delta vypadá rozumně
            //   - "CPU" = jakákoliv jiná kombinace
            BackendInfo = UseGpu && gpuSupported && !probablyFellToCpu ? "GPU" : "CPU";

            Log.Information("LlamaService.LoadModel: hotovo {Name} | backend={Backend} | gpu_offload={GpuOff} | " +
                            "VRAM Δ={VramDelta:F2} GB | RAM Δ={RamDelta:F2} GB | offload_success={Success} | " +
                            "fell_to_cpu={FellToCpu}",
                            modelName, BackendInfo, gpuSupported, vramDeltaGb, ramDeltaGb,
                            vramOffloadSuccess, probablyFellToCpu);

            // ── 5) Status message s diagnostikou ─────────────────────────────
            string statusMsg;
            if (!UseGpu)
            {
                statusMsg = $"Model: {modelName} [CPU — uživatel zvolil v Nastavení]";
            }
            else if (!gpuSupported)
            {
                // CUDA/Vulkan native lib se nenahraje — runtime chybí
                statusMsg = DetectedGpu?.Vendor switch
                {
                    AIStudio.Core.Models.GpuVendor.Nvidia
                        => $"Model: {modelName} [CPU — CUDA 12 runtime nenalezen, nainstaluj z nvidia.com]",
                    AIStudio.Core.Models.GpuVendor.Amd or AIStudio.Core.Models.GpuVendor.Intel
                        => $"Model: {modelName} [CPU — Vulkan runtime nenalezen, aktualizuj GPU ovladač]",
                    _   => $"Model: {modelName} [CPU — GPU backend nenalezen]"
                };
            }
            else if (probablyFellToCpu)
            {
                // Worst case — backend lib OK, ale VRAM nestouplo. Nejčastější příčina:
                // model je větší než dostupná VRAM, llama.cpp tiše propadl na CPU.
                statusMsg = $"Model: {modelName} [CPU fallback — model se nevešel do VRAM " +
                            $"(potřebuje ~{expectedVramGb:F1} GB)]";
                Log.Warning("LlamaService: GPU offload tiše selhal pro {Name} (model {Size:F1} GB, " +
                            "VRAM Δ jen {Delta:F2} GB). Zkus menší kvantizaci nebo modelu, " +
                            "nebo ulož ostatní GPU procesy.",
                            modelName, expectedVramGb, vramDeltaGb);
            }
            else
            {
                // Vše OK — model je na GPU
                var deltaInfo = _monitor is not null && vramDeltaGb >= 0.1
                    ? $" — VRAM +{vramDeltaGb:F1} GB"
                    : string.Empty;
                statusMsg = $"Model: {modelName} [GPU ✓]{deltaInfo}";
            }

            StatusChanged?.Invoke(statusMsg);
        }
        finally
        {
            IsLoadingModel = false;
            _lock.Release();
        }
    }

    public async Task UnloadModelAsync()
    {
        await _lock.WaitAsync();
        try { await DisposeModelAsync(); }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Mapuje native exception z LLamaWeights.LoadFromFileAsync na user-friendly
    /// český hint. Heuristika podle známých substringů v ex.Message; pokud
    /// nepoznáme, vrátíme generický hint s odkazem na update aplikace.
    /// </summary>
    private static string ClassifyLoadError(Exception ex, string modelName, long modelSizeBytes)
    {
        var msg   = (ex.Message ?? string.Empty).ToLowerInvariant();
        var inner = (ex.InnerException?.Message ?? string.Empty).ToLowerInvariant();
        var all   = msg + " || " + inner;

        // Architecture not supported — nový model, starý llama.cpp
        if (all.Contains("unknown model architecture") ||
            all.Contains("unsupported architecture")   ||
            all.Contains("unknown architecture"))
            return "Architektura modelu není v této verzi aplikace podporovaná. " +
                   "Zkus aktualizovat AI Studio nebo zvolit starší model (Llama 3.x, Qwen 2.5, Mistral).";

        // Corrupted / partial download — chybí tensory
        if (all.Contains("tensor") && (all.Contains("not found") || all.Contains("missing")))
            return "Soubor je poškozený nebo neúplný (chybí některé tensory). " +
                   "Smaž ho v sekci Modely a stáhni znovu.";

        // Kvantizace nepodporovaná
        if (all.Contains("tensor type") || all.Contains("quantization") || all.Contains("ggml_type"))
            return "Kvantizace tohoto modelu není podporovaná. " +
                   "Zkus klasickou Q4_K_M nebo Q5_K_M variantu — IQ1/IQ2/IQ3 vyžadují novější llama.cpp.";

        // Image model omylem — FLUX / SD checkpoint
        if (modelName.Contains("flux", StringComparison.OrdinalIgnoreCase) ||
            modelName.Contains("sd", StringComparison.OrdinalIgnoreCase) ||
            all.Contains("image") || all.Contains("diffusion"))
            return "Vypadá to jako obrázkový model (FLUX/SDXL), ne chat. " +
                   "Image modely patří do Image Studia, ne do chatu.";

        // GGUF magic byte mismatch — vůbec není GGUF
        if (all.Contains("magic") || all.Contains("invalid file") || all.Contains("not a gguf"))
            return "Soubor není platný GGUF model. Možná se stáhla špatná varianta " +
                   "— zkontroluj URL nebo zkus jiný quant.";

        // Příliš malý soubor — pravděpodobně chyba downloadu
        if (modelSizeBytes > 0 && modelSizeBytes < 100_000_000)
            return $"Soubor je podezřele malý ({modelSizeBytes / 1_048_576} MB) — " +
                   "pravděpodobně se nedostáhnul. Smaž a stáhni znovu.";

        // Out of memory
        if (all.Contains("out of memory") || all.Contains("oom") || all.Contains("alloc"))
            return "Nedostatek paměti pro načtení modelu. Zkus menší kvantizaci nebo " +
                   "vypni GPU offload v Nastavení (běží na CPU + RAM).";

        // Generic fallback
        return $"Načtení selhalo: {ex.Message?.Trim() ?? "neznámá chyba"}. " +
               "Zkus restart aplikace, jiný kvant, nebo report bug.";
    }

    // ── Generování (chat) ─────────────────────────────────────────────────────

    public async IAsyncEnumerable<string> ChatAsync(
        IReadOnlyList<(string Role, string Content)> messages,
        int maxTokens = 2048,
        float temperature = 0.7f,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_model is null || _modelParams is null)
        {
            yield return "⚠️ Žádný model není načten. Nejprve nahraj model v sekci Modely.";
            yield break;
        }

        if (messages.Count == 0)
            yield break;

        // Rychle získáme reference (model nikam nepůjde dokud jen čteme)
        await _lock.WaitAsync(ct);
        LLamaWeights model;
        ModelParams  modelParams;
        try
        {
            if (_model is null || _modelParams is null)
            {
                yield return "⚠️ Model byl uvolněn.";
                yield break;
            }
            model       = _model;
            modelParams = _modelParams;
        }
        finally { _lock.Release(); }

        // ── Sestavení promptu podle chat template z GGUF metadat ──────────────
        // LLamaTemplate čte tokenizer.chat_template přímo z GGUF, takže pro každý
        // model (Llama 3, Qwen, Gemma, Mistral, Phi…) vygeneruje jeho NATIVNÍ formát
        // se speciálními tokeny (<|start_header_id|>…<|eot_id|> apod.). Bez toho
        // model dostával generický „User: …\nAssistant:" prompt, který nikdy
        // neviděl při tréninku — neuměl skončit a halucinoval celé další tahy.
        var prompt = BuildPrompt(model, messages);

        // ── Inference přes StatelessExecutor ─────────────────────────────────
        // Stateless = každé volání dostane vlastní KV cache → žádná kontaminace
        // mezi konverzacemi, žádné ruční vytváření kontextu, žádný ChatSession.
        var executor = new StatelessExecutor(model, modelParams);

        var inferenceParams = new InferenceParams
        {
            MaxTokens        = maxTokens,
            AntiPrompts      = AntiPromptsFor(LoadedModelName ?? ""),
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = Math.Clamp(temperature, 0.0f, 2.0f),
                TopP        = 0.9f,
            },
        };

        await foreach (var token in executor.InferAsync(prompt, inferenceParams, ct))
        {
            if (!string.IsNullOrEmpty(token))
                yield return token;
        }
    }

    /// <summary>
    /// Postaví prompt pomocí GGUF chat template modelu. Pokud z nějakého důvodu
    /// LLamaTemplate selže (model bez metadat, neznámý formát), spadne na
    /// generický fallback — který sice halucinuje, ale aspoň aplikace nekrachne.
    /// </summary>
    private static string BuildPrompt(
        LLamaWeights model,
        IReadOnlyList<(string Role, string Content)> messages)
    {
        try
        {
            var template = new LLamaTemplate(model);
            foreach (var (role, content) in messages)
                template.Add(NormalizeRole(role), content);
            template.AddAssistant = true;   // přidá hlavičku pro nadcházející asistent odpověď

            var bytes = template.Apply();
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            // Fallback — nemělo by se stát u moderních instruct GGUFů, ale jištění je jištění
            var sb = new StringBuilder();
            foreach (var (role, content) in messages)
            {
                var r = char.ToUpperInvariant(NormalizeRole(role)[0]) +
                        NormalizeRole(role)[1..];
                sb.Append(r).Append(": ").AppendLine(content);
            }
            sb.Append("Assistant: ");
            return sb.ToString();
        }
    }

    // ── Pomocné ───────────────────────────────────────────────────────────────

    private static string NormalizeRole(string role) => role.ToLowerInvariant() switch
    {
        "assistant" => "assistant",
        "system"    => "system",
        _           => "user",
    };

    /// <summary>
    /// Stop-tokeny specifické pro rodinu modelu.
    /// Bez správných anti-promptů model "preteče" do vymyšleného dalšího tahu uživatele.
    /// </summary>
    private static IReadOnlyList<string> AntiPromptsFor(string modelName)
    {
        var n = modelName.ToLowerInvariant();

        if (n.Contains("llama-3") || n.Contains("llama 3"))
            return ["<|eot_id|>", "<|end_of_text|>", "<|start_header_id|>user<|end_header_id|>"];

        if (n.Contains("mistral") || n.Contains("mixtral"))
            return ["</s>", "[INST]"];

        if (n.Contains("gemma"))
            return ["<end_of_turn>", "<start_of_turn>user"];

        if (n.Contains("qwen"))
            return ["<|im_end|>", "<|im_start|>user"];

        if (n.Contains("phi"))
            return ["<|end|>", "<|user|>"];

        if (n.Contains("deepseek"))
            return ["<|end▁of▁sentence|>", "User:"];

        // Obecný fallback
        return ["\nUser:", "\nHuman:", "</s>", "<|end|>"];
    }

    private async Task DisposeModelAsync()
    {
        _model?.Dispose();
        _model      = null;
        _modelParams = null;
        LoadedModelName = null;
        StatusChanged?.Invoke("Připraven");
        // Dej GC šanci uvolnit nativní paměť
        await Task.Run(GC.Collect);
    }

    public async ValueTask DisposeAsync()
    {
        await _lock.WaitAsync();
        try { await DisposeModelAsync(); }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }
}
