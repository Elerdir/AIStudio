namespace AIStudio.Core.Services;

/// <summary>
/// Mapuje typ modelu (z Civitai API, např. „LORA"/„Checkpoint"/„VAE") na ComfyUI
/// podsložku v Models adresáři. Důvod: LoRA musí ležet v <c>loras/</c>, jinak ji
/// AI Studio LoRA knihovna (která skenuje <c>loras/</c>) ani ComfyUI LoRA picker
/// nenajdou. Stejně tak VAE/ControlNet/embeddings mají své složky.
///
/// <para>Checkpointy a chat GGUF zůstávají v rootu (<c>""</c>) — tam je
/// <c>extra_model_paths.yaml</c> i LlamaService vidí a měnit to by zbytečně
/// přesouvalo fungující soubory.</para>
/// </summary>
public static class ModelFolders
{
    public const string Loras         = "loras";
    public const string Vae           = "vae";
    public const string Controlnet    = "controlnet";
    public const string Embeddings    = "embeddings";
    public const string UpscaleModels = "upscale_models";

    /// <summary>
    /// Vrátí podsložku pro daný typ modelu (relativní k Models adresáři), nebo
    /// prázdný string pro root (checkpoint / chat / neznámé). <paramref name="fileName"/>
    /// slouží jako záloha, když typ chybí (heuristika z názvu souboru).
    /// </summary>
    public static string ResolveSubfolder(string? modelType, string? fileName = null)
    {
        switch ((modelType ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "lora":
            case "locon":
            case "lycoris":
            case "dora":
                return Loras;
            case "textualinversion":
            case "embedding":
            case "embeddings":
                return Embeddings;
            case "vae":
                return Vae;
            case "controlnet":
                return Controlnet;
            case "upscaler":
            case "upscale":
                return UpscaleModels;
            case "checkpoint":
            case "":
                // Neznámý/checkpoint → zkus heuristiku z názvu (níže), jinak root.
                break;
            default:
                return string.Empty;   // Hypernetwork, Poses, … necháme v rootu
        }

        // Záloha: typ chybí → odhad z názvu souboru (jen spolehlivé signály).
        var fn = (fileName ?? string.Empty).ToLowerInvariant();
        if (fn.Contains("lora") || fn.Contains("locon") || fn.Contains("lycoris"))
            return Loras;

        return string.Empty;
    }
}
