using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Core.Services;

namespace AIStudio.App.ViewModels.Chat;

/// <summary>
/// Model upgrade flow — chat → image gen recommender najde lepší nesažený
/// model, UI v bublině zobrazí nabídku, uživatel zvolí. Tento partial drží
/// state + commands pro celý flow, oddělené od základní text/image logiky.
///
/// <para>Toky:</para>
/// <list type="number">
/// <item>Orchestrátor invokuje askForUpgrade → <see cref="PromptUpgradeAsync"/>
///   nastaví <see cref="PendingUpgradeOffer"/> + <c>IsAwaitingUpgradeChoice</c>
///   a vrátí Task&lt;UpgradeChoice&gt;, který doplní user klik.</item>
/// <item>Uživatel klikne tlačítko → <see cref="AcceptUpgradeCommand"/> /
///   <see cref="RejectUpgradeCommand"/> dokončí TCS.</item>
/// <item>Pokud DownloadBetter → <see cref="IsDownloadingUpgradeModel"/> = true,
///   progress updaty přes <see cref="UpdateDownloadStatus"/>.</item>
/// </list>
/// </summary>
public partial class ChatMessage
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImageMessage))]
    [NotifyPropertyChangedFor(nameof(HasPendingUpgradeOffer))]
    private ModelUpgradeOffer? _pendingUpgradeOffer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImageMessage))]
    private bool _isAwaitingUpgradeChoice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImageMessage), nameof(IsImageProgressOnly))]
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
        // Pokud uživatel zaškrtl "už mi to nenabízej", emit event s kindem —
        // ChatPageViewModel ho zapíše do AppSettings.IgnoredImageUpgradeKinds.
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
}
