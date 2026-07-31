using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AIStudio.Core.Models;

namespace AIStudio.App.ViewModels.Lora;

/// <summary>
/// Parametry tréninku (rank/alpha/kroky/LR/batch), presety a jejich odvození
/// z base modelu. Partial split z hlavního <see cref="LoraTrainingPaneViewModel"/>:
/// všechno, co ovlivňuje <b>jak</b> se trénuje — bez I/O.
/// </summary>
public partial class LoraTrainingPaneViewModel
{
    // ── Parametry ─────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstimatedTimeLabel))]
    private int _rank = 32;

    [ObservableProperty] private int _alpha = 16;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstimatedTimeLabel))]
    private int _steps = 1500;

    [ObservableProperty] private double _learningRate = 1e-4;

    [ObservableProperty] private int _batchSize = 1;

    [ObservableProperty] private string _selectedOptimizer = "AdamW8bit";

    public IReadOnlyList<string> AvailableOptimizers { get; } =
        new[] { "AdamW8bit", "AdamW", "Lion", "Prodigy" };

    /// <summary>Aktivní preset pro zvýraznění tlačítka (null = vlastní/ručně upraveno).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPresetPerson), nameof(IsPresetStyle), nameof(IsPresetQuick))]
    private string? _activePreset = "person";

    public bool IsPresetPerson => ActivePreset == "person";
    public bool IsPresetStyle  => ActivePreset == "style";
    public bool IsPresetQuick  => ActivePreset == "quick";

    /// <summary>
    /// Presety parametrů jedním klikem. Hodnoty osvědčené pro SDXL na 8+ GB VRAM:
    /// <list type="bullet">
    /// <item><b>Postava</b> — konkrétní osoba/obličej. Rank 32, 1500 kroků,
    ///   batch 2 (využije VRAM 3090), 1e-4. Sweet spot pro ~20-30 fotek.</item>
    /// <item><b>Styl</b> — estetika/umělecký styl. Vyšší rank 64 (víc kapacity
    ///   pro komplexní vzory), 2000 kroků, nižší LR 5e-5 (jemnější).</item>
    /// <item><b>Rychlý test</b> — ověření že pipeline funguje. Rank 16, jen 400
    ///   kroků, ~2-3 min. Výsledek je hrubý, ale rychle uvidíš, zda to jede.</item>
    /// </list>
    /// </summary>
    [RelayCommand]
    private void ApplyPreset(string preset)
    {
        // Guard: nastavování hodnot níže by jinak přes OnXxxChanged shodilo
        // ActivePreset zpátky na null (vypadalo by to jako „ruční úprava").
        _applyingPreset = true;
        try
        {
            switch (preset)
            {
                case "person":
                    Rank = 32; Alpha = 16; Steps = 1500; LearningRate = 1e-4;
                    BatchSize = RecommendedBatchForVram(); SelectedOptimizer = "AdamW8bit";
                    break;
                case "style":
                    Rank = 64; Alpha = 32; Steps = 2000; LearningRate = 5e-5;
                    BatchSize = RecommendedBatchForVram(); SelectedOptimizer = "AdamW8bit";
                    break;
                case "quick":
                    Rank = 16; Alpha = 8; Steps = 400; LearningRate = 1e-4;
                    BatchSize = 1; SelectedOptimizer = "AdamW8bit";
                    break;
                default:
                    return;
            }
            ActivePreset = preset;
        }
        finally { _applyingPreset = false; }
    }

    /// <summary>
    /// Doporučený batch size podle VRAM. 24 GB → 2 (rychlejší, gradient hladší
    /// u SDXL LoRA), pod 12 GB → 1 (jistota proti OOM).
    /// </summary>
    private int RecommendedBatchForVram()
    {
        var vram = _monitor?.Current?.VramTotalGb ?? 0;
        return vram >= 16 ? 2 : 1;
    }

    // Když uživatel ručně sáhne na parametr, zrušíme zvýraznění presetu
    // (hodnota už neodpovídá presetu — je „vlastní").
    partial void OnRankChanged(int value)            => ClearPresetIfManual();
    partial void OnAlphaChanged(int value)           => ClearPresetIfManual();
    partial void OnStepsChanged(int value)
    {
        ClearPresetIfManual();
        // Ruční editace kroků zruší auto-doporučení (aby přidání další fotky
        // uživateli nepřepsalo jeho vlastní hodnotu).
        if (!_autoSettingSteps) _userTouchedSteps = true;
    }

    /// <summary>Poslední naměřená VRAM (GB) z monitoru — cache pro chvíle, kdy je Current dočasně 0.</summary>
    private double _lastKnownVramGb;
    /// <summary>True když uživatel ručně přepsal počet kroků — pak ho auto-doporučení nepřepisuje.</summary>
    private bool _userTouchedSteps;
    /// <summary>Guard: programové nastavení Steps nemá počítat jako ruční editaci.</summary>
    private bool _autoSettingSteps;

    /// <summary>
    /// Doporučený počet kroků = ~100 na fotku (osvědčená heuristika pro person/character
    /// LoRA), ohraničený doporučeným minimem dle base modelu a stropem 4000. Aplikuje se
    /// jen dokud uživatel kroky ručně nepřepsal.
    /// </summary>
    private void UpdateRecommendedSteps()
    {
        if (_userTouchedSteps || IsTraining) return;

        var filename = Path.GetFileName(ResolveSelectedBaseModelPath() ?? SelectedBaseModel ?? string.Empty);
        var baseSteps = LoraTrainingParameters.DefaultsFor(filename).Steps;
        var recommended = DatasetItems.Count > 0
            ? Math.Clamp(DatasetItems.Count * 100, baseSteps, 4000)
            : baseSteps;

        _autoSettingSteps = true;
        Steps = recommended;
        _autoSettingSteps = false;
    }
    partial void OnLearningRateChanged(double value) => ClearPresetIfManual();
    partial void OnBatchSizeChanged(int value)       => ClearPresetIfManual();
    partial void OnSelectedOptimizerChanged(string value) => ClearPresetIfManual();

    /// <summary>Guard proti rekurzi: ApplyPreset nastavuje hodnoty, nechceme aby to hned shodilo preset.</summary>
    private bool _applyingPreset;

    private void ClearPresetIfManual()
    {
        if (!_applyingPreset) ActivePreset = null;
    }


    // ── Doporučené parametry podle base modelu ────────────────────────────────

    partial void OnSelectedBaseModelChanged(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;

        // Filename pro DefaultsFor — SelectedBaseModel může nést display prefix
        // („[ComfyUI] ", „[Vlastní] "), detekce by ho jinak interpretovala chybně.
        var filename = Path.GetFileName(ResolveSelectedBaseModelPath() ?? value);

        // Aplikuj doporučené default parametry, pokud uživatel ještě nemění nic
        // (tj. má je na initial defaults). Pokud už si rank zvedl, neměníme to.
        var defaults = LoraTrainingParameters.DefaultsFor(filename);

        // Heuristika "uživatel zatím needitoval" — porovnáme s našimi sticky defaults.
        // Pro MVP prostě vždycky aplikujeme — pokročilý uživatel může přepsat zpátky.
        Rank         = defaults.Rank;
        Alpha        = defaults.Alpha;
        LearningRate = defaults.LearningRate;
        BatchSize    = defaults.BatchSize;

        // Změna base modelu resetuje i auto-doporučení kroků (nové optimum dle typu).
        _userTouchedSteps = false;
        UpdateRecommendedSteps();

        // Synchronizuj přepínač typu podle vybraného base (FLUX base → FLUX přepínač).
        // Guard brání zpětnému přepnutí base v OnSelectedTrainingTypeChanged.
        _syncingTrainingType = true;
        SelectedTrainingType = BaseModelTypeLabel == "FLUX" ? "FLUX" : "SDXL";
        _syncingTrainingType = false;
    }
}
