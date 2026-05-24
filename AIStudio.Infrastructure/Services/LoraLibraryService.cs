using AIStudio.Core.Interfaces;
using Serilog;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Kombinuje ComfyUI seznam LoRA (přes <see cref="IComfyService.GetLorasAsync"/>)
/// s lokálním skenováním <c>Models/lora/</c> a <c>Models/loras/</c>. Skenování
/// je idempotentní a synchronní (Directory.EnumerateFiles), takže ho lze
/// testovat bez síťových mocků.
/// </summary>
public sealed class LoraLibraryService : ILoraLibraryService
{
    /// <summary>Podporované přípony LoRA souborů.</summary>
    private static readonly string[] LoraExtensions = { "*.safetensors", "*.pt", "*.ckpt" };

    /// <summary>Subdir názvy, které ComfyUI používá pro LoRA modely.</summary>
    private static readonly string[] LoraSubdirs = { "lora", "loras" };

    private readonly ISettingsService _settings;
    private readonly IComfyService    _comfy;

    public LoraLibraryService(ISettingsService settings, IComfyService comfy)
    {
        _settings = settings;
        _comfy    = comfy;
    }

    public async Task<IReadOnlyList<string>> ListAllAsync(CancellationToken ct = default)
    {
        var combined = new List<string>();

        // 1) ComfyUI (autoritativní pokud běží — vidí to, co umí načíst)
        if (_comfy.IsRunning)
        {
            try
            {
                combined.AddRange(await _comfy.GetLorasAsync(ct));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "LoraLibraryService: ComfyUI GetLorasAsync selhalo, padám na lokální sken");
            }
        }

        // 2) Lokální sken — doplní soubory, které ComfyUI ještě nevidí
        //    (nový download během běhu, ComfyUI off-line atd.)
        var modelsRoot = ResolveModelsRoot();
        foreach (var name in ScanLocal(modelsRoot))
        {
            if (!combined.Contains(name, StringComparer.OrdinalIgnoreCase))
                combined.Add(name);
        }

        // Stabilní pořadí pro UI (bez něj by se ComboBox míchal mezi spuštěními)
        return combined.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<string> ScanLocal(string modelsRoot)
    {
        if (string.IsNullOrWhiteSpace(modelsRoot)) return Array.Empty<string>();

        var dirsToScan = LoraSubdirs
            .Select(sub => Path.Combine(modelsRoot, sub))
            .Where(Directory.Exists)
            .ToArray();

        if (dirsToScan.Length == 0) return Array.Empty<string>();

        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();

        try
        {
            foreach (var dir in dirsToScan)
            foreach (var ext in LoraExtensions)
            foreach (var path in Directory.EnumerateFiles(dir, ext, SearchOption.AllDirectories))
            {
                // ComfyUI LoraLoader očekává relativní cestu od kořene loras adresáře,
                // např. "anime/lora.safetensors" — ne jen "lora.safetensors".
                // Lomítka musí být dopředná (ComfyUI/Python konvence).
                var relativeName = Path.GetRelativePath(dir, path).Replace('\\', '/');
                if (seen.Add(relativeName))
                    found.Add(relativeName);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LoraLibraryService: ScanLocal({Root}) selhalo", modelsRoot);
        }

        return found;
    }

    /// <summary>Resolve Models adresáře — uživatelská cesta nebo default AppData.</summary>
    private string ResolveModelsRoot() =>
        AIStudio.Core.Services.AppPaths.ResolveModelsDirectory(_settings.Settings.ModelsDirectory);
}
