using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using AIStudio.Core.Models;

namespace AIStudio.App.ViewModels.Chat;

/// <summary>
/// Systémové prompty (presety) — builtin + uživatelské. Partial split z hlavního
/// ChatPageViewModel: kohezní feature (knihovna presetů, uložit/smazat vlastní,
/// aplikovat na konverzaci) s vlastní službou <c>ISystemPromptPresetService</c>.
/// </summary>
public partial class ChatPageViewModel
{
    /// <summary>
    /// Předdefinované role/persony — uživatel je vidí jako tlačítka nad systémovým
    /// promptem. Klik naplní SystemPrompt aktuální konverzace.
    ///
    /// Zdroj: <see cref="ISystemPromptPresetService"/> — kombinuje builtin
    /// (Asistent/Editor/Kreativní psaní/Programátor/Brainstorm/Bez instrukcí)
    /// s uživatelskými, které se ukládají do JSON souboru. Builtin presety
    /// jsou available okamžitě po konstruktoru, custom přibydou až po
    /// <see cref="LoadPresetsAsync"/>.
    /// </summary>
    public ObservableCollection<SystemPromptPreset> SystemPromptPresets { get; }

    /// <summary>
    /// Načte custom presety ze souboru a doplní je do <see cref="SystemPromptPresets"/>.
    /// Builtin presety už jsou v kolekci z konstruktoru. Volá se na pozadí
    /// (fire-and-forget) z konstruktoru a kdykoliv po SaveCustom/DeleteCustom.
    /// </summary>
    private async Task LoadPresetsAsync()
    {
        try
        {
            var all = await _presetService.LoadAllAsync();
            Dispatcher.UIThread.Post(() =>
            {
                SystemPromptPresets.Clear();
                foreach (var p in all) SystemPromptPresets.Add(p);
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ChatPageViewModel: načítání custom presetů selhalo, používám builtin");
        }
    }

    /// <summary>
    /// Naplní SystemPrompt vybrané konverzace zvoleným presetem. Volá UI
    /// po kliku na tlačítko presetu. Pokud není vybraná konverzace, no-op.
    /// </summary>
    [RelayCommand]
    private void ApplySystemPromptPreset(SystemPromptPreset? preset)
    {
        if (preset is null || SelectedConversation is null) return;
        SelectedConversation.SystemPrompt = preset.Prompt;
        _ = TrySaveConversationAsync(SelectedConversation);
    }

    // ── Ukládání / mazání vlastních presetů ───────────────────────────────────

    /// <summary>Inline „pojmenuj a ulož preset" stav — TextBox v panelu.</summary>
    [ObservableProperty] private bool   _isNamingPreset;
    [ObservableProperty] private string _newPresetName = string.Empty;

    /// <summary>Otevře inline pole pro pojmenování — uloží aktuální systémový prompt jako preset.</summary>
    [RelayCommand]
    private void BeginSavePreset()
    {
        if (SelectedConversation is null || string.IsNullOrWhiteSpace(SelectedConversation.SystemPrompt))
            return;
        NewPresetName  = string.Empty;
        IsNamingPreset = true;
    }

    [RelayCommand]
    private void CancelSavePreset() => IsNamingPreset = false;

    /// <summary>Uloží aktuální systémový prompt jako pojmenovaný uživatelský preset.</summary>
    [RelayCommand]
    private async Task ConfirmSavePresetAsync()
    {
        var name   = NewPresetName.Trim();
        var prompt = SelectedConversation?.SystemPrompt ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(prompt))
        {
            IsNamingPreset = false;
            return;
        }
        try
        {
            await _presetService.SaveCustomAsync(new SystemPromptPreset(name, prompt, IsBuiltIn: false));
            await LoadPresetsAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ChatPageViewModel: uložení presetu '{Name}' selhalo", name);
        }
        IsNamingPreset = false;
    }

    /// <summary>Smaže uživatelský preset. Builtin presety nelze mazat (no-op).</summary>
    [RelayCommand]
    private async Task DeletePresetAsync(SystemPromptPreset? preset)
    {
        if (preset is null || preset.IsBuiltIn) return;
        try
        {
            await _presetService.DeleteCustomAsync(preset.Name);
            await LoadPresetsAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ChatPageViewModel: smazání presetu '{Name}' selhalo", preset.Name);
        }
    }
}
