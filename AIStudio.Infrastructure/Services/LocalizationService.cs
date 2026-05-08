using System.Reflection;
using System.Resources;
using System.Xml.Linq;
using AIStudio.Core.Enums;
using AIStudio.Core.Interfaces;
using Serilog;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Implementace lokalizace přes ResX soubory zabudované do AIStudio.App assembly.
/// ResX soubory jsou uloženy v AIStudio.App/Strings/ a embeddovány jako EmbeddedResource.
/// Fallback: pokud klíč není v aktuálním jazyce, hledáme v češtině, pak vrátíme klíč.
/// </summary>
public sealed class LocalizationService : ILocalizationService
{
    private static readonly Dictionary<AppLanguage, Dictionary<string, string>> _cache = new();
    private static Assembly? _appAssembly;

    private AppLanguage _language;

    public AppLanguage Language
    {
        get => _language;
        set
        {
            if (_language == value) return;
            _language = value;
            LanguageChanged?.Invoke(this, EventArgs.Empty);
            Log.Information("Localization: jazyk přepnut na {Language}", value);
        }
    }

    public event EventHandler? LanguageChanged;

    public string this[string key]
    {
        get
        {
            var dict = GetOrLoadDictionary(_language);
            if (dict.TryGetValue(key, out var val)) return val;

            // Fallback: čeština
            if (_language != AppLanguage.Czech)
            {
                var cs = GetOrLoadDictionary(AppLanguage.Czech);
                if (cs.TryGetValue(key, out var csVal)) return csVal;
            }

            return key;   // Poslední záchrana: vrátí klíč samotný
        }
    }

    // ── Načítání ResX ─────────────────────────────────────────────────────────

    private static Dictionary<string, string> GetOrLoadDictionary(AppLanguage lang)
    {
        if (_cache.TryGetValue(lang, out var cached)) return cached;

        var dict = LoadResX(ResourceNameFor(lang));
        _cache[lang] = dict;
        return dict;
    }

    // SDK auto-embeduje .resx jako {Namespace}.{Subdir}.{FilenameWithoutExtension}
    // → Strings/Strings.cs.resx → AIStudio.App.Strings.Strings.cs
    private static string ResourceNameFor(AppLanguage lang) => lang switch
    {
        AppLanguage.English => "AIStudio.App.Strings.Strings.en",
        _                   => "AIStudio.App.Strings.Strings.cs",
    };

    private static Dictionary<string, string> LoadResX(string resourceName)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            // Najdeme AIStudio.App assembly (načtena jako závislost)
            _appAssembly ??= AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "AIStudio.App");

            if (_appAssembly is null)
            {
                Log.Warning("LocalizationService: AIStudio.App assembly nenalezena");
                return result;
            }

            using var stream = _appAssembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                Log.Warning("LocalizationService: resource {Name} nenalezen", resourceName);
                return result;
            }

            var doc = XDocument.Load(stream);
            foreach (var data in doc.Descendants("data"))
            {
                var name  = data.Attribute("name")?.Value;
                var value = data.Element("value")?.Value;
                if (name is not null && value is not null)
                    result[name] = value;
            }

            Log.Information("LocalizationService: načteno {Count} klíčů z {Resource}",
                result.Count, resourceName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "LocalizationService: chyba při načítání {Resource}", resourceName);
        }
        return result;
    }
}
