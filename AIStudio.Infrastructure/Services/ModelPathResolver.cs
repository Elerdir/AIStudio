using AIStudio.Core.Models;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Rozlišení cesty ke GGUF modelu z jeho zobrazovaného názvu. Dříve zapečené
/// v <c>ChatPageViewModel.GetModelPath</c> — fuzzy matching nešel testovat bez
/// reálné Models složky a UI. Tady je čistý helper (jen filesystem I/O), který
/// jde testovat s temp adresářem.
///
/// <para>Strategie hledání (v pořadí):</para>
/// <list type="number">
/// <item>Přesný název souboru z <see cref="ModelRegistry"/> katalogu, pokud existuje.</item>
/// <item>Fuzzy: libovolný <c>*.gguf</c> v Models (rekurzivně), jehož název obsahuje
///   sanitizovaný model name (mezery/lomítka → podtržítka).</item>
/// <item>Fallback: <c>{modelsDir}/{katalogový-název-nebo-modelName.gguf}</c>
///   (nemusí existovat — caller si File.Exists ověří sám).</item>
/// </list>
/// </summary>
public static class ModelPathResolver
{
    /// <summary>
    /// Vrátí nejlepší match cesty k GGUF souboru pro daný <paramref name="modelName"/>.
    /// Negarantuje existenci — fallback větev vrací předpokládanou cestu.
    /// </summary>
    public static string Resolve(string modelsDir, string modelName)
    {
        var fileNameMap = ModelRegistry.AsFileNameDictionary();

        // 1) Přesný název z katalogu
        if (fileNameMap.TryGetValue(modelName, out var fileName))
        {
            var exact = Path.Combine(modelsDir, fileName);
            if (File.Exists(exact)) return exact;
        }

        // 2) Fuzzy sken Models složky
        if (Directory.Exists(modelsDir))
        {
            var safe = modelName.Replace(' ', '_').Replace('/', '_');
            try
            {
                var found = Directory
                    .EnumerateFiles(modelsDir, "*.gguf", SearchOption.AllDirectories)
                    .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f)
                        .Contains(safe, StringComparison.OrdinalIgnoreCase));
                if (found is not null) return found;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "ModelPathResolver: sken {Dir} selhal", modelsDir);
            }
        }

        // 3) Fallback — předpokládaná cesta (nemusí existovat)
        return Path.Combine(modelsDir,
            fileNameMap.GetValueOrDefault(modelName, $"{modelName}.gguf"));
    }
}
