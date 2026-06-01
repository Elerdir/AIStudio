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
    private ChatImageIntent ClassifyIntent(ConversationViewModel conv, string userText, bool hasAttachment)
    {
        if (_imageIntent is null || _imageOrch is null)
            return ChatImageIntent.Chat;

        var lastHadImage = FindLastAssistantImage(conv) is not null;

        return ImageMode switch
        {
            // ForceChat = žádné image GEN. Příloha pak (přes routing v SendMessageAsync)
            // půjde do vision LLM („popovídej si o fotce"), ne do generování.
            ChatImageMode.ForceChat  => ChatImageIntent.Chat,
            ChatImageMode.ForceImage => (lastHadImage || hasAttachment)
                ? ChatImageIntent.EditPreviousImage
                : ChatImageIntent.GenerateImage,
            // Auto: u přílohy je default EDITACE (imperativ „udělej černobílou",
            // „přidej klobouk"…) — do vision (Chat) jdeme JEN u jasné otázky
            // („co je na fotce?", „popiš"). Bez přílohy rozhoduje běžný keyword
            // detektor (generování od nuly).
            _ /* Auto */ when hasAttachment =>
                _imageIntent.IsImageQuestion(userText)
                    ? ChatImageIntent.Chat
                    : ChatImageIntent.EditPreviousImage,
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
        string?               attachedImagePath,
        CancellationToken     ct)
    {
        if (_imageOrch is null) return;  // sanity check, klasifikátor by sem neměl pustit

        // Reference pro img2img — přednost má uživatelem přiložená fotka (📎),
        // ta je nejsilnější signál „pracuj s tímhle obrázkem". Když příloha není,
        // padáme na poslední vygenerovaný obrázek konverzace (follow-up edit).
        // null = txt2img od nuly.
        var referencePath =
            !string.IsNullOrEmpty(attachedImagePath) && File.Exists(attachedImagePath)
                ? attachedImagePath
                : intent == ChatImageIntent.EditPreviousImage
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

        // KRITICKÉ: SmartParser (ImageIntentParser) potřebuje chat LLM, aby přeložil
        // český popis do anglického promptu pro SDXL/FLUX. Diffusion modely (SD/SDXL/
        // FLUX) jsou trénované hlavně na EN datech a CZ promptu nerozumí — bez překladu
        // by uživatel napsal "nakresli psa na louce" a SDXL vygeneroval cokoliv jiného.
        //
        // Pokud LLM ještě není načtený (např. nová konverzace, image jako první zpráva),
        // pokusíme se ho teď načíst. Když selže (model nestažený, OOM, atd.), pojedeme
        // dál — parser vrátí Fallback intent a SDXL dostane český text. To je horší
        // než nic, ale aspoň aplikace nehavaruje.
        await TryEnsureChatLlmForImageParserAsync(conv, placeholder, ct);

        // ComfyUI progress 0–100 → bublina ukáže „Generuji obrázek… X %".
        var progress = new Progress<int>(p =>
            Dispatcher.UIThread.Post(() => placeholder.ImageGenProgress = p));

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

        // Invoke (ne Post) — musí doběhnout PŘED TrySaveMessageAsync, jinak by se
        // uložil prázdný placeholder a obrázek by po restartu zmizel (prázdná bublina).
        Dispatcher.UIThread.Invoke(() =>
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

    /// <summary>
    /// Best-effort načtení chat LLM před image gen, aby SmartParser mohl
    /// český popis přeložit do anglického SDXL promptu. Žádné exception
    /// neunikne — když model není stažený, nejde načíst, nebo cokoliv jiného
    /// selže, jen to zalogujeme a image gen pojede dál s tím že parser
    /// propadne na pass-through fallback.
    ///
    /// Důvod proč tu nepoužíváme <see cref="EnsureModelLoadedAsync"/>: ta hází
    /// <see cref="ModelNotAvailableException"/> / <see cref="ModelLoadFailedException"/>
    /// a píše do placeholderu chybové zprávy určené pro chat flow. Pro image flow
    /// chceme pokračovat i bez chat LLM (parser má fallback) a placeholder
    /// reservujeme pro image-gen specific stavy ("Hledám lepší model" / "Generuji").
    /// </summary>
    private async Task TryEnsureChatLlmForImageParserAsync(
        ConversationViewModel conv,
        ChatMessage           placeholder,
        CancellationToken     ct)
    {
        if (string.IsNullOrWhiteSpace(conv.SelectedModelName))
        {
            Log.Information("Image gen: žádný chat model vybraný, SmartParser pojede ve fallbacku");
            return;
        }

        if (_llama.IsLoaded && _llama.LoadedModelName == conv.SelectedModelName)
            return;  // už načtený, můžeme rovnou parsovat

        try
        {
            var modelPath = GetModelPath(conv.SelectedModelName);
            if (!File.Exists(modelPath))
            {
                Log.Information("Image gen: chat LLM '{Model}' není stažen, SmartParser pojede ve fallbacku " +
                                "(SDXL může vrátit halucinaci pro český prompt)",
                                conv.SelectedModelName);
                return;
            }

            // Krátký vizuální feedback — placeholder zatím nemá nic, dáme text že
            // model nahráváme. Po loadu se ho ChatImageOrchestrator přepíše na
            // "Hledám lepší model" / "Generuji obrázek".
            Dispatcher.UIThread.Post(() =>
                placeholder.Content = $"⏳ Načítám chat model **{conv.SelectedModelName}** pro analýzu promptu…");

            var gpuLayers   = _settings.Settings.UseGpu ? -1 : 0;
            var contextSize = _settings.Settings.ChatContextSize;
            Log.Information("Image gen: načítám chat LLM '{Model}' pro SmartParser", conv.SelectedModelName);

            await _llama.LoadModelAsync(modelPath, conv.SelectedModelName,
                                        gpuLayers: gpuLayers,
                                        contextSize: contextSize,
                                        ct: ct);

            // Po úspěšném loadu placeholder vyčistíme — orchestrátor si ho přepíše sám
            Dispatcher.UIThread.Post(() => placeholder.Content = "");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "Image gen: chat LLM nelze načíst, SmartParser pojede ve fallbacku " +
                            "(SDXL může vrátit halucinaci pro český prompt)");
            // Vrátíme placeholder do prázdného stavu — orchestrátor si ho přepíše
            Dispatcher.UIThread.Post(() => placeholder.Content = "");
        }
    }

    // ── PuLID — generování osoby z fotky obličeje (identita bez tréninku) ─────

    /// <summary>
    /// PuLID flow — uživatel přiložil fotku obličeje a chce osobu v nové scéně
    /// („vytvoř ji na pláži"). Při prvním použití se PuLID stack auto-instaluje
    /// (node + deps + model + antelopev2). Stav jde přes placeholder bublinu.
    /// </summary>
    private async Task RunPersonGenerationAsync(
        ConversationViewModel conv, string userText, string faceImagePath, CancellationToken ct)
    {
        if (_imageOrch is null) return;

        var placeholder = new ChatMessage
        {
            Role                  = MessageRole.Assistant,
            Content               = "",
            IsSearchingForUpgrade = true,
            ImageReferencePath    = faceImagePath,
        };
        conv.Messages.Add(placeholder);
        try { await _repo.SaveMessageAsync(placeholder.ToRecord(conv.Id, conv.Messages.Count - 1)); }
        catch (Exception ex) { Log.Warning(ex, "PuLID: uložení placeholderu selhalo"); }

        var downloadProgress = new Progress<DownloadStatusUpdate>(u => placeholder.UpdateDownloadStatus(u));
        var stageProgress = new Progress<ChatImageGenStage>(stage =>
            Dispatcher.UIThread.Post(() =>
            {
                switch (stage)
                {
                    case ChatImageGenStage.Recommending:
                        placeholder.IsSearchingForUpgrade = true;  placeholder.IsImageGenerating = false; break;
                    case ChatImageGenStage.Generating:
                        placeholder.IsSearchingForUpgrade = false; placeholder.IsImageGenerating = true;  break;
                }
            }));

        // KRITICKÉ: načti chat LLM, aby SmartParser přeložil český popis na anglický
        // prompt pro FLUX. Bez toho parser propadne na fallback a scéna („na pláži")
        // se ztratí — vznikne osoba, ale ne v požadovaném prostředí.
        await TryEnsureChatLlmForImageParserAsync(conv, placeholder, ct);

        var progress = new Progress<int>(p =>
            Dispatcher.UIThread.Post(() => placeholder.ImageGenProgress = p));

        var result = await _imageOrch.GeneratePersonAsync(
            userText, faceImagePath, progress: progress, ct,
            downloadProgress: downloadProgress, stageProgress: stageProgress);

        placeholder.ClearUpgradeState();
        // Invoke (ne Post) — musí doběhnout PŘED TrySaveMessageAsync, jinak by se
        // uložil prázdný placeholder (ImagePath/Content ještě nenastavené) a po
        // restartu by byla bublina prázdná.
        Dispatcher.UIThread.Invoke(() =>
        {
            placeholder.IsImageGenerating = false;
            if (result.Success && !string.IsNullOrEmpty(result.ImagePath))
            {
                placeholder.ImagePath = result.ImagePath;
                placeholder.Content   = $"_{result.ModelUsed}_ · {result.Reasoning}".Trim();
            }
            else
            {
                placeholder.IsImageFailed = true;
                placeholder.IsError       = true;
                placeholder.Content       = $"❌ {result.ErrorMessage ?? "Generování osoby selhalo"}";
            }
        });

        try { await TrySaveMessageAsync(placeholder, conv); }
        catch (Exception ex) { Log.Warning(ex, "PuLID: finální save selhalo"); }
        _ = TrySaveConversationAsync(conv);
        _ = MaybeAutoRenameAsync(conv);
    }

    // ── Vision LLM (Stage 3) — „popovídej si o přiloženém obrázku" ────────────

    /// <summary>
    /// Vision flow — uživatel přiložil obrázek a ptá se na něj (ne edituje).
    /// VLM model „vidí" obrázek a streamuje odpověď. Auto-stáhne VLM při prvním
    /// použití (~6 GB). Volá se z <c>SendMessageAsync</c>, když je příloha a intent
    /// je Chat (otázka), ne editace.
    /// </summary>
    private async Task RunVisionAsync(
        ConversationViewModel conv, string question, string imagePath, CancellationToken ct)
    {
        if (_vision is null) return;

        var assistantMsg = new ChatMessage { Role = MessageRole.Assistant, Content = "", IsStreaming = true };
        conv.Messages.Add(assistantMsg);

        var modelsDir = AIStudio.Core.Services.AppPaths.ResolveModelsDirectory(_settings.Settings.ModelsDirectory);

        try
        {
            // Auto-download VLM pokud chybí (uživatel zvolil „vše automaticky")
            if (!_vision.IsModelAvailable(modelsDir))
            {
                Dispatcher.UIThread.Post(() => assistantMsg.Content = "⏳ Stahuji vision model (poprvé, ~6 GB)…");
                var dl = new Progress<AIStudio.Core.Models.DownloadProgressInfo>(_ =>
                    Dispatcher.UIThread.Post(() => assistantMsg.Content = $"⏳ {_vision.DownloadStatusLine}"));
                await _vision.EnsureModelAsync(modelsDir, _settings.Settings.HuggingFaceToken, dl, ct);

                if (!_vision.IsModelAvailable(modelsDir))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        assistantMsg.IsStreaming = false;
                        assistantMsg.IsError     = true;
                        assistantMsg.Content     = "❌ Vision model se nepodařilo stáhnout. Zkus to znovu.";
                    });
                    await TrySaveMessageAsync(assistantMsg, conv);
                    return;
                }
                Dispatcher.UIThread.Post(() => assistantMsg.Content = "");
            }

            await StreamIntoMessageAsync(
                _vision.DescribeAsync(imagePath, question, modelsDir, ct: ct),
                assistantMsg, ct);

            await TrySaveMessageAsync(assistantMsg, conv);
            _ = TrySaveConversationAsync(conv);
            _ = MaybeAutoRenameAsync(conv);
        }
        catch (OperationCanceledException)
        {
            if (!string.IsNullOrEmpty(assistantMsg.Content))
            {
                assistantMsg.Content += " *(přerušeno)*";
                await TrySaveMessageAsync(assistantMsg, conv);
            }
            else conv.Messages.Remove(assistantMsg);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "RunVisionAsync: selhalo");
            Dispatcher.UIThread.Post(() =>
            {
                assistantMsg.IsStreaming = false;
                assistantMsg.IsError     = true;
                assistantMsg.Content     = $"❌ Analýza obrázku selhala: {ex.Message}";
            });
            await TrySaveMessageAsync(assistantMsg, conv);
        }
    }
}
