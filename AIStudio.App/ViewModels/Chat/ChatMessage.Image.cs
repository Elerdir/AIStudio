using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Infrastructure.Services;

namespace AIStudio.App.ViewModels.Chat;

/// <summary>
/// Image generation state + commands. Partial split z ChatMessage.cs —
/// chat → image gen feature přidala spoustu Image-specific properties
/// + commandů (zoom, save, open external), které logicky patří dohromady.
///
/// <para>IsImageMessage computed property je v tomto partial souboru, protože
/// agreguje flagy z více partial souborů (Image + Upgrade) — má sense držet
/// ji blízko image state.</para>
/// </summary>
public partial class ChatMessage
{
    // ── Image generation state ────────────────────────────────────────────────
    //
    // Tahle zpráva může reprezentovat vygenerovaný obrázek (assistant role,
    // chat → image gen flow). Pokud ImagePath != null, UI vykreslí <Image>
    // místo Markdownu; pokud IsImageGenerating == true, UI ukáže "Generuju
    // obrázek…" placeholder místo typing dots.
    //
    // ImageReferencePath je cesta k obrázku, který byl vstupem do img2img
    // follow-up generace (pokud uživatel řekl "udělej to v noci" na poslední
    // obrázek). Slouží hlavně pro audit / regenerování konverzace.

    /// <summary>Cesta k vygenerovanému obrázku (null = klasická text zpráva).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImageMessage))]
    private string? _imagePath;

    /// <summary>Reference obrázek pro img2img follow-up (null = txt2img od nuly).</summary>
    [ObservableProperty] private string? _imageReferencePath;

    /// <summary>True dokud běží Comfy generování — UI ukáže placeholder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImageMessage))]
    private bool _isImageGenerating;

    /// <summary>True pokud generování obrázku selhalo — UI ukáže Zkusit znovu.</summary>
    [ObservableProperty] private bool _isImageFailed;

    /// <summary>
    /// True dokud recommender hledá online lepší model. UI ukáže "Hledám
    /// lepší model online…" placeholder, aby uživatel netipoval kolik to
    /// trvá. Toggluje se v PromptUpgradeAsync flow.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImageMessage))]
    private bool _isSearchingForUpgrade;

    /// <summary>
    /// True pokud tahle zpráva nese obrázek (hotový nebo se generuje, nebo
    /// běží upgrade flow). UI podle toho přepíná mezi text bublinou a
    /// image bublinou. Agreguje flagy z Image + Upgrade partials.
    /// </summary>
    public bool IsImageMessage => !string.IsNullOrEmpty(ImagePath)
                                  || IsImageGenerating
                                  || IsSearchingForUpgrade
                                  || IsAwaitingUpgradeChoice
                                  || IsDownloadingUpgradeModel;

    // ── Image akce (jen pro IsImageMessage) ───────────────────────────────────

    /// <summary>
    /// Otevře plný náhled obrázku v samostatném okně přes <see cref="IDialogService"/>.
    /// Nedělá nic, pokud zpráva nenese cestu, soubor neexistuje, nebo dialog
    /// service ještě nebyla nastavena (cold start, edge case).
    /// </summary>
    [RelayCommand]
    private void OpenImageZoom()
    {
        if (string.IsNullOrEmpty(ImagePath) || !File.Exists(ImagePath)) return;
        DialogService?.ShowImagePreview(ImagePath);
    }

    /// <summary>
    /// Vyvolá Save As dialog a zkopíruje obrázek na uživatelem zvolené místo.
    /// Soubor v gallery zůstává — kopírujeme jen kopii.
    /// </summary>
    [RelayCommand]
    private async Task SaveImageAsAsync()
    {
        if (string.IsNullOrEmpty(ImagePath) || !File.Exists(ImagePath)) return;
        if (DialogService is null) return;

        var ext = Path.GetExtension(ImagePath).TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(ext)) ext = "png";

        var destPath = await DialogService.SaveFileAsync(
            title:             "Uložit obrázek jako…",
            suggestedFileName: Path.GetFileNameWithoutExtension(ImagePath),
            defaultExtension:  ext,
            filters: new[]
            {
                new FileFilter($"Obrázek ({ext.ToUpperInvariant()})", new[] { $"*.{ext}" })
            });

        if (string.IsNullOrEmpty(destPath)) return;

        try
        {
            await using var input  = File.OpenRead(ImagePath);
            await using var output = File.Create(destPath);
            await input.CopyToAsync(output);
            Log.Information("ChatMessage: obrázek uložen jako {Path}", destPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ChatMessage: SaveImageAs file copy selhalo");
        }
    }

    /// <summary>Otevře obrázek v systémové prohlížečce (asociace dle OS).</summary>
    [RelayCommand]
    private void OpenImageExternal()
    {
        if (string.IsNullOrEmpty(ImagePath) || !File.Exists(ImagePath)) return;
        try { PlatformShell.Open(ImagePath); }
        catch (Exception ex) { Log.Warning(ex, "ChatMessage: OpenImageExternal selhal"); }
    }
}
