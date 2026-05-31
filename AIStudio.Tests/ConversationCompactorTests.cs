using AIStudio.Core.Services;
using FluentAssertions;
using Msg = AIStudio.Core.Services.ConversationCompactor.Message;

namespace AIStudio.Tests;

/// <summary>
/// Testy pure logiky compactu konverzace — rozdělení zpráv, sestavení promptu,
/// formátování summary. Žádný LLM ani UI; deterministické.
/// </summary>
public class ConversationCompactorTests
{
    private static List<Msg> Make(int count)
    {
        var list = new List<Msg>();
        for (var i = 0; i < count; i++)
            list.Add(new Msg(i % 2 == 0 ? "user" : "assistant", $"zpráva {i}"));
        return list;
    }

    // ── CanCompact ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, false)]
    [InlineData(5, false)]   // pod minimem
    [InlineData(6, true)]
    [InlineData(20, true)]
    public void CanCompact_RespectsMinimum(int count, bool expected)
    {
        ConversationCompactor.CanCompact(count).Should().Be(expected);
    }

    [Fact]
    public void CanCompact_NotEnoughBeyondKeepRecent_False()
    {
        // keepRecent=4, count=5 → jen 1 zpráva k shrnutí, navíc pod minimem
        ConversationCompactor.CanCompact(5, keepRecent: 4).Should().BeFalse();
    }

    // ── Split ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Split_KeepsLastNVerbatim()
    {
        var msgs = Make(10);

        var (toSummarize, toKeep) = ConversationCompactor.Split(msgs, keepRecent: 4);

        toSummarize.Should().HaveCount(6);
        toKeep.Should().HaveCount(4);
        toKeep[0].Content.Should().Be("zpráva 6");
        toKeep[^1].Content.Should().Be("zpráva 9");
    }

    [Fact]
    public void Split_EmptyInput_ReturnsEmpty()
    {
        var (toSummarize, toKeep) = ConversationCompactor.Split(new List<Msg>());
        toSummarize.Should().BeEmpty();
        toKeep.Should().BeEmpty();
    }

    [Fact]
    public void Split_KeepRecentLargerThanCount_KeepsAll()
    {
        var msgs = Make(3);
        var (toSummarize, toKeep) = ConversationCompactor.Split(msgs, keepRecent: 10);
        toSummarize.Should().BeEmpty();
        toKeep.Should().HaveCount(3);
    }

    // ── BuildSummaryPrompt ─────────────────────────────────────────────────────

    [Fact]
    public void BuildSummaryPrompt_IncludesAllMessagesAndRoles()
    {
        var msgs = new List<Msg>
        {
            new("user", "Jak na to?"),
            new("assistant", "Takhle."),
        };

        var prompt = ConversationCompactor.BuildSummaryPrompt(msgs);

        prompt.Should().HaveCount(2);
        prompt[0].Role.Should().Be("system");
        prompt[1].Role.Should().Be("user");
        prompt[1].Content.Should().Contain("Uživatel: Jak na to?");
        prompt[1].Content.Should().Contain("Asistent: Takhle.");
    }

    // ── FormatSummary ──────────────────────────────────────────────────────────

    [Fact]
    public void FormatSummary_WrapsWithHeader()
    {
        var result = ConversationCompactor.FormatSummary("- bod jedna\n- bod dva");

        result.Should().StartWith(ConversationCompactor.SummaryHeader);
        result.Should().Contain("bod jedna");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void FormatSummary_EmptyInput_StillProducesNonEmptyBody(string? raw)
    {
        var result = ConversationCompactor.FormatSummary(raw);
        result.Should().StartWith(ConversationCompactor.SummaryHeader);
        result.Length.Should().BeGreaterThan(ConversationCompactor.SummaryHeader.Length + 2);
    }

    // ── IsSummary ──────────────────────────────────────────────────────────────

    [Fact]
    public void IsSummary_DetectsFormattedSummary()
    {
        var summary = ConversationCompactor.FormatSummary("něco");
        ConversationCompactor.IsSummary(summary).Should().BeTrue();
        ConversationCompactor.IsSummary("Normální zpráva").Should().BeFalse();
        ConversationCompactor.IsSummary(null).Should().BeFalse();
    }
}
