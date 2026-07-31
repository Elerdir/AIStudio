using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Core.Services;
using Avalonia.Threading;

namespace AIStudio.App.ViewModels.Lora;

/// <summary>
/// Samotný běh tréninku — Code of Conduct před prvním spuštěním, sestavení
/// požadavku, start/zrušení a překlad průběhu do UI. Partial split z hlavního
/// <see cref="LoraTrainingPaneViewModel"/>.
/// </summary>
public partial class LoraTrainingPaneViewModel
{
    // ── Trénink: start / cancel ───────────────────────────────────────────────

    // ── Code of Conduct (první spuštění tréninku) ─────────────────────────────

    /// <summary>True když je třeba ukázat CoC dialog místo startu tréninku.</summary>
    [ObservableProperty] private bool _isCodeOfConductVisible;

    [RelayCommand]
    private void AcceptCodeOfConduct()
    {
        _settings.Settings.LoraTrainingCodeOfConductAccepted = true;
        _ = _settings.SaveAsync();
        IsCodeOfConductVisible = false;
        // Po souhlasu rovnou pokračujeme s tréninkem
        _ = StartTrainingAsync();
    }

    [RelayCommand]
    private void DeclineCodeOfConduct() => IsCodeOfConductVisible = false;

    /// <summary>
    /// Najde FLUX text encodery (clip_l, t5xxl) + VAE (ae) v Models složce —
    /// v podsložkách clip/ a vae/ (kam je ukládá FluxDependencyService) i v rootu.
    /// </summary>
    private static (string? ClipL, string? T5, string? Ae) FindFluxDeps(string modelsRoot)
    {
        string? Find(string sub, params string[] files)
        {
            foreach (var f in files)
            {
                var inSub  = Path.Combine(modelsRoot, sub, f);
                var inRoot = Path.Combine(modelsRoot, f);
                if (File.Exists(inSub))  return inSub;
                if (File.Exists(inRoot)) return inRoot;
            }
            return null;
        }
        var clipL = Find("clip", "clip_l.safetensors");
        var t5    = Find("clip", "t5xxl_fp8_e4m3fn.safetensors", "t5xxl_fp16.safetensors");
        var ae    = Find("vae",  "ae.safetensors");
        return (clipL, t5, ae);
    }

    [RelayCommand]
    private async Task StartTrainingAsync()
    {
        if (!CanStartTraining) return;

        // První spuštění → CoC dialog. Po souhlasu se metoda zavolá znovu.
        if (!_settings.Settings.LoraTrainingCodeOfConductAccepted)
        {
            IsCodeOfConductVisible = true;
            return;
        }

        // KRITICKÉ pro rychlost: uvolni VRAM, kterou drží ComfyUI (FLUX/SDXL model
        // ~12 GB z generování/upscalu). Bez toho zbyde na trénink jen ~12 GB → VRAM
        // přeteče do system RAM (NVIDIA sysmem fallback) → GPU jede na 100 %, ale
        // ~30 s/krok místo ~4 (17 h místo ~2). Best-effort.
        if (_comfy is not null)
        {
            StatusLine = "Uvolňuji VRAM (ComfyUI)…";
            try { await _comfy.FreeMemoryAsync(); }
            catch (Exception ex) { Log.Warning(ex, "LoRA: uvolnění ComfyUI VRAM před tréninkem selhalo"); }
        }

        var modelsRoot = AppPaths.ResolveModelsDirectory(_settings.Settings.ModelsDirectory);
        var baseModelPath = ResolveSelectedBaseModelPath()
            ?? throw new InvalidOperationException(
                $"Nelze rozeznat cestu k '{SelectedBaseModel}'. Klikni Obnovit a zkus znovu.");
        var outputDir     = Path.Combine(modelsRoot, "loras");

        var dataset = DatasetItems
            .Select(i => new LoraTrainingImage(i.ImagePath, i.Caption ?? string.Empty))
            .ToList();

        // Trénovací rozlišení. FLUX dáváme 768 (ne 1024): na 24 GB se FLUX (12B)
        // při 1024 NEVEJDE ani s gradient checkpointingem → přeteče do system RAM
        // (sysmem fallback) → GPU 100 %, ale ~30 s/krok. 768 výrazně sníží paměť
        // i compute (attention škáluje s plochou²) a pro obličej/postavu je bohatě
        // dost. SDXL 1024, SD 1.5 512.
        var resolution = BaseModelTypeLabel switch
        {
            "SD 1.5" => 512,
            "FLUX"   => 768,
            _        => 1024,   // SDXL
        };

        // Block swapping (FLUX) adaptivně dle VRAM. NIKDY 0 pro FLUX na 24 GB —
        // i při 768 je blízko hraně, a 0 = riziko přetečení do RAM (thrashing).
        // Malý swap garantuje, že se to vejde do VRAM s minimálním zpomalením.
        // Slabší karty potřebují víc. Bereme max(live, poslední známá VRAM).
        var vramGb = Math.Max(_monitor?.Current?.VramTotalGb ?? 0, _lastKnownVramGb);
        var blocksToSwap = vramGb >= 22 ? 6
                         : vramGb >= 16 ? 14
                         : vramGb >= 12 ? 22
                         : 28;
        Log.Information(
            "LoRA trénink: VRAM={Vram:F1} GB, rozlišení={Res} → blocks_to_swap={Blocks}",
            vramGb, resolution, blocksToSwap);

        var parameters = new LoraTrainingParameters
        {
            Rank                  = Rank,
            Alpha                 = Alpha,
            Steps                 = Steps,
            LearningRate          = LearningRate,
            BatchSize             = BatchSize,
            Optimizer             = SelectedOptimizer,
            // Gradient checkpointing: SDXL i FLUX vždy (bez něj OOM i na 24 GB —
            // SDXL na 1024 trénuje 2 text encodery + UNet ~22 GB). SD 1.5 je malé,
            // tam ho zapneme jen na hraniční VRAM. (Trainer ho pro SDXL/FLUX navíc
            // vynutí, takže je to dvojitá pojistka.)
            GradientCheckpointing = BaseModelTypeLabel != "SD 1.5" || vramGb < 12,
            BlocksToSwap          = blocksToSwap,
            MixedPrecisionFp16    = true,
            Resolution            = resolution,
        };

        // FLUX trénink potřebuje samostatné clip_l/t5/ae — zajisti je (auto-download)
        // a resolvuj cesty. Sdílíme je s FLUX generováním (stejná Models složka).
        string? fluxClipL = null, fluxT5 = null, fluxAe = null;
        if (baseModelPath.Contains("flux", StringComparison.OrdinalIgnoreCase))
        {
            if (_fluxDeps is not null && !_fluxDeps.AreDependenciesPresent(modelsRoot))
            {
                IsTraining = true;
                StatusLine = "Stahuji FLUX závislosti (CLIP-L, T5, VAE)…";
                using var depCts = new CancellationTokenSource();
                try { await _fluxDeps.EnsureAsync(modelsRoot, _settings.Settings.HuggingFaceToken, depCts.Token); }
                catch (Exception ex) { Log.Warning(ex, "LoRA: stažení FLUX závislostí selhalo"); }
            }
            (fluxClipL, fluxT5, fluxAe) = FindFluxDeps(modelsRoot);
        }

        var request = new LoraTrainingRequest(
            Name:            TrainingName.Trim(),
            BaseModelPath:   baseModelPath,
            Dataset:         dataset,
            Parameters:      parameters,
            OutputDirectory: outputDir,
            FluxClipLPath:   fluxClipL,
            FluxT5Path:      fluxT5,
            FluxAePath:      fluxAe,
            TokenOnlyCaptions: TokenOnlyCaptions);

        // Reset stavu pro UI
        IsTraining       = true;
        IsResultSuccess  = false;
        IsResultError    = false;
        ResultMessage    = string.Empty;
        CurrentStep      = 0;
        TotalSteps       = Steps;
        CurrentProgress  = 0;
        StatusLine       = "Spouštím trénink…";
        ElapsedLabel     = string.Empty;
        RemainingLabel   = string.Empty;
        LossLabel        = string.Empty;

        _cts = new CancellationTokenSource();
        var progress = new Progress<LoraTrainingProgress>(p => Dispatcher.UIThread.Post(() => ApplyProgress(p)));

        try
        {
            var result = await Task.Run(async () =>
                await _trainer.TrainAsync(request, progress, _cts.Token), _cts.Token);

            Dispatcher.UIThread.Post(() =>
            {
                if (result.Success)
                {
                    IsResultSuccess = true;
                    ResultMessage   = $"✓ LoRA hotová za {FormatDuration(result.TotalTime)} — {result.OutputFilePath}";
                    StatusLine      = "Hotovo";
                    CurrentProgress = 100;
                }
                else
                {
                    IsResultError = true;
                    ResultMessage = $"❌ Trénink selhal: {result.ErrorMessage}";
                    StatusLine    = "Selhalo";
                }
            });
        }
        catch (OperationCanceledException)
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsResultError = true;
                ResultMessage = "Trénink zrušen uživatelem.";
                StatusLine    = "Zrušeno";
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "LoraTrainingPane: trénink hodil výjimku");
            Dispatcher.UIThread.Post(() =>
            {
                IsResultError = true;
                ResultMessage = $"❌ {ex.Message}";
                StatusLine    = "Chyba";
            });
        }
        finally
        {
            IsTraining = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void CancelTraining()
    {
        try { _cts?.Cancel(); }
        catch (Exception ex) { Log.Warning(ex, "LoraTrainingPane: cancel selhal"); }
    }

    private void ApplyProgress(LoraTrainingProgress p)
    {
        CurrentStep     = p.CurrentStep;
        TotalSteps      = p.TotalSteps;
        CurrentProgress = p.TotalSteps > 0 ? (double)p.CurrentStep / p.TotalSteps * 100 : 0;
        StatusLine      = p.StatusLine;
        ElapsedLabel    = FormatDuration(p.Elapsed);
        RemainingLabel  = p.EstimatedRemaining.HasValue
            ? $"zbývá ~{FormatDuration(p.EstimatedRemaining.Value)}"
            : string.Empty;
        LossLabel       = p.CurrentLoss.HasValue ? $"loss {p.CurrentLoss.Value:F4}" : string.Empty;
    }

    private static string FormatDuration(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours}h {t.Minutes}m"
        : t.TotalMinutes >= 1
            ? $"{t.Minutes}m {t.Seconds}s"
            : $"{t.Seconds}s";
}
