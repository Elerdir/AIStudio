using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using AIStudio.App.Views.Chat;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Core.Services;
using AIStudio.Infrastructure.Services;

namespace AIStudio.App.ViewModels.Chat;

public enum MessageRole { User, Assistant, System }

public partial class ChatMessage : ObservableObject
{
    public string      Id        { get; init; } = Guid.NewGuid().ToString();
    public MessageRole Role      { get; init; }
    public DateTime    Timestamp { get; init; } = DateTime.UtcNow;   // UTC, konvertuj při zobrazení

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenHint))]
    private string _content = string.Empty;

    /// <summary>Hrubý odhad počtu tokenů zprávy pro zobrazení v UI.</summary>
    public string TokenHint
    {
        get
        {
            var t = AIStudio.Core.Services.TokenEstimator.EstimateText(Content);
            return t > 0 ? $"~{t}t" : "";
        }
    }

    // ── Zobrazení času ────────────────────────────────────────────────────────

    /// <summary>Formátovaný čas zprávy v lokálním čase. Dnes = HH:mm, starší = d. M. HH:mm.</summary>
    public string TimeLabel
    {
        get
        {
            var local = Timestamp.ToLocalTime();
            return local.Date == DateTime.Today
                ? local.ToString("HH:mm")
                : local.ToString("d. M. HH:mm");
        }
    }

    // ── Streaming / error state ───────────────────────────────────────────────

    /// <summary>True dokud nedorazí první token — zobrazí animované tečky místo prázdné bubliny.</summary>
    [ObservableProperty] private bool _isStreaming;

    /// <summary>True pokud generování skončilo chybou — zobrazí tlačítko Zkusit znovu.</summary>
    [ObservableProperty] private bool _isError;

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
    /// True pokud tahle zpráva nese obrázek (hotový nebo se generuje). UI
    /// podle toho přepíná mezi text bublinou a image bublinou.
    /// </summary>
    public bool IsImageMessage => !string.IsNullOrEmpty(ImagePath)
                                  || IsImageGenerating
                                  || IsSearchingForUpgrade
                                  || IsAwaitingUpgradeChoice
                                  || IsDownloadingUpgradeModel;

    /// <summary>
    /// True dokud recommender hledá online lepší model. UI ukáže "Hledám
    /// lepší model online…" placeholder, aby uživatel netipoval kolik to
    /// trvá. Toggluje se v PromptUpgradeAsync flow.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImageMessage))]
    private bool _isSearchingForUpgrade;

    // ── Model upgrade flow (chat → image gen recommender) ────────────────────
    //
    // Orchestrátor může před generováním navrhnout, že má lepší (nesažený)
    // model. UI to ukáže jako inline panel v bublině — uživatel klikne na
    // Použít stažené nebo Stáhnout lepší, a callback ho oznámí orchestrátoru.
    //
    // Toky:
    //   1) Orchestrátor invokuje askForUpgrade → vytvoří TCS, nastaví
    //      PendingUpgradeOffer + IsAwaitingUpgradeChoice = true.
    //   2) Uživatel klikne tlačítko → command nastaví UpgradeChoice → TCS dokončí.
    //   3) Pokud DownloadBetter → IsDownloadingUpgradeModel = true,
    //      progress updaty přes UpdateDownloadStatus().

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImageMessage))]
    [NotifyPropertyChangedFor(nameof(HasPendingUpgradeOffer))]
    private ModelUpgradeOffer? _pendingUpgradeOffer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImageMessage))]
    private bool _isAwaitingUpgradeChoice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImageMessage))]
    private bool _isDownloadingUpgradeModel;

    [ObservableProperty] private int    _upgradeDownloadPercent;
    [ObservableProperty] private string _upgradeDownloadStatusLabel = string.Empty;

    /// <summary>
    /// UI checkbox "Už mi to nenavrhuj pro tento typ". Pokud zaškrtnuto a uživatel
    /// klikne Použít stažený → kind se persistuje do <c>AppSettings.IgnoredImageUpgradeKinds</c>
    /// (event vystaví ChatPageViewModel přes <c>UpgradeDismissalRequested</c>).
    /// </summary>
    [ObservableProperty] private bool _dontAskAgainForThisKind;

    /// <summary>
    /// True pokud nabídka je z Civitai a uživatel nemá nastavený Civitai API
    /// klíč v Settings. Download by mohl selhat s 401 / rate limit. UI ukáže
    /// info tip "💡 Pro Civitai modely doporučujeme API klíč v Nastavení".
    /// Nastavuje ChatPageViewModel přes PromptUpgradeAsync.
    /// </summary>
    [ObservableProperty] private bool _civitaiKeyWarningVisible;

    public bool HasPendingUpgradeOffer => PendingUpgradeOffer is not null;

    /// <summary>
    /// Notifikuje listenera (ChatPageViewModel), že uživatel zvolil "Použít stažený"
    /// + zaškrtl "už mi to nenabízej". Argument = string reprezentace <c>ImageKind</c>
    /// (pro zápis do <c>AppSettings.IgnoredImageUpgradeKinds</c>).
    /// </summary>
    public event Action<string>? UpgradeDismissalRequested;

    /// <summary>Lidsky čitelná velikost pro UI — "6.8 GB".</summary>
    public string UpgradeOfferSizeLabel => PendingUpgradeOffer is null
        ? ""
        : ByteFormatter.Format(PendingUpgradeOffer.SizeBytes);

    /// <summary>TCS, který orchestrátor čeká — dokončí se po user kliku.</summary>
    private TaskCompletionSource<UpgradeChoice>? _upgradeChoiceTcs;

    /// <summary>
    /// Kind label (string ImageKind enumu) aktuální nabídky — uloženo
    /// pro RejectUpgrade, který emit UpgradeDismissalRequested s kindem.
    /// </summary>
    private string? _currentKindLabel;

    /// <summary>
    /// Vystaví nabídku v UI a čeká na uživatelovo rozhodnutí. Volá se z
    /// callbacku, který orchestrátor dostane v GenerateAsync. Po vyřízení
    /// uklízí stav (IsAwaitingUpgradeChoice = false, offer = null).
    /// </summary>
    /// <param name="kindLabel">String reprezentace ImageKind enumu — pro
    /// případnou perzistenci do AppSettings.IgnoredImageUpgradeKinds.</param>
    /// <param name="civitaiKeyMissing">True pokud nabídka je z Civitai a uživatel
    /// nemá API klíč v Settings — UI ukáže tip.</param>
    public Task<UpgradeChoice> PromptUpgradeAsync(
        ModelUpgradeOffer offer,
        string            kindLabel,
        bool              civitaiKeyMissing,
        CancellationToken ct)
    {
        _currentKindLabel = kindLabel;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            PendingUpgradeOffer       = offer;
            IsAwaitingUpgradeChoice   = true;
            DontAskAgainForThisKind   = false;  // reset z předchozí nabídky
            CivitaiKeyWarningVisible  = civitaiKeyMissing && IsCivitaiOffer(offer);
            OnPropertyChanged(nameof(UpgradeOfferSizeLabel));
        });

        _upgradeChoiceTcs = new TaskCompletionSource<UpgradeChoice>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Pokud uživatel zruší celou věc Stopem, propagujeme jako Cancel
        ct.Register(() => _upgradeChoiceTcs?.TrySetResult(UpgradeChoice.Cancel));

        return _upgradeChoiceTcs.Task;
    }

    [RelayCommand]
    private void AcceptUpgrade()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => IsAwaitingUpgradeChoice = false);
        _upgradeChoiceTcs?.TrySetResult(UpgradeChoice.DownloadBetter);
    }

    [RelayCommand]
    private void RejectUpgrade()
    {
        // Pokud uživatel zaškrtl "už mi to nenabízej", emit event s kindem
        // — ChatPageViewModel ho zapíše do AppSettings.IgnoredImageUpgradeKinds.
        // Kind si vytáhneme z PendingUpgradeOffer.Id... actually nemáme tam přímo
        // kind. Místo toho dispatcher dostane signál a sám z aktivního intentu
        // vytáhne kind — to už zařídí ChatPageViewModel přes _imageOrch context.
        // Pro jednoduchost emit jen "true/false" signál — VM si kind dohledá.
        if (DontAskAgainForThisKind)
            UpgradeDismissalRequested?.Invoke(_currentKindLabel ?? string.Empty);

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsAwaitingUpgradeChoice = false;
            PendingUpgradeOffer     = null;
            DontAskAgainForThisKind = false;
            OnPropertyChanged(nameof(UpgradeOfferSizeLabel));
        });
        _upgradeChoiceTcs?.TrySetResult(UpgradeChoice.UseLocal);
    }

    /// <summary>
    /// Push update progress z orchestrátoru během downloadu — vytvoří label
    /// "FLUX Schnell · 45 % · 12 MB/s · 2.2 GB / 4.9 GB".
    /// </summary>
    public void UpdateDownloadStatus(DownloadStatusUpdate update)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Pokud orchestrátor začal stahovat, vystoupíme z awaiting (panel se přepne na progress)
            if (!IsDownloadingUpgradeModel)
            {
                IsDownloadingUpgradeModel = true;
                IsAwaitingUpgradeChoice   = false;
            }
            UpgradeDownloadPercent = update.Percent;
            UpgradeDownloadStatusLabel = string.Format(
                "{0} · {1} % · {2:F1} MB/s · {3} / {4}",
                update.ModelName,
                update.Percent,
                update.MegabytesPerSecond,
                ByteFormatter.Format(update.BytesDone),
                ByteFormatter.Format(update.BytesTotal));
        });
    }

    /// <summary>Reset upgrade state po dokončení / chybě (volá orchestrátor).</summary>
    public void ClearUpgradeState()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsDownloadingUpgradeModel = false;
            IsAwaitingUpgradeChoice   = false;
            PendingUpgradeOffer       = null;
            OnPropertyChanged(nameof(UpgradeOfferSizeLabel));
        });
    }

    /// <summary>
    /// Detekuje, jestli nabídka je z Civitai — podle URL nebo ID prefixu
    /// (live-Civitai-…). Pro warning o chybějícím API klíči.
    /// </summary>
    private static bool IsCivitaiOffer(ModelUpgradeOffer offer)
    {
        return offer.DownloadUrl.Contains("civitai.com", StringComparison.OrdinalIgnoreCase)
            || offer.Id.StartsWith("live-Civitai-",     StringComparison.OrdinalIgnoreCase);
    }

    // ── Edit state ────────────────────────────────────────────────────────────

    [ObservableProperty] private bool   _isEditing;
    [ObservableProperty] private string _editContent = string.Empty;

    [RelayCommand]
    private void BeginEdit()
    {
        EditContent = Content;
        IsEditing   = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
    }

    // ── Clipboard ─────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isCopied;

    [RelayCommand]
    private async Task CopyContentAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime
            { MainWindow: { } win }) return;

        var clipboard = TopLevel.GetTopLevel(win)?.Clipboard;
        if (clipboard is null) return;

        await clipboard.SetTextAsync(Content);

        // Krátká vizuální zpětná vazba ✓
        IsCopied = true;
        await Task.Delay(1500);
        IsCopied = false;
    }

    // ── Image akce (jen pro IsImageMessage) ───────────────────────────────────

    /// <summary>
    /// Otevře plný náhled obrázku v samostatném okně. Nedělá nic, pokud zpráva
    /// nenese cestu nebo soubor neexistuje (uživatel ho mohl smazat).
    /// </summary>
    [RelayCommand]
    private void OpenImageZoom()
    {
        if (string.IsNullOrEmpty(ImagePath) || !File.Exists(ImagePath)) return;

        try
        {
            var win = new ImageZoomWindow();
            win.Load(ImagePath);

            // Owner = MainWindow — aby zoom okno bylo modal-on-top a nezmizelo za hlavním
            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime
                { MainWindow: { } main })
            {
                win.Show(main);
            }
            else
            {
                win.Show();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ChatMessage: OpenImageZoom selhal");
        }
    }

    /// <summary>
    /// Vyvolá Save As dialog a zkopíruje obrázek na uživatelem zvolené místo.
    /// Soubor v gallery zůstává — kopírujeme jen kopii.
    /// </summary>
    [RelayCommand]
    private async Task SaveImageAsAsync()
    {
        if (string.IsNullOrEmpty(ImagePath) || !File.Exists(ImagePath)) return;
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime
            { MainWindow: { } main }) return;

        var sp = TopLevel.GetTopLevel(main)?.StorageProvider;
        if (sp is null) return;

        try
        {
            var ext = Path.GetExtension(ImagePath).TrimStart('.').ToLowerInvariant();
            if (string.IsNullOrEmpty(ext)) ext = "png";

            var picked = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title             = "Uložit obrázek jako…",
                SuggestedFileName = Path.GetFileNameWithoutExtension(ImagePath),
                DefaultExtension  = ext,
                FileTypeChoices   = new[]
                {
                    new FilePickerFileType($"Obrázek ({ext.ToUpperInvariant()})")
                    {
                        Patterns = new[] { $"*.{ext}" }
                    }
                }
            });

            if (picked is null) return;

            await using var input  = File.OpenRead(ImagePath);
            await using var output = await picked.OpenWriteAsync();
            await input.CopyToAsync(output);

            Log.Information("ChatMessage: obrázek uložen jako {Name}", picked.Name);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ChatMessage: SaveImageAs selhal");
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

    // ── DB mapování ───────────────────────────────────────────────────────────

    public static ChatMessage FromRecord(MessageRecord r)
    {
        var role = r.Role.ToLowerInvariant() switch
        {
            "assistant" => MessageRole.Assistant,
            "system"    => MessageRole.System,
            _           => MessageRole.User
        };

        return new ChatMessage
        {
            Id                 = r.Id,
            Role               = role,
            Timestamp          = r.Timestamp,
            Content            = r.Content,
            ImagePath          = r.ImagePath,
            ImageReferencePath = r.ImageReferencePath,
            // Pokud po load existuje ImagePath ale soubor zmizel (uživatel ho smazal
            // mimo aplikaci), UI ukáže "obrázek nenalezen". Stav IsImageGenerating
            // nikdy nenačítáme z DB — v okamžiku načtení už nic neběží.
            // Obnov příznak chyby podle obsahu (pro zprávy uložené v předchozích sezeních)
            IsError            = r.Content.StartsWith("❌") || r.Content.StartsWith("⚠️"),
        };
    }

    public MessageRecord ToRecord(string conversationId, int orderIndex) => new(
        Id,
        conversationId,
        Role.ToString().ToLowerInvariant(),
        Content,
        Timestamp,
        orderIndex,
        ImagePath,
        ImageReferencePath);
}
