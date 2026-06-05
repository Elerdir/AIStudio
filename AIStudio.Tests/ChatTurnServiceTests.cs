using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Core.Services;
using AIStudio.Infrastructure.Services;
using FluentAssertions;
using NSubstitute;

namespace AIStudio.Tests;

public sealed class ChatTurnServiceTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("aistudio_turn_").FullName;

    private (ChatTurnService svc, ILlamaService llama, ISettingsService settings) Make(
        bool useGpu = true, int contextSize = 4096)
    {
        var llama    = Substitute.For<ILlamaService>();
        var settings = Substitute.For<ISettingsService>();
        settings.Settings.Returns(new AppSettings
        {
            ModelsDirectory = _tmp,
            UseGpu          = useGpu,
            ChatContextSize = contextSize,
        });
        return (new ChatTurnService(llama, settings), llama, settings);
    }

    [Fact]
    public async Task EnsureModelLoaded_AlreadyLoaded_DoesNotReload()
    {
        var (svc, llama, _) = Make();
        llama.IsLoaded.Returns(true);
        llama.LoadedModelName.Returns("my-model");

        await svc.EnsureModelLoadedAsync("my-model", CancellationToken.None);

        await llama.DidNotReceive().LoadModelAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureModelLoaded_FileMissing_Throws_DoesNotLoad()
    {
        var (svc, llama, _) = Make();
        llama.IsLoaded.Returns(false);

        var act = async () => await svc.EnsureModelLoadedAsync("neexistuje", CancellationToken.None);

        await act.Should().ThrowAsync<ModelNotAvailableException>();
        await llama.DidNotReceive().LoadModelAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureModelLoaded_FileExists_LoadsWithSettings()
    {
        var (svc, llama, _) = Make(useGpu: true, contextSize: 8192);
        llama.IsLoaded.Returns(false);

        // Vytvoř GGUF soubor v rozlišené Models složce, ať ho ModelPathResolver najde.
        var resolvedDir = AppPaths.ResolveModelsDirectory(_tmp);
        Directory.CreateDirectory(resolvedDir);
        var modelFile = Path.Combine(resolvedDir, "mymodel.gguf");
        await File.WriteAllTextAsync(modelFile, "x");

        await svc.EnsureModelLoadedAsync("mymodel", CancellationToken.None);

        await llama.Received(1).LoadModelAsync(
            modelFile, "mymodel",
            gpuLayers: -1,            // useGpu=true → -1
            contextSize: 8192,
            ct: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureModelLoaded_CpuMode_UsesZeroGpuLayers()
    {
        var (svc, llama, _) = Make(useGpu: false);
        llama.IsLoaded.Returns(false);

        var resolvedDir = AppPaths.ResolveModelsDirectory(_tmp);
        Directory.CreateDirectory(resolvedDir);
        await File.WriteAllTextAsync(Path.Combine(resolvedDir, "cpu.gguf"), "x");

        await svc.EnsureModelLoadedAsync("cpu", CancellationToken.None);

        await llama.Received(1).LoadModelAsync(
            Arg.Any<string>(), "cpu", gpuLayers: 0, contextSize: 4096, ct: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamReply_BuildsHistoryWithSystemPrompt_DelegatesToLlama()
    {
        var (svc, llama, _) = Make();
        IReadOnlyList<(string Role, string Content)>? captured = null;
        llama.ChatAsync(
                Arg.Do<IReadOnlyList<(string Role, string Content)>>(h => captured = h),
                Arg.Any<int>(), Arg.Any<float>(), Arg.Any<CancellationToken>())
            .Returns(_ => Tokens("ahoj", " světe"));

        var req = new ChatTurnRequest(
            SystemPrompt:    "Mluv jako pirát.",
            ModelName:       "llama-3.1-8b",
            ThinkingEnabled: true,
            PriorMessages:   new[] { ("user", "ahoj") },
            MaxTokens:       256,
            Temperature:     0.7);

        var outTokens = new List<string>();
        await foreach (var t in svc.StreamReplyAsync(req, CancellationToken.None))
            outTokens.Add(t);

        outTokens.Should().Equal("ahoj", " světe");
        captured.Should().NotBeNull();
        captured![0].Role.Should().Be("system");
        captured[0].Content.Should().Be("Mluv jako pirát.");
        captured.Should().Contain(m => m.Role == "user" && m.Content == "ahoj");
    }

    private static async IAsyncEnumerable<string> Tokens(params string[] xs)
    {
        foreach (var x in xs) { yield return x; await Task.CompletedTask; }
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* ignore */ }
    }
}
