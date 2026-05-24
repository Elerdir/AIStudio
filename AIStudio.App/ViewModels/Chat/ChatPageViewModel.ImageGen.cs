using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;

namespace AIStudio.App.ViewModels.Chat;

/// <summary>
/// Chat → image generation flow + UI image mode toggle. Partial split z hlavního
/// ChatPageViewModel.cs — image gen feature dorostla na ~280 řádků a má jasně
/// oddělené odpovědnosti od textového LLM streamu.
///
/// <para>Co tu je:</para>
/// <list type="bullet">
/// <item>ImageMode property + 🎨 toggle (Auto/ForceImage/ForceChat)</item>
/// <item><see cref="ClassifyIntent"/> — Chat / GenerateImage / EditPreviousImage</item>
/// <item><see cref="FindLastAssistantImage"/> — pro img2img follow-up</item>
/// <item><see cref="RunImageGenerationAsync"/> — celé orchestrátor flow s callback wiring</item>
/// </list>
/// </summary>
public partial class ChatPageViewModel
{
    // ── UI image mode toggle (🎨 v inputu) ────────────────────────────────────

    /// <summary>
    /// Override pro detekci image intentu z UI toggle ikonky 🎨:
    /// <list type="bullet">
    /// <item><c>Auto</c> — keyword-based detektor rozhodne (default)</item>
    /// <item><c>ForceImage</c> — každá zpráva půjde do image gen</item>
    /// <item><c>ForceChat</c> — image gen vypnut, vše do LLM</item>
    /// </list>
    /// Cycling tlačítko v ChatPageView ovládá <see cref="CycleImageModeCommand"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImageModeAuto))]
    [NotifyPropertyChangedFor(nameof(IsImageModeForceImage))]
    [NotifyPropertyChangedFor(nameof(IsImageModeForceChat))]
    [NotifyPropertyChangedFor(nameof(ImageModeTooltip))]
    private ChatImageMode _imageMode = ChatImageMode.Auto;

    public bool IsImageModeAuto       => ImageMode == ChatImageMode.Auto;
    public bool IsImageModeForceImage => ImageMode == ChatImageMode.ForceImage;
    public bool IsImageModeForceChat  => ImageMode == ChatImageMode.ForceChat;

    /// <summary>Tooltip pro toggle button — vysvětluje aktuální i další krok cyklu.</summary>
    public string ImageModeTooltip => ImageMode switch
    {
        ChatImageMode.ForceImage => "Režim: vždy obrázek\nKlik → vypnout image gen",
        ChatImageMode.ForceChat  => "Režim: vždy chat (image gen vypnut)\nKlik → automatická detekce",
        _                        => "Režim: automatická detekce\nKlik → vždy obrázek",
    };

    /// <summary>Cykluje Auto → ForceImage → ForceChat → Auto.</summary>
    [RelayCommand]
    private void CycleImageMode()
    {
        ImageMode = ImageMode switch
        {
            ChatImageMode.Auto       => ChatImageMode.ForceImage,
            ChatImageMode.ForceImage => ChatImageMode.ForceChat,
            _                        => ChatImageMode.Auto,
        };
    }

    // ── Intent classification + image gen orchestration ──────────────────────

    /// <summary>
    /// Rozhodne podle ImageMode + detektoru, jestli zpráva má jít do image gen
    /// nebo do LLM. Pokud nemáme detektor (DI neposkytl), vrací vždy Chat.
    ///
    /// <para>EditPreviousImage se vrátí jen pokud opravdu existuje předchozí
    /// assistant zpráva s obrázkem — jinak se downgradne na GenerateImage
    /// (kdyby detektor zachytil edit-keywords ale není co editovat).</para>
    /// </summary>
    private ChatImageIntent ClassifyIntent(ConversationViewModel conv, string userText)
    {
        if (_imageIntent is null || _imageOrch is null)
            return ChatImageIntent.Chat;

        var lastHadImage = FindLastAssistantImage(conv) is not null;

        return ImageMode switch
        {
            ChatImageMode.ForceChat  => ChatImageIntent.Chat,
            ChatImageMode.ForceImage => lastHadImage
                ? ChatImageIntent.EditPreviousImage
                : ChatImageIntent.GenerateImage,
            _ /* Auto */             => _imageIntent.Detect(userText, lastHadImage),
        };
    }

    /// <summary>
    /// Najde nejnovější assistantskou zprávu, která má vyplněný ImagePath
    /// (a soubor pořád existuje na disku). Slouží jako reference pro
    /// img2img follow-up generování.
    /// </summary>
    private static ChatMessage? FindLastAssistantImage(ConversationViewModel conv)
    {
        for (var i = conv.Messages.Count - 1; i >= 0; i--)
        {
            var m = conv.Messages[i];
            if (m.Role == MessageRole.Assistant &&
                !string.IsNullOrEmpty(m.ImagePath) &&
                File.Exists(m.ImagePath))
                return m;
        }
        return null;
    }

    /// <summary>
    /// Image gen variant of SendMessageAsync — předpokládá, že user message
    /// už je v conv.Messages (přidaná před voláním). Vytvoří placeholder
    /// assistant message, zavolá orchestrátor a aktualizuje placeholder
    /// s výsledkem (úspěch / chyba). Vše perzistuje do DB.
    /// </summary>
    private async Task RunImageGenerationAsync(
        ConversationViewModel conv,
        string                userText,
        ChatImageIntent       intent,
        CancellationToken     ct)
    {
        if (_imageOrch is null) return;  // sanity check, klasifikátor by sem neměl pustit

        // Reference pro img2img — null pokud GenerateImage
        var referencePath = intent == ChatImageIntent.EditPreviousImage
            ? FindLastAssistantImage(conv)?.ImagePath
            : null;

        // Placeholder startuje ve fázi Recommending — orchestrátor přepne na
        // Generating, jakmile recommender + případný download jsou hotové.
        var placeholder = new ChatMessage
        {
            Role                  = MessageRole.Assistant,
            Content               = "",
            IsSearchingForUpgrade = true,
            ImageReferencePath    = referencePath,
        };
        conv.Messages.Add(placeholder);

        // Persistujeme placeholder, ať se neztratí při crashi mezi orchestrátorem
        // a finální aktualizací. ImagePath je null, ImageReferencePath může být set.
        try { await _repo.SaveMessageAsync(placeholder.ToRecord(conv.Id, conv.Messages.Count - 1)); }
        catch (Exception ex) { Log.Warning(ex, "Image gen: ulození placeholder selhalo"); }

        Log.Information("Image gen: intent={Intent} ref={Ref}",
                        intent, referencePath ?? "(none)");

        var progress = new Progress<int>(_ => { /* zatím nevyužíváme — UI bublina má jen typing dots */ });

        // Callback pro upgrade nabídku — orchestrátor zavolá, pokud najde lepší
        // nesažený model. Vystavíme nabídku v placeholder bublině a čekáme na klik.
        // Kind si vytáhneme přímo z offer.Kind (předaný recommenderem).
        // CivitaiKeyMissing check — pokud user nemá nastavený API klíč a nabídka
        // je z Civitai, UI ukáže tip.
        Func<ModelUpgradeOffer, CancellationToken, Task<UpgradeChoice>> askForUpgrade =
            (offer, cancellationToken) =>
            {
                var civitaiKeyMissing = string.IsNullOrWhiteSpace(_settings.Settings.CivitaiApiKey);
                return placeholder.PromptUpgradeAsync(offer, offer.Kind.ToString(), civitaiKeyMissing, cancellationToken);
            };

        // Subscribuj se na "už mi to nenabízej" event — zapíše kind do settings.
        // Unsubscribe v finally větvi, ať placeholder nedrží referenci na VM.
        Action<string> onDismissalRequested = kindLabel =>
        {
            if (string.IsNullOrEmpty(kindLabel)) return;
            var settings = _settings.Settings;
            if (!settings.IgnoredImageUpgradeKinds.Contains(kindLabel))
            {
                settings.IgnoredImageUpgradeKinds.Add(kindLabel);
                _ = _settings.SaveAsync();
                Log.Information("Image gen: user dismissed upgrade prompts for kind={Kind}", kindLabel);
            }
        };
        placeholder.UpgradeDismissalRequested += onDismissalRequested;

        // Download progress (pokud uživatel zvolí DownloadBetter) — bublina ukáže
        // progress bar místo dialog panelu.
        var downloadProgress = new Progress<DownloadStatusUpdate>(u => placeholder.UpdateDownloadStatus(u));

        // Stage progress — bublina se přepne ze "Hledám lepší model" na "Generuji obrázek"
        var stageProgress = new Progress<ChatImageGenStage>(stage =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                switch (stage)
                {
                    case ChatImageGenStage.Recommending:
                        placeholder.IsSearchingForUpgrade = true;
                        placeholder.IsImageGenerating     = false;
                        break;
                    case ChatImageGenStage.Generating:
                        placeholder.IsSearchingForUpgrade = false;
                        placeholder.IsImageGenerating     = true;
                        break;
                }
            });
        });

        var result = await _imageOrch.GenerateAsync(
            userText, referencePath, progress, ct,
            askForUpgrade:    askForUpgrade,
            downloadProgress: downloadProgress,
            stageProgress:    stageProgress);

        // Po skončení (úspěch / chyba / cancel) vždy vyresetuj upgrade state,
        // ať tam nevisí ducha z předchozí nabídky + odpojí event handler.
        placeholder.ClearUpgradeState();
        placeholder.UpgradeDismissalRequested -= onDismissalRequested;

        Dispatcher.UIThread.Post(() =>
        {
            placeholder.IsImageGenerating = false;

            if (result.Success && !string.IsNullOrEmpty(result.ImagePath))
            {
                placeholder.ImagePath = result.ImagePath;
                // Krátký podtitul — model + reasoning od parseru, ne plný EN prompt
                placeholder.Content = $"_{result.ModelUsed}_ · {result.Reasoning}".Trim();
            }
            else
            {
                placeholder.IsImageFailed = true;
                placeholder.IsError       = true;
                placeholder.Content       = $"❌ {result.ErrorMessage ?? "Generování selhalo"}";
            }
        });

        try { await TrySaveMessageAsync(placeholder, conv); }
        catch (Exception ex) { Log.Warning(ex, "Image gen: finalní save selhalo"); }

        _ = TrySaveConversationAsync(conv);
        _ = MaybeAutoRenameAsync(conv);
    }
}
