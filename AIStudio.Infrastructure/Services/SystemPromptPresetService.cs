using System.Text.Json;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using Serilog;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Soubor s uživatelskými presety: <c>%AppData%/AIStudio/prompt-presets.json</c>.
/// Builtin presety jsou hard-coded (níže) a vrací se vždy první, bez I/O.
///
/// Zápis: atomicky přes <c>.tmp</c> + <see cref="File.Move(string, string, bool)"/>,
/// stejnou strategií jako <see cref="SettingsService"/> — nikdy nepoškodíme
/// hlavní soubor pádem uprostřed save.
/// </summary>
public sealed class SystemPromptPresetService : ISystemPromptPresetService
{
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AIStudio", "prompt-presets.json");

    private readonly string _filePath;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public SystemPromptPresetService() : this(DefaultPath) { }

    internal SystemPromptPresetService(string filePath)
    {
        _filePath = filePath;
    }

    // ── Builtin ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Vestavěné presety — verbatim převzaté z původního ChatPageViewModel.
    /// Drží se češtiny a tónu používaného v AI Studiu. „Bez instrukcí" je
    /// poslední a má prázdný prompt — UI ho použije jako reset.
    /// </summary>
    public IReadOnlyList<SystemPromptPreset> BuiltInPresets { get; } = new SystemPromptPreset[]
    {
        new("Asistent",
            "Jsi přátelský český asistent. Odpovídej jasně, stručně a konkrétně. " +
            "Pokud něco nevíš nebo si nejsi jistý, otevřeně to řekni místo vymýšlení.",
            IsBuiltIn: true),

        new("Editor",
            "Jsi profesionální editor češtiny. Když ti uživatel pošle text, " +
            "oprav gramatiku, interpunkci, stylistiku a čtivost. Změny stručně " +
            "okomentuj v krátkém shrnutí na konci. Pokud je text dobrý, řekni to.",
            IsBuiltIn: true),

        new("Kreativní psaní",
            "Jsi tvůrčí spisovatel s citem pro detail, atmosféru a dialog. " +
            "Piš barvitě, rozvíjej charaktery, používej smyslové detaily. " +
            "Drž se českého jazyka, pokud uživatel neřekne jinak.",
            IsBuiltIn: true),

        new("Programátor",
            "Jsi senior programátor. Vysvětluj kód jasně, navrhuj nejlepší " +
            "praktiky, varuj před antipatterny. Buď struční, ale úplní — " +
            "uveď nejen JAK, ale i PROČ. Když si nejsi jistý, řekni to.",
            IsBuiltIn: true),

        new("Brainstorm",
            "Jsi kreativní partner v brainstormingu. Generuj nápady volně, bez " +
            "filtrů. Když je třeba, kategorizuj. Neptej se na vyjasnění předčasně — " +
            "nejdřív hoď několik směrů, pak ladíme.",
            IsBuiltIn: true),

        new("Bez instrukcí", string.Empty, IsBuiltIn: true),
    };

    // ── Načítání / ukládání ──────────────────────────────────────────────────

    public async Task<IReadOnlyList<SystemPromptPreset>> LoadAllAsync()
    {
        var custom = await LoadCustomAsync();
        if (custom.Count == 0) return BuiltInPresets;

        // Builtin první, custom za nimi. UI tak má stabilní pořadí stávajících
        // tlačítek a nové se přidávají na konec.
        var combined = new List<SystemPromptPreset>(BuiltInPresets.Count + custom.Count);
        combined.AddRange(BuiltInPresets);
        combined.AddRange(custom);
        return combined;
    }

    public async Task SaveCustomAsync(SystemPromptPreset preset)
    {
        if (preset is null) throw new ArgumentNullException(nameof(preset));
        if (string.IsNullOrWhiteSpace(preset.Name))
            throw new ArgumentException("Preset musí mít neprázdné jméno.", nameof(preset));

        // Neumožni přepsat buildin — uživatel může vytvořit „Asistent2", ale ne
        // přepsat default „Asistent"
        if (BuiltInPresets.Any(b => string.Equals(b.Name, preset.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"Preset '{preset.Name}' má stejné jméno jako vestavěný — vyber jiné.");

        await _ioLock.WaitAsync();
        try
        {
            var custom = await ReadCustomFromDiskAsync();

            // Upsert: pokud už existuje preset stejného jména, přepiš ho
            var existing = custom.FindIndex(p =>
                string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));

            // Force IsBuiltIn=false — kdyby někdo zkusil podsunout zlomyslnou hodnotu
            var toSave = preset with { IsBuiltIn = false };

            if (existing >= 0) custom[existing] = toSave;
            else               custom.Add(toSave);

            await WriteCustomToDiskAsync(custom);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task<bool> DeleteCustomAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        // Builtin nelze smazat — uživatel by si rozbil výchozí stav
        if (BuiltInPresets.Any(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)))
            return false;

        await _ioLock.WaitAsync();
        try
        {
            var custom  = await ReadCustomFromDiskAsync();
            var removed = custom.RemoveAll(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

            if (removed == 0) return false;

            await WriteCustomToDiskAsync(custom);
            return true;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    // ── Disk I/O ──────────────────────────────────────────────────────────────

    /// <summary>Veřejný wrapper pro testy — interně používá <see cref="ReadCustomFromDiskAsync"/>.</summary>
    private async Task<IReadOnlyList<SystemPromptPreset>> LoadCustomAsync()
    {
        await _ioLock.WaitAsync();
        try { return await ReadCustomFromDiskAsync(); }
        finally { _ioLock.Release(); }
    }

    private async Task<List<SystemPromptPreset>> ReadCustomFromDiskAsync()
    {
        if (!File.Exists(_filePath)) return new List<SystemPromptPreset>();

        try
        {
            var json = await File.ReadAllTextAsync(_filePath);
            var loaded = JsonSerializer.Deserialize<List<SystemPromptPreset>>(json);
            // Stripni případné IsBuiltIn=true z disku — disk obsahuje pouze custom
            return loaded?.Select(p => p with { IsBuiltIn = false }).ToList()
                   ?? new List<SystemPromptPreset>();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SystemPromptPresetService: nelze načíst {Path}, používám prázdný seznam", _filePath);
            return new List<SystemPromptPreset>();
        }
    }

    private async Task WriteCustomToDiskAsync(List<SystemPromptPreset> custom)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

        var tmp  = _filePath + ".tmp";
        var json = JsonSerializer.Serialize(custom, JsonOpts);
        await File.WriteAllTextAsync(tmp, json);

        // Atomic rename — stejně jako SettingsService
        if (File.Exists(_filePath)) File.Move(tmp, _filePath, overwrite: true);
        else                         File.Move(tmp, _filePath);
    }
}
