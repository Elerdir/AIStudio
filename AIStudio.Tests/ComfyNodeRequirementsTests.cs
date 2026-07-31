using AIStudio.Core.Services;
using FluentAssertions;

namespace AIStudio.Tests;

/// <summary>
/// Kontrola uzlů proti běžícímu ComfyUI. Chrání proti třídě chyb, kterou ostatní
/// testy nechytí: workflow builder odkazuje uzel řetězcem, takže překlep nebo uzel
/// přejmenovaný v novém ComfyUI projde buildem i unit testy a spadne až při generování.
/// </summary>
public class ComfyNodeRequirementsTests
{
    /// <summary>Vše, co kterákoliv skupina vyžaduje — „ideální" instalace.</summary>
    private static HashSet<string> AllRequiredNodes() =>
        new(ComfyNodeRequirements.All.SelectMany(g => g.Nodes), StringComparer.Ordinal);

    [Fact]
    public void Evaluate_CompleteInstall_ReportsNothingMissing()
    {
        ComfyNodeRequirements.Evaluate(AllRequiredNodes()).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_EmptyInstall_ReportsEveryGroup()
    {
        var missing = ComfyNodeRequirements.Evaluate(Array.Empty<string>());
        missing.Should().HaveCount(ComfyNodeRequirements.All.Count);
    }

    [Fact]
    public void Evaluate_MissingRifeNode_PointsAtFrameInterpolationPack()
    {
        // Přesně scénář, kvůli kterému kontrola vznikla: „RIFE VFI" je název
        // s mezerou a balík se doinstalovává za běhu — snadno tiše chybí.
        var nodes = AllRequiredNodes();
        nodes.Remove("RIFE VFI");

        var missing = ComfyNodeRequirements.Evaluate(nodes);

        missing.Should().ContainSingle()
            .Which.Should().Match<ComfyMissingNodes>(m =>
                m.Missing.Contains("RIFE VFI") &&
                m.CustomNodePack == ComfyNodeRequirements.PackFrameInterp);
    }

    [Fact]
    public void Evaluate_MissingVideoHelperSuite_ReportsBothVideoGroups()
    {
        // VHS balík dodává zápis MP4 i načtení hotového videa (dlouhé video).
        var nodes = AllRequiredNodes();
        nodes.Remove("VHS_VideoCombine");
        nodes.Remove("VHS_LoadVideoPath");

        var missing = ComfyNodeRequirements.Evaluate(nodes);

        missing.Should().HaveCount(2);
        missing.Should().OnlyContain(m => m.CustomNodePack == ComfyNodeRequirements.PackVideoHelper);
    }

    [Fact]
    public void Evaluate_IsCaseSensitive()
    {
        // ComfyUI bere class_type case-sensitive — „vhs_videocombine" by při
        // generování neprošlo, takže se nesmí počítat jako shoda.
        var nodes = AllRequiredNodes();
        nodes.Remove("VHS_VideoCombine");
        nodes.Add("vhs_videocombine");

        ComfyNodeRequirements.Evaluate(nodes)
            .Should().Contain(m => m.Missing.Contains("VHS_VideoCombine"));
    }

    [Fact]
    public void Evaluate_ExtraNodesAreIgnored()
    {
        var nodes = AllRequiredNodes();
        nodes.Add("SomeUnrelatedCustomNode");

        ComfyNodeRequirements.Evaluate(nodes).Should().BeEmpty();
    }

    [Fact]
    public void All_GroupsAreNonEmptyAndUniquelyNamed()
    {
        ComfyNodeRequirements.All.Should().OnlyContain(g => g.Nodes.Count > 0);
        ComfyNodeRequirements.All.Select(g => g.Feature).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Describe_NothingMissing_SaysSo()
    {
        ComfyNodeRequirements.Describe(Array.Empty<ComfyMissingNodes>())
            .Should().Contain("dostupné");
    }

    [Fact]
    public void Describe_MissingNodes_NamesNodeAndPack()
    {
        var missing = new[]
        {
            new ComfyMissingNodes("Plynulejší video", new[] { "RIFE VFI" },
                                  ComfyNodeRequirements.PackFrameInterp),
        };

        ComfyNodeRequirements.Describe(missing)
            .Should().Contain("RIFE VFI").And.Contain(ComfyNodeRequirements.PackFrameInterp);
    }

    [Fact]
    public void CheckResult_NotAvailable_IsNotTreatedAsSuccess()
    {
        // „Nezjištěno" a „nic nechybí" mají obojí prázdný seznam — nesmí splynout.
        ComfyNodeCheckResult.NotAvailable.AllPresent.Should().BeFalse();
        new ComfyNodeCheckResult(true, 500, Array.Empty<ComfyMissingNodes>())
            .AllPresent.Should().BeTrue();
    }
}
