using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using AIStudio.Infrastructure.Services;

namespace AIStudio.App.ViewModels.Lora;

/// <summary>
/// Jedna položka v seznamu LoRA adapterů. Drží relativní název (pro ComfyUI
/// LoraLoader, který používá forward-slash relativní cesty jako
/// <c>anime/style.safetensors</c>) a absolutní cestu na disk (pro file ops:
/// reveal v Exploreru, smazání).
///
/// <para>Velikost se počítá jednou při vytvoření přes <see cref="FileInfo.Length"/>
/// — soubor se v UI po prvním scanu už nešahá, dokud uživatel nestiskne Refresh.</para>
/// </summary>
public partial class LoraItemViewModel : ViewModelBase
{
    private readonly Func<LoraItemViewModel, Task> _onDeleteRequested;

    /// <summary>
    /// Relativní název pro ComfyUI — např. <c>anime/style.safetensors</c>.
    /// Obsahuje forward slashy, i když na Windows. Tohle je formát který
    /// odpovídá LoraLoader node v ComfyUI workflow JSON.
    /// </summary>
    public string Name { get; }

    /// <summary>Absolutní cesta na disku — pro file ops (delete, reveal).</summary>
    public string FilePath { get; }

    /// <summary>Velikost souboru v bajtech.</summary>
    public long SizeBytes { get; }

    /// <summary>Lidsky čitelný popis velikosti (např. „142 MB").</summary>
    public string SizeLabel { get; }

    /// <summary>Jen filename bez přípony — pro hlavní řádek karty.</summary>
    public string DisplayName { get; }

    /// <summary>Podsložka (relativní k loras root), pokud LoRA leží mimo root. Prázdné = root.</summary>
    public string SubfolderLabel { get; }

    /// <summary>Přípona (např. „.safetensors") — pro badge / barevné rozlišení.</summary>
    public string Extension { get; }

    [ObservableProperty] private bool _isDeleting;

    public LoraItemViewModel(string name, string filePath, long sizeBytes,
                             Func<LoraItemViewModel, Task> onDeleteRequested)
    {
        Name              = name;
        FilePath          = filePath;
        SizeBytes         = sizeBytes;
        SizeLabel         = FormatSize(sizeBytes);
        DisplayName       = Path.GetFileNameWithoutExtension(name);
        Extension         = Path.GetExtension(name).ToLowerInvariant();
        _onDeleteRequested = onDeleteRequested;

        // Forward slashy v Name nahrazujeme zpátky pro display path
        // (nehrázlivé pro UI, ale konzistentní s OS konvencí v labelu)
        var dir = Path.GetDirectoryName(name)?.Replace('\\', '/');
        SubfolderLabel = string.IsNullOrEmpty(dir) ? string.Empty : dir;
    }

    /// <summary>Otevře LoRA soubor v Průzkumníkovi/Finderu (s highlight).</summary>
    [RelayCommand]
    private void Reveal()
    {
        try { PlatformShell.Reveal(FilePath); }
        catch (Exception ex) { Log.Warning(ex, "LoraItem.Reveal '{Path}' selhalo", FilePath); }
    }

    /// <summary>Vyžádá smazání souboru z disku přes parent VM (musí potvrdit).</summary>
    [RelayCommand]
    private async Task DeleteAsync() => await _onDeleteRequested(this);

    /// <summary>Formátuje velikost na lidský label: B/KB/MB/GB.</summary>
    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024L                       => $"{bytes} B",
        < 1024L * 1024                => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024         => $"{bytes / (1024.0 * 1024):F1} MB",
        _                             => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };
}
