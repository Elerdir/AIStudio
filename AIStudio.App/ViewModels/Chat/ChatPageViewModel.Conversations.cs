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
/// ChatPageViewModel — část <b>Správa konverzací</b>: editace názvu, nová/smazat, kopie,
/// export, vyčištění, pin, thinking mód, větvení. Oddělené z hlavního souboru kvůli velikosti;
/// stejná partial třída, žádná změna chování.
/// </summary>
public partial class ChatPageViewModel
{
    // ── Title edit commands ────────────────────────────────────────────────────

    [RelayCommand]
    private void BeginEditTitle()
    {
        if (SelectedConversation is null) return;
        EditingTitle    = SelectedConversation.Title;
        IsEditingTitle  = true;
    }

    [RelayCommand]
    private void ConfirmTitleEdit()
    {
        if (!IsEditingTitle) return;
        IsEditingTitle = false;

        if (SelectedConversation is null) return;
        var trimmed = EditingTitle.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;

        SelectedConversation.Title = trimmed;
        _ = TrySaveConversationAsync(SelectedConversation);
    }

    [RelayCommand]
    private void CancelTitleEdit()
    {
        IsEditingTitle = false;
    }

    // ── Conversation commands ──────────────────────────────────────────────────

    [RelayCommand]
    private void NewConversation()
    {
        // Priorita:
        //   1) právě načtený model (žádný unload nepotřeba — okamžitě použitelný)
        //   2) výchozí model z nastavení (pokud je stažený)
        //   3) první dostupný stažený model
        //   4) fallback na Llama 3.1 8B (nestažený)
        var settingsDefault = _settings.Settings.DefaultChatModelName;

        string defaultModel;
        if (_llama.IsLoaded
            && !string.IsNullOrEmpty(_llama.LoadedModelName)
            && AvailableModels.Contains(_llama.LoadedModelName))
        {
            defaultModel = _llama.LoadedModelName;
        }
        else if (!string.IsNullOrEmpty(settingsDefault) && AvailableModels.Contains(settingsDefault))
        {
            defaultModel = settingsDefault;
        }
        else
        {
            defaultModel = AvailableModels.Count > 0 ? AvailableModels[0] : "Llama 3.1 8B Instruct Q4_K_M";
        }

        var conv = new ConversationViewModel
        {
            Title             = $"Chat {Conversations.Count + 1}",
            SelectedModelName = defaultModel
        };
        _ = TrySaveConversationAsync(conv);
        Conversations.Insert(0, conv);
        ResortConversations();          // zachová pinned konverzace nahoře
        SelectedConversation = conv;
    }

    [RelayCommand]
    private void DeleteConversation(ConversationViewModel conv)
    {
        _ = TryDeleteConversationAsync(conv.Id);

        var idx = Conversations.IndexOf(conv);
        Conversations.Remove(conv);

        if (SelectedConversation == conv)
            SelectedConversation = Conversations.ElementAtOrDefault(Math.Max(0, idx - 1));

        // Po smazání posledního chatu už NEvytváříme automaticky náhradní —
        // chat area zobrazí empty state s pokynem kliknout na „+ Nový chat".
    }

    // ── Copy whole conversation to clipboard ──────────────────────────────────

    /// <summary>Krátký vizuální feedback po stisknutí „Kopírovat celou" — ikona ✓ na 1.5 s.</summary>
    [ObservableProperty] private bool _isConversationCopied;

    [RelayCommand]
    private async Task CopyConversationAsync()
    {
        var conv = SelectedConversation;
        if (conv is null || conv.Messages.Count == 0) return;

        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } win }) return;

        var clipboard = Avalonia.Controls.TopLevel.GetTopLevel(win)?.Clipboard;
        if (clipboard is null) return;

        // Pure formátování v Core.ConversationExporter (testovatelné). Pro export
        // do MD/TXT existuje samostatný command — tohle je rychlá clipboard varianta.
        var text = AIStudio.Core.Services.ConversationExporter.ToClipboardText(ToExportMessages(conv));

        try
        {
            await clipboard.SetTextAsync(text);
            IsConversationCopied = true;
            await Task.Delay(1500);
            IsConversationCopied = false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CopyConversation: clipboard SetTextAsync selhalo");
        }
    }

    // ── Export conversation ───────────────────────────────────────────────────

    [RelayCommand]
    private async Task ExportConversationAsync()
    {
        var conv = SelectedConversation;
        if (conv is null || conv.Messages.Count == 0) return;

        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } win })
            return;

        var result = await win.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Exportovat konverzaci",
            SuggestedFileName = AIStudio.Core.Services.ConversationExporter.SanitizeFileName(conv.Title),
            DefaultExtension  = "md",
            FileTypeChoices =
            [
                new FilePickerFileType("Markdown") { Patterns = ["*.md"] },
                new FilePickerFileType("Prostý text") { Patterns = ["*.txt"] },
            ],
        });

        if (result is null) return;

        var path     = result.Path.LocalPath;
        var isMd     = path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
        var messages = ToExportMessages(conv);
        var now      = DateTime.Now;

        var content = isMd
            ? AIStudio.Core.Services.ConversationExporter.ToMarkdown(
                conv.Title, conv.SelectedModelName, conv.SystemPrompt, now, messages)
            : AIStudio.Core.Services.ConversationExporter.ToPlainText(
                conv.Title, conv.SelectedModelName, conv.SystemPrompt, now, messages);

        await File.WriteAllTextAsync(path, content, Encoding.UTF8);
    }

    /// <summary>Mapuje UI ChatMessage na primitivní ExportMessage pro Core exportér.</summary>
    private static List<AIStudio.Core.Services.ExportMessage> ToExportMessages(ConversationViewModel conv) =>
        conv.Messages
            .Select(m => new AIStudio.Core.Services.ExportMessage(
                RoleToString(m.Role), m.Content, m.Timestamp))
            .ToList();

    // ── Clear conversation ────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ClearConversationAsync()
    {
        var conv = SelectedConversation;
        if (conv is null || IsSending) return;

        conv.Messages.Clear();
        try   { await _repo.DeleteMessagesFromIndexAsync(conv.Id, 0); }
        catch (Exception ex) { Log.Error(ex, "Failed to clear messages for {Id}", conv.Id); }

        _ = TrySaveConversationAsync(conv);
        UpdateEstimatedTokens();
    }

    // ── Pin conversation ──────────────────────────────────────────────────────

    [RelayCommand]
    private void TogglePinConversation(ConversationViewModel conv)
    {
        conv.IsPinned = !conv.IsPinned;
        ResortConversations();
        _ = TrySaveConversationAsync(conv);
    }

    private void ResortConversations()
    {
        var sorted = Conversations
            .OrderByDescending(c => c.IsPinned)
            .ThenByDescending(c => c.UpdatedAt)
            .ToList();

        for (var i = 0; i < sorted.Count; i++)
        {
            var cur = Conversations.IndexOf(sorted[i]);
            if (cur != i) Conversations.Move(cur, i);
        }

        UpdateFilteredConversations();
    }

    // ── Thinking mode toggle (Qwen3) ──────────────────────────────────────────

    [RelayCommand]
    private void ToggleThinkingMode()
    {
        if (SelectedConversation is null) return;
        SelectedConversation.IsThinkingEnabled = !SelectedConversation.IsThinkingEnabled;
    }

    // ── Branch conversation ───────────────────────────────────────────────────

    [RelayCommand]
    private async Task BranchConversationAsync(ChatMessage fromMessage)
    {
        var conv = SelectedConversation;
        if (conv is null) return;

        var idx = conv.Messages.IndexOf(fromMessage);
        if (idx < 0) return;

        var branch = new ConversationViewModel
        {
            Title             = $"{conv.Title} (větev)",
            SelectedModelName = conv.SelectedModelName,
            MaxTokens         = conv.MaxTokens,
            Temperature       = conv.Temperature,
            SystemPrompt      = conv.SystemPrompt,
        };

        // Zkopíruj zprávy až po (včetně) zvolené
        for (var i = 0; i <= idx; i++)
        {
            var src  = conv.Messages[i];
            var copy = new ChatMessage { Role = src.Role, Content = src.Content };
            branch.Messages.Add(copy);
        }

        await TrySaveConversationAsync(branch);
        for (var i = 0; i < branch.Messages.Count; i++)
            await TrySaveMessageAsync(branch.Messages[i], branch);

        Conversations.Insert(0, branch);
        ResortConversations();
        SelectedConversation = branch;
    }

}
