using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Core.Services;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Implementace <see cref="IChatTurnService"/> — centralizuje LLM tah, který byl dřív
/// rozkopírovaný v <c>ChatPageViewModel</c> (Send / Regenerate / Edit / Compare). Načtení
/// modelu řeší přes <see cref="ILlamaService"/> + <see cref="ModelPathResolver"/>, historii
/// přes pure <see cref="ChatPromptBuilder"/>.
/// </summary>
public sealed class ChatTurnService : IChatTurnService
{
    private readonly ILlamaService    _llama;
    private readonly ISettingsService _settings;

    public ChatTurnService(ILlamaService llama, ISettingsService settings)
    {
        _llama    = llama;
        _settings = settings;
    }

    public async Task EnsureModelLoadedAsync(string modelName, CancellationToken ct)
    {
        if (_llama.IsLoaded && _llama.LoadedModelName == modelName)
            return;

        var modelsDir = AppPaths.ResolveModelsDirectory(_settings.Settings.ModelsDirectory);
        var modelPath = ModelPathResolver.Resolve(modelsDir, modelName);
        if (!File.Exists(modelPath))
            throw new ModelNotAvailableException(modelName);

        var gpuLayers   = _settings.Settings.UseGpu ? -1 : 0;
        var contextSize = _settings.Settings.ChatContextSize;
        await _llama.LoadModelAsync(modelPath, modelName,
                                    gpuLayers: gpuLayers,
                                    contextSize: contextSize,
                                    ct: ct);
    }

    public IAsyncEnumerable<string> StreamReplyAsync(ChatTurnRequest request, CancellationToken ct)
    {
        var history = ChatPromptBuilder.BuildHistory(
            request.SystemPrompt, request.ModelName, request.ThinkingEnabled, request.PriorMessages);

        return _llama.ChatAsync(history, request.MaxTokens, (float)request.Temperature, ct);
    }
}
