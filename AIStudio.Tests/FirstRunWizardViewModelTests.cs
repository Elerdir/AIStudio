using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.App.ViewModels.Setup;
using FluentAssertions;
using NSubstitute;

namespace AIStudio.Tests;

/// <summary>
/// Testy pro FirstRunWizardViewModel — čistá MVVM logika bez Avalonia UI.
/// ISystemMonitorService.StartAsync() je mockován, StatusUpdated event
/// spouštíme ručně pro determinismus.
/// </summary>
public class FirstRunWizardViewModelTests
{
    private static (FirstRunWizardViewModel vm,
                    ISettingsService settings,
                    ISystemMonitorService monitor) MakeVm(AppSettings? seed = null)
    {
        seed ??= new AppSettings();
        var settings = Substitute.For<ISettingsService>();
        settings.Settings.Returns(seed);
        settings.SaveAsync().Returns(Task.CompletedTask);

        var monitor = Substitute.For<ISystemMonitorService>();
        monitor.StartAsync().Returns(Task.CompletedTask);

        var vm = new FirstRunWizardViewModel(settings, monitor);
        return (vm, settings, monitor);
    }

    // ── Navigace kroky ────────────────────────────────────────────────────────

    [Fact]
    public void InitialStep_IsZero()
    {
        var (vm, _, _) = MakeVm();
        vm.CurrentStep.Should().Be(0);
    }

    [Fact]
    public void Next_IncrementsStep()
    {
        var (vm, _, _) = MakeVm();
        vm.NextCommand.Execute(null);
        vm.CurrentStep.Should().Be(1);
    }

    [Fact]
    public void Back_DecrementsStep()
    {
        var (vm, _, _) = MakeVm();
        vm.NextCommand.Execute(null);   // Step 0 → 1
        vm.BackCommand.Execute(null);   // Step 1 → 0
        vm.CurrentStep.Should().Be(0);
    }

    [Fact]
    public void Back_OnStep0_DoesNotGoNegative()
    {
        var (vm, _, _) = MakeVm();
        vm.BackCommand.Execute(null);
        vm.CurrentStep.Should().Be(0);
    }

    [Fact]
    public void CanGoBack_FalseOnFirstStep_TrueAfterNext()
    {
        var (vm, _, _) = MakeVm();
        vm.CanGoBack.Should().BeFalse();
        vm.NextCommand.Execute(null);
        vm.CanGoBack.Should().BeTrue();
    }

    [Fact]
    public void IsLastStep_TrueOnlyAtStep4()
    {
        var (vm, _, _) = MakeVm();
        for (int i = 0; i < 4; i++)
        {
            vm.IsLastStep.Should().BeFalse($"krok {i} není poslední");
            vm.NextCommand.Execute(null);
        }
        vm.IsLastStep.Should().BeTrue("krok 4 je poslední");
    }

    [Fact]
    public void NextButtonText_ChangesOnLastStep()
    {
        var (vm, _, _) = MakeVm();
        vm.NextButtonText.Should().Be("Pokračovat →");

        for (int i = 0; i < 4; i++) vm.NextCommand.Execute(null);
        vm.NextButtonText.Should().Be("Spustit AI Studio →");
    }

    // ── IsStep* properties ────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void IsStepN_TrueOnlyForCurrentStep(int targetStep)
    {
        var (vm, _, _) = MakeVm();
        for (int i = 0; i < targetStep; i++) vm.NextCommand.Execute(null);

        bool[] expected = [
            vm.IsStep0, vm.IsStep1, vm.IsStep2, vm.IsStep3, vm.IsStep4
        ];

        for (int i = 0; i < 5; i++)
            expected[i].Should().Be(i == targetStep, $"IsStep{i} při targetStep={targetStep}");
    }

    // ── Initialize načítá stávající nastavení ─────────────────────────────────

    [Fact]
    public void Initialize_LoadsExistingSettings()
    {
        var seed = new AppSettings
        {
            ModelsDirectory  = @"D:\Models",
            UseGpu           = false,
            HuggingFaceToken = "hf_test123",
            CivitaiApiKey    = "civ_key456",
        };
        var (vm, _, _) = MakeVm(seed);
        vm.Initialize();

        vm.ModelsDirectory.Should().Be(@"D:\Models");
        vm.UseGpu.Should().BeFalse();
        vm.HuggingFaceToken.Should().Be("hf_test123");
        vm.CivitaiApiKey.Should().Be("civ_key456");
    }

    [Fact]
    public void Initialize_EmptyModelsDir_UsesDefault()
    {
        var (vm, _, _) = MakeVm(new AppSettings { ModelsDirectory = string.Empty });
        vm.Initialize();

        vm.ModelsDirectory.Should().Be(FirstRunWizardViewModel.DefaultModelsDir);
    }

    // ── SummaryModelsDir ──────────────────────────────────────────────────────

    [Fact]
    public void SummaryModelsDir_ReturnsCustomIfSet()
    {
        var (vm, _, _) = MakeVm();
        vm.ModelsDirectory = @"E:\CustomModels";
        vm.SummaryModelsDir.Should().Be(@"E:\CustomModels");
    }

    [Fact]
    public void SummaryModelsDir_ReturnsDefaultIfEmpty()
    {
        var (vm, _, _) = MakeVm();
        vm.ModelsDirectory = string.Empty;
        vm.SummaryModelsDir.Should().Be(FirstRunWizardViewModel.DefaultModelsDir);
    }

    // ── Finish ukládá nastavení ───────────────────────────────────────────────

    [Fact]
    public void Finish_SavesSettingsAndFiresWizardCompleted()
    {
        var seed = new AppSettings();
        var (vm, settings, _) = MakeVm(seed);

        vm.ModelsDirectory  = @"D:\Models";
        vm.UseGpu           = false;
        vm.HuggingFaceToken = "  hf_abc  ";   // trimming
        vm.CivitaiApiKey    = "  civ_xyz  ";

        bool completed = false;
        vm.WizardCompleted += (_, _) => completed = true;

        // Přejdeme na krok 4 a klikneme Next → Finish
        for (int i = 0; i < 4; i++) vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);   // poslední Next = Finish

        seed.ModelsDirectory.Should().Be(@"D:\Models");
        seed.UseGpu.Should().BeFalse();
        seed.HuggingFaceToken.Should().Be("hf_abc");
        seed.CivitaiApiKey.Should().Be("civ_xyz");
        seed.SetupCompleted.Should().BeTrue();

        settings.Received(1).SaveAsync();
        completed.Should().BeTrue();
    }

    [Fact]
    public void Finish_DefaultModelsDir_SavedAsEmpty()
    {
        var seed = new AppSettings();
        var (vm, _, _) = MakeVm(seed);

        vm.ModelsDirectory = FirstRunWizardViewModel.DefaultModelsDir;
        for (int i = 0; i < 5; i++) vm.NextCommand.Execute(null);

        // Výchozí cesta se ukládá jako prázdný string (kompaktní uložení)
        seed.ModelsDirectory.Should().BeEmpty();
    }

    // ── HasHfToken / HasCivitaiKey ────────────────────────────────────────────

    [Theory]
    [InlineData("hf_token123", true)]
    [InlineData("",            false)]
    public void HasHfToken_ReflectsHuggingFaceToken(string token, bool expected)
    {
        var (vm, _, _) = MakeVm();
        vm.HuggingFaceToken = token;
        vm.HasHfToken.Should().Be(expected);
    }

    [Theory]
    [InlineData("civ_key", true)]
    [InlineData("",        false)]
    public void HasCivitaiKey_ReflectsCivitaiApiKey(string key, bool expected)
    {
        var (vm, _, _) = MakeVm();
        vm.CivitaiApiKey = key;
        vm.HasCivitaiKey.Should().Be(expected);
    }

    // ── GPU detekce ───────────────────────────────────────────────────────────

    [Fact]
    public void Initialize_StartsWithGpuDetecting()
    {
        var (vm, _, _) = MakeVm();
        vm.Initialize();
        // GPU detekce je asynchronní — hned po Initialize je IsDetectingGpu = true
        vm.IsDetectingGpu.Should().BeTrue();
    }

    [Fact]
    public void GpuSummaryLine_WithoutGpu_ContainsCpu()
    {
        var (vm, _, _) = MakeVm();
        vm.GpuDetected = false;
        vm.GpuSummaryLine.Should().Contain("CPU");
    }

    [Fact]
    public void GpuSummaryLine_WithGpu_ContainsNameAndVram()
    {
        var (vm, _, _) = MakeVm();
        vm.GpuDetected  = true;
        vm.GpuName      = "RTX 3080";
        vm.VramTotalGb  = 10.0;
        vm.GpuSummaryLine.Should().Contain("RTX 3080").And.Contain("10");
    }

    [Fact]
    public void UseGpu_DefaultTrue()
    {
        var (vm, _, _) = MakeVm();
        vm.UseGpu.Should().BeTrue();
    }
}
