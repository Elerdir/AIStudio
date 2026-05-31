using AIStudio.Infrastructure.Services;
using FluentAssertions;

namespace AIStudio.Tests;

/// <summary>
/// Testy pro <see cref="ModelPathResolver"/> — používají dočasný adresář
/// s fake .gguf soubory, aby ověřily exact / fuzzy / fallback strategie.
/// </summary>
public sealed class ModelPathResolverTests : IDisposable
{
    private readonly string _tmpDir;

    public ModelPathResolverTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "AIStudio.Tests.ModelPath", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    private void CreateFile(string name) => File.WriteAllText(Path.Combine(_tmpDir, name), "fake gguf");

    // ── Exact match z katalogu ────────────────────────────────────────────────

    [Fact]
    public void Resolve_ExactCatalogFileExists_ReturnsIt()
    {
        // „Llama 3.1 8B Instruct Q4_K_M" → Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf (z registru)
        CreateFile("Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf");

        var result = ModelPathResolver.Resolve(_tmpDir, "Llama 3.1 8B Instruct Q4_K_M");

        result.Should().EndWith("Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf");
        File.Exists(result).Should().BeTrue();
    }

    // ── Fuzzy match (název neodpovídá katalogu, ale soubor obsahuje token) ─────

    [Fact]
    public void Resolve_FuzzyMatchInDir_ReturnsMatchingGguf()
    {
        // Soubor není v katalogu, ale jeho název obsahuje sanitizovaný model name
        CreateFile("my-custom-model-v2.gguf");

        var result = ModelPathResolver.Resolve(_tmpDir, "my-custom-model");

        result.Should().EndWith("my-custom-model-v2.gguf");
    }

    [Fact]
    public void Resolve_FuzzyMatch_SpaceAndSlashSanitized()
    {
        // Mezery a lomítka v názvu se mapují na podtržítka při hledání
        CreateFile("some_model_name.gguf");

        var result = ModelPathResolver.Resolve(_tmpDir, "some model/name");

        result.Should().EndWith("some_model_name.gguf");
    }

    [Fact]
    public void Resolve_FuzzyMatch_Recursive()
    {
        var sub = Path.Combine(_tmpDir, "subfolder");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "nested-model.gguf"), "fake");

        var result = ModelPathResolver.Resolve(_tmpDir, "nested-model");

        result.Should().EndWith("nested-model.gguf");
        result.Should().Contain("subfolder");
    }

    // ── Fallback (nic nenalezeno) ─────────────────────────────────────────────

    [Fact]
    public void Resolve_NoMatch_ReturnsAssumedPath()
    {
        // Prázdná složka, neznámý model → fallback {dir}/{modelName}.gguf
        var result = ModelPathResolver.Resolve(_tmpDir, "totally-unknown-model");

        result.Should().Be(Path.Combine(_tmpDir, "totally-unknown-model.gguf"));
        File.Exists(result).Should().BeFalse();
    }

    [Fact]
    public void Resolve_NonexistentDir_ReturnsFallbackWithoutThrowing()
    {
        var ghostDir = Path.Combine(_tmpDir, "does-not-exist");

        var act = () => ModelPathResolver.Resolve(ghostDir, "model");

        act.Should().NotThrow();
        ModelPathResolver.Resolve(ghostDir, "model")
            .Should().Be(Path.Combine(ghostDir, "model.gguf"));
    }

    [Fact]
    public void Resolve_CatalogModelButFileAbsent_FallsBackToCatalogFileName()
    {
        // Model je v katalogu, ale soubor není na disku → fallback vrátí
        // katalogový filename (ne modelName.gguf)
        var result = ModelPathResolver.Resolve(_tmpDir, "Phi-4 Q4_K_M");

        result.Should().EndWith("phi-4-Q4_K_M.gguf");
    }
}
