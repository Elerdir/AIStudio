using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using AIStudio.Core.Enums;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Infrastructure.Services;

namespace AIStudio.App.ViewModels.Chat;

/// <summary>
/// ChatPageViewModel — část <b>Zprávy / LLM tah</b>: odeslání, regenerace, edit+regenerace,
/// streamovací pumpa, sestavení tahu, compact. Oddělené z hlavního souboru kvůli velikosti;
/// stejná partial třída, žádná změna chování.
/// </summary>
public partial class ChatPageViewModel
{
    // ── Send message ──────────────────────────────────────────────────────────

    private CancellationTokenSource? _sendCts;

    [RelayCommand]
    private async Task SendMessageAsync()
    {
        var text = InputText.Trim();
        if (string.IsNullOrEmpty(text) || SelectedConversation is null || IsSending)
        {
            Log.Information("SendMessage: bail-out empty={Empty} convNull={ConvNull} sending={Sending}",
                string.IsNullOrEmpty(text), SelectedConversation is null, IsSending);
            return;
        }

        Log.Information("SendMessage: ENTER conv={Id} model={Model} textLen={Len}",
            SelectedConversation.Id, SelectedConversation.SelectedModelName, text.Length);

        using var cts = new CancellationTokenSource();
        _sendCts  = cts;
        IsSending = true;
        InputText = string.Empty;

        var conv = SelectedConversation;

        // Přiložené obrázky vložíme jako markdown před text (zobrazí se v bublině).
        // První si podržíme jako primární referenci — pokud zpráva půjde do image
        // gen/editace/vision, použije se ta („přilož fotku + napiš co s ní udělat").
        var attachedImages    = AttachedImages.ToList();
        var attachedImagePath = attachedImages.FirstOrDefault() ?? string.Empty;
        var content = attachedImages.Count == 0
            ? text
            : string.Concat(attachedImages.Select(p => $"![obrázek]({p})\n")) + text;
        AttachedImages.Clear();

        var userMsg = new ChatMessage { Role = MessageRole.User, Content = content };
        conv.Messages.Add(userMsg);
        Log.Information("SendMessage: user msg added to UI, calling SaveMessageAsync (orderIndex={Idx})",
            conv.Messages.Count - 1);

        try
        {
            await _repo.SaveMessageAsync(userMsg.ToRecord(conv.Id, conv.Messages.Count - 1));
            Log.Information("SendMessage: user msg uložen úspěšně");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SendMessage: SaveMessageAsync (user) selhalo");
        }

        // Title se z prvního dotazu nepřepisuje — uživatel si ho přejmenovává ručně
        // (F2 nebo ikonkou tužky v hlavičce). UpdatedAt se aktualizuje po dokončení
        // streamu níž přes TrySaveConversationAsync.

        // ── Vyzbrojená editace (klik „Upravit" na obrázku) ────────────────────
        // Má nejvyšší prioritu — uživatel explicitně řekl „uprav TENHLE obrázek",
        // takže další zpráva je instrukce k editaci, ne nový obrázek/chat.
        var pendingEdit = PendingEditImagePath;
        if (!string.IsNullOrEmpty(pendingEdit) && _imageOrch is not null && File.Exists(pendingEdit))
        {
            PendingEditImagePath = null;   // spotřebováno
            await RunImageGenerationAsync(conv, text, ChatImageIntent.EditPreviousImage, pendingEdit, cts.Token);
            _sendCts  = null;
            IsSending = false;
            UpdateEstimatedTokens();
            return;
        }

        // ── Klasifikace: chat vs image gen ────────────────────────────────────
        // Před LLM voláním zkusíme, jestli uživatel nechce vygenerovat obrázek.
        // Pokud ano, větvíme do image flow (Comfy + galerie); jinak standardní
        // LLM stream. UI override (ImageMode) přebije auto detekci.
        // ── PuLID: příloha (obličej) + creation intent → generuj osobu v nové scéně ──
        // „vytvoř ji na pláži" → PuLID (identita bez tréninku), na rozdíl od editace
        // („udělej černobílou" → Kontext) nebo otázky („co je na fotce" → vision).
        if (!string.IsNullOrEmpty(attachedImagePath) && _imageOrch is not null && _imageIntent is not null
            && _imageIntent.IsPersonGeneration(text) && !_imageIntent.IsImageQuestion(text))
        {
            await RunPersonGenerationAsync(conv, text, attachedImagePath, cts.Token);
            _sendCts  = null;
            IsSending = false;
            UpdateEstimatedTokens();
            return;
        }

        var classifiedIntent = ClassifyIntent(conv, text, hasAttachment: !string.IsNullOrEmpty(attachedImagePath));
        if (classifiedIntent != ChatImageIntent.Chat && _imageOrch is not null)
        {
            await RunImageGenerationAsync(conv, text, classifiedIntent, attachedImagePath, cts.Token);
            _sendCts  = null;
            IsSending = false;
            UpdateEstimatedTokens();
            return;
        }

        // ── Vision: příloha + otázka (ne editace) → VLM „uvidí" obrázek a odpoví ──
        // classifiedIntent == Chat znamená, že to není žádost o generování/editaci.
        // Když je u toho přiložená fotka a máme vision službu, pošleme to do VLM
        // (popis / OCR / odpověď na otázku o obrázku) místo slepého textového chatu.
        if (!string.IsNullOrEmpty(attachedImagePath) && _vision is not null)
        {
            await RunVisionAsync(conv, text, attachedImagePath, cts.Token);
            _sendCts  = null;
            IsSending = false;
            UpdateEstimatedTokens();
            return;
        }

        var assistantMsg = new ChatMessage { Role = MessageRole.Assistant, Content = "", IsStreaming = true };
        conv.Messages.Add(assistantMsg);

        try
        {
            await _turn.EnsureModelLoadedAsync(conv.SelectedModelName, cts.Token);

            await StreamIntoMessageAsync(
                _turn.StreamReplyAsync(BuildTurnRequest(conv), cts.Token),
                assistantMsg,
                cts.Token);

            await TrySaveMessageAsync(assistantMsg, conv);
            _ = TrySaveConversationAsync(conv);

            // Auto-rename — fire-and-forget. Triggeruje se jen když má konverzace
            // default title („Chat N") a stačí počet zpráv. Vlastní helper si
            // ohlídá podmínky a tichounce skončí, když nejsou splněné.
            _ = MaybeAutoRenameAsync(conv);
        }
        catch (ModelNotAvailableException ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                assistantMsg.IsStreaming = false;
                assistantMsg.IsError     = true;
                assistantMsg.Content     = $"⚠️ Model **{ex.ModelName}** není stažen. Stáhni ho v sekci *Modely*.";
            });
        }
        catch (AIStudio.Core.Models.ModelLoadFailedException loadEx)
        {
            // LLamaSharp se nepovedlo načíst model (nepodporovaná architektura,
            // corrupted soubor, špatný formát…). Zobrazíme user-friendly hint
            // z ClassifyLoadError místo krytického native stack tracu.
            Log.Warning(loadEx, "Load model failed for {Name}", loadEx.ModelName);
            Dispatcher.UIThread.Post(() =>
            {
                assistantMsg.IsStreaming = false;
                assistantMsg.IsError     = true;
                assistantMsg.Content     = $"❌ **{loadEx.ModelName}** se nepodařilo načíst.\n\n{loadEx.Hint}";
            });
            await TrySaveMessageAsync(assistantMsg, conv);
        }
        catch (OperationCanceledException)
        {
            // IsStreaming=false už nahodil StreamIntoMessageAsync.finally
            if (!string.IsNullOrEmpty(assistantMsg.Content))
            {
                assistantMsg.Content += " *(přerušeno)*";
                await TrySaveMessageAsync(assistantMsg, conv);
            }
            else
            {
                conv.Messages.Remove(assistantMsg);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during chat generation");
            Dispatcher.UIThread.Post(() =>
            {
                assistantMsg.IsStreaming = false;
                assistantMsg.IsError     = true;
                assistantMsg.Content     = $"❌ Chyba: {ex.Message}";
            });
            await TrySaveMessageAsync(assistantMsg, conv);
        }
        finally
        {
            _sendCts  = null;
            IsSending = false;
            UpdateEstimatedTokens();
        }
    }

    // Image gen flow → ChatPageViewModel.ImageGen.cs (partial)

    [RelayCommand]
    private void StopGeneration() => _sendCts?.Cancel();

    // ── Regenerate last response ──────────────────────────────────────────────

    [RelayCommand]
    private async Task RegenerateLastResponseAsync()
    {
        var conv = SelectedConversation;
        if (conv is null || IsSending || conv.Messages.Count == 0) return;

        Log.Debug("RegenerateLastResponse: conv={Id} model={Model} msgCount={Count}",
            conv.Id, conv.SelectedModelName, conv.Messages.Count);

        var lastMsg = conv.Messages[^1];
        if (lastMsg.Role != MessageRole.Assistant) return;

        using var cts = new CancellationTokenSource();
        _sendCts  = cts;
        IsSending = true;

        // Vymaž obsah zprávy + nastav streaming flag
        Dispatcher.UIThread.Post(() => { lastMsg.Content = ""; lastMsg.IsStreaming = true; lastMsg.IsError = false; });

        try
        {
            await _turn.EnsureModelLoadedAsync(conv.SelectedModelName, cts.Token);

            await StreamIntoMessageAsync(
                _turn.StreamReplyAsync(BuildTurnRequest(conv), cts.Token),
                lastMsg,
                cts.Token);

            await TrySaveMessageAsync(lastMsg, conv);
            _ = TrySaveConversationAsync(conv);
        }
        catch (ModelNotAvailableException ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                lastMsg.IsStreaming = false;
                lastMsg.IsError     = true;
                lastMsg.Content     = $"⚠️ Model **{ex.ModelName}** není stažen. Stáhni ho v sekci *Modely*.";
            });
        }
        catch (OperationCanceledException)
        {
            // IsStreaming=false už nahodil StreamIntoMessageAsync.finally
            if (!string.IsNullOrEmpty(lastMsg.Content))
            {
                lastMsg.Content += " *(přerušeno)*";
                await TrySaveMessageAsync(lastMsg, conv);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during regeneration");
            Dispatcher.UIThread.Post(() =>
            {
                lastMsg.IsStreaming = false;
                lastMsg.IsError     = true;
                lastMsg.Content     = $"❌ Chyba při regeneraci: {ex.Message}";
            });
            await TrySaveMessageAsync(lastMsg, conv);
        }
        finally
        {
            _sendCts  = null;
            IsSending = false;
            UpdateEstimatedTokens();
        }
    }

    // ── Edit & Regenerate ─────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ConfirmEditAsync(ChatMessage msg)
    {
        var conv = SelectedConversation;
        if (conv is null || IsSending) return;

        var msgIdx = conv.Messages.IndexOf(msg);
        if (msgIdx < 0) return;

        // Ulož nový obsah zprávy
        msg.Content   = msg.EditContent;
        msg.IsEditing = false;

        try   { await _repo.SaveMessageAsync(msg.ToRecord(conv.Id, msgIdx)); }
        catch (Exception ex) { Log.Error(ex, "Failed to save edited message"); }

        // Odstraň všechny zprávy za editovanou (asistent + případné další)
        var toRemove = conv.Messages.Skip(msgIdx + 1).ToList();
        foreach (var m in toRemove)
        {
            m.IsEditing = false;   // #5 reset edit stavu před odstraněním
            conv.Messages.Remove(m);
        }

        try   { await _repo.DeleteMessagesFromIndexAsync(conv.Id, msgIdx + 1); }
        catch (Exception ex) { Log.Error(ex, "Failed to delete messages after edit"); }

        // Generuj novou odpověď
        using var cts = new CancellationTokenSource();
        _sendCts  = cts;
        IsSending = true;

        var assistantMsg = new ChatMessage { Role = MessageRole.Assistant, Content = "", IsStreaming = true };
        conv.Messages.Add(assistantMsg);

        try
        {
            await _turn.EnsureModelLoadedAsync(conv.SelectedModelName, cts.Token);

            await StreamIntoMessageAsync(
                _turn.StreamReplyAsync(BuildTurnRequest(conv), cts.Token),
                assistantMsg,
                cts.Token);

            await TrySaveMessageAsync(assistantMsg, conv);
            _ = TrySaveConversationAsync(conv);
        }
        catch (ModelNotAvailableException ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                assistantMsg.IsStreaming = false;
                assistantMsg.IsError     = true;
                assistantMsg.Content     = $"⚠️ Model **{ex.ModelName}** není stažen. Stáhni ho v sekci *Modely*.";
            });
        }
        catch (OperationCanceledException)
        {
            // IsStreaming=false už nahodil StreamIntoMessageAsync.finally
            if (!string.IsNullOrEmpty(assistantMsg.Content))
            {
                assistantMsg.Content += " *(přerušeno)*";
                await TrySaveMessageAsync(assistantMsg, conv);
            }
            else
            {
                conv.Messages.Remove(assistantMsg);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during edit+regenerate");
            Dispatcher.UIThread.Post(() =>
            {
                assistantMsg.IsStreaming = false;
                assistantMsg.IsError     = true;
                assistantMsg.Content     = $"❌ Chyba: {ex.Message}";
            });
            await TrySaveMessageAsync(assistantMsg, conv);
        }
        finally
        {
            _sendCts  = null;
            IsSending = false;
            UpdateEstimatedTokens();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Spotřebuje stream tokenů a do <paramref name="target"/>.Content je posílá
    /// v dávkách — nejvýše jednou za ~80 ms místo po každém tokenu. Bez throttlu
    /// by se MarkdownViewer re-parsoval 60-80×/s a UI thread by se zahltil
    /// ("Neodpovídá"). IsStreaming zůstává true až do konce → XAML zobrazuje
    /// laciný plain text místo drahého markdownu během streamu.
    ///
    /// Finální flush a IsStreaming=false běží ve finally — i při OperationCancelled
    /// nebo výjimce zůstane v bublině poslední konzistentní stav. Výjimky probublají
    /// dál, kde si je volající chytá vlastními catch bloky.
    /// </summary>
    private async Task StreamIntoMessageAsync(
        IAsyncEnumerable<string> tokens,
        ChatMessage              target,
        CancellationToken        ct)
    {
        const int FlushIntervalMs = 80;

        var sb        = new StringBuilder(target.Content);
        var lastTick  = Environment.TickCount64 - FlushIntervalMs; // první chunk flushne hned
        var startTick = Environment.TickCount64;
        var tokenCount = 0;

        Dispatcher.UIThread.Post(() => TokensPerSecond = 0);

        try
        {
            await foreach (var token in tokens.WithCancellation(ct))
            {
                sb.Append(token);
                tokenCount++;

                var now = Environment.TickCount64;
                if (now - lastTick >= FlushIntervalMs)
                {
                    var snapshot   = sb.ToString();
                    var elapsedSec = (now - startTick) / 1000.0;
                    var tps        = elapsedSec > 0.25 ? tokenCount / elapsedSec : 0;   // ustálí se po ~čtvrt s
                    Dispatcher.UIThread.Post(() => { target.Content = snapshot; TokensPerSecond = tps; });
                    lastTick = now;
                }
            }
        }
        finally
        {
            var final = sb.ToString();
            Dispatcher.UIThread.Post(() =>
            {
                target.Content     = final;
                target.IsStreaming = false;
                TokensPerSecond    = 0;   // ticker zmizí po dokončení (label visí jen při streamu)
            });
        }
    }

    /// <summary>
    /// Sestaví zadání LLM tahu pro <see cref="IChatTurnService"/>: systémový prompt, model,
    /// thinking flag, předchozí zprávy (bez posledního — placeholder asistenta) a parametry
    /// generování. Mapování UI typu (<see cref="ChatMessage"/>) na primitivy zůstává tady;
    /// vlastní načtení modelu + stream řeší služba.
    /// </summary>
    private ChatTurnRequest BuildTurnRequest(ConversationViewModel conv) =>
        new(conv.SystemPrompt, conv.SelectedModelName, conv.IsThinkingEnabled,
            BuildPriorMessages(conv), conv.MaxTokens, conv.Temperature);

    /// <summary>Předchozí zprávy konverzace (bez posledního placeholderu) jako primitivy role/obsah.</summary>
    private List<(string Role, string Content)> BuildPriorMessages(ConversationViewModel conv) =>
        conv.Messages
            .Take(conv.Messages.Count - 1)
            .Select(m => (RoleToString(m.Role), m.Content))
            .ToList();

    /// <summary>
    /// Cesta k GGUF souboru modelu. Používá image-gen flow (vlastní no-throw načtení modelu
    /// pro parsování intentu) — hlavní chat tah jde přes <see cref="IChatTurnService"/>.
    /// </summary>
    private string GetModelPath(string modelName)
    {
        var modelsDir = AIStudio.Core.Services.AppPaths.ResolveModelsDirectory(
            _settings.Settings.ModelsDirectory);
        return ModelPathResolver.Resolve(modelsDir, modelName);
    }

    private static string RoleToString(MessageRole role) => role switch
    {
        MessageRole.User      => "user",
        MessageRole.Assistant => "assistant",
        MessageRole.System    => "system",
        _                     => "user",
    };

    // ── Compact conversation (shrnutí starší historie) ────────────────────────
    //
    // Obdoba /compact v Claude Code: starší zprávy se LLM-em shrnou do jednoho
    // kompaktního summary (System zpráva s hlavičkou). Tím prudce klesne počet
    // tokenů posílaných do kontextu, ale model si „pamatuje" o čem byla řeč —
    // okno tak může zůstat otevřené mnohem déle, než narazí na limit kontextu.
    //
    // Pure logika (rozdělení zpráv, prompt, formátování) je v Core.ConversationCompactor;
    // tady jen orchestrace LLM streamu a přepisu zpráv v UI + DB.

    [RelayCommand]
    private async Task CompactConversationAsync()
    {
        var conv = SelectedConversation;
        if (conv is null || IsSending || IsCompacting) return;
        if (!AIStudio.Core.Services.ConversationCompactor.CanCompact(conv.Messages.Count)) return;

        Log.Information("Compact: start conv={Id} msgs={Count}", conv.Id, conv.Messages.Count);

        // Rozděl zprávy: starší k shrnutí, posledních pár doslovně.
        var snapshot = conv.Messages
            .Select(m => new AIStudio.Core.Services.ConversationCompactor.Message(
                RoleToString(m.Role), m.Content))
            .ToList();
        var (toSummarize, toKeep) =
            AIStudio.Core.Services.ConversationCompactor.Split(snapshot);
        if (toSummarize.Count == 0) return;

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        _sendCts     = cts;
        IsSending    = true;   // blokuje odesílání během compactu
        IsCompacting = true;
        ModelStatusText = "Shrnuji konverzaci…";

        try
        {
            await _turn.EnsureModelLoadedAsync(conv.SelectedModelName, cts.Token);

            var prompt = AIStudio.Core.Services.ConversationCompactor.BuildSummaryPrompt(toSummarize);

            var sb = new StringBuilder();
            await foreach (var token in _llama.ChatAsync(prompt, maxTokens: 1024, temperature: 0.3f, cts.Token))
                sb.Append(token);

            var summaryContent = AIStudio.Core.Services.ConversationCompactor.FormatSummary(sb.ToString());

            // Přepiš in-memory zprávy: summary (System) + ponechané doslovné.
            var keptMessages = conv.Messages.Skip(toSummarize.Count).ToList();
            var summaryMsg   = new ChatMessage { Role = MessageRole.System, Content = summaryContent };

            conv.Messages.Clear();
            conv.Messages.Add(summaryMsg);
            foreach (var m in keptMessages) conv.Messages.Add(m);

            // Přepiš i DB: smaž vše a ulož znovu s novými order indexy.
            try
            {
                await _repo.DeleteMessagesFromIndexAsync(conv.Id, 0);
                for (var i = 0; i < conv.Messages.Count; i++)
                    await _repo.SaveMessageAsync(conv.Messages[i].ToRecord(conv.Id, i));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Compact: přepis zpráv v DB selhal pro {Id}", conv.Id);
            }

            _ = TrySaveConversationAsync(conv);
            Log.Information("Compact: hotovo conv={Id}, {Old} → {New} zpráv",
                conv.Id, snapshot.Count, conv.Messages.Count);
        }
        catch (ModelNotAvailableException)
        {
            Log.Warning("Compact: model {Model} není dostupný", conv.SelectedModelName);
        }
        catch (OperationCanceledException)
        {
            Log.Information("Compact: zrušeno / timeout");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Compact: selhalo");
        }
        finally
        {
            _sendCts     = null;
            IsSending    = false;
            IsCompacting = false;
            ModelStatusText = string.Empty;
            UpdateEstimatedTokens();
        }
    }

}
