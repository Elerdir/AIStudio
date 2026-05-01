using System.Runtime.CompilerServices;
using System.Text;
using AIStudio.Core.Interfaces;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace AIStudio.Infrastructure.Services;

public sealed class LlamaService : ILlamaService
{
    private LLamaWeights? _model;
    private ModelParams?  _modelParams;   // uloženo pro tvorbu čerstvého kontextu při každé generaci

    private readonly SemaphoreSlim _lock = new(1, 1);

    public string? LoadedModelName  { get; private set; }
    public bool    IsLoaded         => _model is not null;
    public bool    IsLoadingModel   { get; private set; }
    public bool    UseGpu           { get; set; } = true;

    public event Action<string>? StatusChanged;

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

            var parameters = new ModelParams(modelPath)
            {
                ContextSize    = 4096,
                GpuLayerCount  = UseGpu ? gpuLayers : 0,   // -1 = vše na GPU, 0 = CPU
                FlashAttention = true,
            };

            _model         = await LLamaWeights.LoadFromFileAsync(parameters, ct);
            _modelParams   = parameters;
            LoadedModelName = modelName;

            StatusChanged?.Invoke($"Model: {modelName}");
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
