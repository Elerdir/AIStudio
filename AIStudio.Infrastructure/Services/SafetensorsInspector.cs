using System.Text;
using Serilog;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Přečte záhlaví .safetensors souboru a zjistí, které komponenty model obsahuje
/// (CLIP encoder, VAE, UNet/transformer). Detekce běží bez načtení celého modelu
/// do paměti — čte jen JSON hlavičku na začátku souboru (typicky 1–10 MB).
/// </summary>
public static class SafetensorsInspector
{
    /// <summary>
    /// Inspektuje .safetensors soubor a vrátí jaké komponenty obsahuje.
    /// Vrací <c>null</c> pokud soubor nelze přečíst nebo není validní safetensors.
    /// V případě chyby neblokuje generování — generování se pokusí spustit a ComfyUI
    /// případně vrátí chybu samo.
    /// </summary>
    public static SafetensorsComponents? Inspect(string filePath)
    {
        if (!File.Exists(filePath)) return null;
        if (!filePath.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            using var fs = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, useAsync: false);

            // Formát safetensors:
            //   [0..7]    uint64 little-endian — délka JSON hlavičky (N bajtů)
            //   [8..8+N]  UTF-8 JSON se jmény a tvary tensorů
            //   [8+N..]   raw tensor data (nás nezajímají)
            var lenBuf = new byte[8];
            if (fs.Read(lenBuf) < 8) return null;

            var headerLen = (long)BitConverter.ToUInt64(lenBuf, 0);

            // Sanity: záhlaví > 100 MB je podezřelé — nečteme
            if (headerLen <= 0 || headerLen > 100_000_000)
            {
                Log.Debug("SafetensorsInspector: nestandardní délka záhlaví {Len} v {File}", headerLen, filePath);
                return null;
            }

            var headerBuf = new byte[headerLen];
            var totalRead = 0;
            while (totalRead < (int)headerLen)
            {
                var chunk = fs.Read(headerBuf, totalRead, (int)headerLen - totalRead);
                if (chunk == 0) break;
                totalRead += chunk;
            }

            if (totalRead < (int)headerLen) return null;

            // Klíče tensorů jsou v JSON jako UTF-8 stringy — hledáme charakteristické prefixy.
            // Nemusíme parsovat celý JSON — stačí prohledat raw bajty (klíče jsou plain ASCII/UTF-8).
            // Tím se vyhneme alokaci velké JSON struktury v paměti.
            var header = Encoding.UTF8.GetString(headerBuf);

            // CLIP encoder — různé konvence pojmenování podle zdrojového frameworku:
            //   ComfyUI merged checkpoint:   "clip.transformer.*"
            //   SD/SDXL (ldm format):        "cond_stage_model.*"
            //   Hugging Face FLUX:           "text_encoders.*"
            //   Starší varianty:             "text_model.*"
            var hasClip = ContainsKeyPrefix(header,
                "\"clip.",
                "\"cond_stage_model.",
                "\"text_encoders.",
                "\"text_model.");

            // VAE — AE (autoencoder) pro FLUX / decoder pro SD
            //   FLUX:       "vae.*" nebo "ae.*" (záleží na exportu)
            //   SD/SDXL:    "first_stage_model.*"
            var hasVae = ContainsKeyPrefix(header,
                "\"vae.",
                "\"ae.",
                "\"first_stage_model.");

            // UNet / Transformer (difúzní model)
            //   SD 1.x / XL:  "model.diffusion_model.*"
            //   FLUX:         "model.diffusion_model.*" nebo "diffusion_model.*" nebo "transformer.*"
            var hasUnet = ContainsKeyPrefix(header,
                "\"model.diffusion_model.",
                "\"diffusion_model.",
                "\"transformer.");

            Log.Debug("SafetensorsInspector: {File} → CLIP={Clip}, VAE={Vae}, UNet={Unet}",
                      Path.GetFileName(filePath), hasClip, hasVae, hasUnet);

            return new SafetensorsComponents(hasClip, hasVae, hasUnet);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SafetensorsInspector: nelze přečíst {File}", filePath);
            return null; // na chybě neblokujeme — ComfyUI hlásí chybu samo
        }
    }

    /// <summary>Zkontroluje, zda JSON hlavička obsahuje alespoň jeden z hledaných prefixů klíče.</summary>
    private static bool ContainsKeyPrefix(string header, params string[] prefixes)
    {
        foreach (var prefix in prefixes)
            if (header.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Najde plnou cestu k modelu prohledáním standardních adresářů.
    /// Vrátí <c>null</c> pokud soubor není nalezen.
    /// </summary>
    public static string? FindModelPath(string modelFileName, string? modelsDirectory, string? comfyUiDir)
    {
        if (string.IsNullOrWhiteSpace(modelFileName)) return null;

        var candidates = new List<string>();

        // 1) Uživatelsky nastavená složka modelů
        if (!string.IsNullOrWhiteSpace(modelsDirectory))
        {
            candidates.Add(Path.Combine(modelsDirectory, modelFileName));
            candidates.Add(Path.Combine(modelsDirectory, "checkpoints", modelFileName));
        }

        // 2) Výchozí složka AIStudio
        var defaultDir = AIStudio.Core.Services.AppPaths.DefaultModelsDirectory;
        candidates.Add(Path.Combine(defaultDir, modelFileName));
        candidates.Add(Path.Combine(defaultDir, "checkpoints", modelFileName));

        // 3) ComfyUI vlastní models/checkpoints
        if (!string.IsNullOrWhiteSpace(comfyUiDir))
        {
            candidates.Add(Path.Combine(comfyUiDir, "models", "checkpoints", modelFileName));
            candidates.Add(Path.Combine(comfyUiDir, "models", "unet", modelFileName));
        }

        return candidates.FirstOrDefault(File.Exists);
    }
}

/// <summary>Výsledek inspekce .safetensors souboru — jaké komponenty model obsahuje.</summary>
/// <param name="HasClip">True pokud soubor obsahuje CLIP textový encoder.</param>
/// <param name="HasVae">True pokud soubor obsahuje VAE (encoder/decoder latentů).</param>
/// <param name="HasUnet">True pokud soubor obsahuje UNet / diffusion transformer.</param>
public record SafetensorsComponents(bool HasClip, bool HasVae, bool HasUnet)
{
    /// <summary>Model je kompletní checkpoint (má vše — UNet + CLIP + VAE).</summary>
    public bool IsComplete => HasClip && HasVae && HasUnet;

    /// <summary>Model je jen UNet/transformer bez textových encoderů a VAE.</summary>
    public bool IsUnetOnly => HasUnet && !HasClip && !HasVae;
}
