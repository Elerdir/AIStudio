namespace AIStudio.Core.Models;

/// <summary>
/// Hozeno když se model nepodaří načíst do LLamaSharp. Drží original exception
/// (přístupné přes <see cref="System.Exception.InnerException"/>) plus user-friendly
/// hint o pravděpodobné příčině.
///
/// <para>Důvody mohou být:</para>
/// <list type="bullet">
/// <item>GGUF s architekturou kterou aktuální LLamaSharp build nezná (nově
///   vyšlé modely typu Qwen3 Next, Llama 4)</item>
/// <item>Corrupted / partially downloaded soubor</item>
/// <item>Kvantizace, kterou nepodporuje native llama.cpp build (IQ varianty
///   občas vyžadují novější runtime)</item>
/// <item>Soubor je něco jiného než LLM GGUF (FLUX image model, embedding…)</item>
/// </list>
/// </summary>
public sealed class ModelLoadFailedException : Exception
{
    public string ModelName { get; }
    public string ModelPath { get; }
    public string Hint      { get; }

    public ModelLoadFailedException(string modelName, string modelPath, string hint, Exception inner)
        : base($"Model '{modelName}' se nepodařilo načíst: {hint}", inner)
    {
        ModelName = modelName;
        ModelPath = modelPath;
        Hint      = hint;
    }
}
