using AIStudio.Core.Models;

namespace AIStudio.Core.Services;

/// <summary>
/// Sestaví argumenty pro <c>sd-cli.exe</c> (stable-diffusion.cpp CLI) z
/// <see cref="NativeImageRequest"/>. Čistá funkce → testovatelná; vrací seznam argumentů
/// (bez shell quotingu — volající je předá přes <c>ProcessStartInfo.ArgumentList</c>).
///
/// <para>CLI argumenty jsou napříč verzemi sd.cpp <b>stabilní</b> (na rozdíl od C ABI), proto
/// volíme shell-out místo P/Invoke. Sampler se mapuje přes <see cref="NativeSamplerMap"/>.</para>
/// </summary>
public static class SdCliArgsBuilder
{
    /// <summary>
    /// Sestaví argumenty pro txt2img/img2img. <paramref name="outputPath"/> je cílový PNG
    /// (sd-cli zapíše rovnou tam). <paramref name="threads"/> = počet CPU vláken (≤0 = auto).
    /// </summary>
    public static List<string> Build(NativeImageRequest r, string outputPath, int threads = 0)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (string.IsNullOrWhiteSpace(r.ModelPath)) throw new ArgumentException("Chybí ModelPath.", nameof(r));
        if (string.IsNullOrWhiteSpace(outputPath))  throw new ArgumentException("Chybí výstupní cesta.", nameof(outputPath));

        var isImg2Img = !string.IsNullOrWhiteSpace(r.InitImagePath);

        // LoRA v sd.cpp: syntaxe v promptu <lora:filename:scale> + adresář přes --lora-model-dir.
        var prompt = r.Prompt ?? string.Empty;
        if (r.Loras is { Count: > 0 })
            foreach (var lora in r.Loras)
                prompt += $" <lora:{LoraName(lora.Path)}:{lora.Scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}>";

        var args = new List<string>
        {
            "-M", isImg2Img ? "img2img" : "txt2img",
            "-m", r.ModelPath,
            "-p", prompt,
            "-n", r.NegativePrompt ?? string.Empty,
            "-W", r.Width.ToString(),
            "-H", r.Height.ToString(),
            "--steps", r.Steps.ToString(),
            "--cfg-scale", r.CfgScale.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-s", r.Seed.ToString(),
            "--sampling-method", NativeSamplerMap.ToSdCpp(r.SamplerName),
            "-b", Math.Max(1, r.BatchCount).ToString(),
            "-o", outputPath,
        };

        if (!string.IsNullOrWhiteSpace(r.VaePath))
        {
            args.Add("--vae");
            args.Add(r.VaePath!);
        }

        if (isImg2Img)
        {
            args.Add("-i");
            args.Add(r.InitImagePath!);
            args.Add("--strength");
            args.Add(r.Denoise.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (r.Loras is { Count: > 0 })
        {
            // Normalizuj '\' → '/' před GetDirectoryName — jinak Windows cesta na Unixu vrátí
            // prázdno (backslash tam není oddělovač) a --lora-model-dir by chyběl.
            var dir = System.IO.Path.GetDirectoryName(r.Loras[0].Path.Replace('\\', '/'));
            if (!string.IsNullOrEmpty(dir))
            {
                args.Add("--lora-model-dir");
                args.Add(dir);
            }
        }

        if (threads > 0)
        {
            args.Add("-t");
            args.Add(threads.ToString());
        }

        return args;
    }

    /// <summary>LoRA název bez cesty a přípony (sd.cpp v promptu očekává jen jméno).</summary>
    private static string LoraName(string path) =>
        System.IO.Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));
}
