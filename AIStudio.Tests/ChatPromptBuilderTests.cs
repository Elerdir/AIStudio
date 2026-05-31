using AIStudio.Core.Services;
using FluentAssertions;

namespace AIStudio.Tests;

public class ChatPromptBuilderTests
{
    // ── System prompt resolution ──────────────────────────────────────────────

    [Fact]
    public void BuildHistory_NoOverride_UsesDefaultSystemPrompt()
    {
        var h = ChatPromptBuilder.BuildHistory(
            systemPromptOverride: null, modelName: "llama-3.1-8b",
            thinkingEnabled: true, priorMessages: []);

        h.Should().HaveCount(1);
        h[0].Role.Should().Be("system");
        h[0].Content.Should().Be(ChatPromptBuilder.DefaultSystemPrompt);
    }

    [Fact]
    public void BuildHistory_WithOverride_UsesCustomPrompt()
    {
        var h = ChatPromptBuilder.BuildHistory(
            "Mluv jako pirát.", "llama-3.1-8b", true, []);

        h[0].Role.Should().Be("system");
        h[0].Content.Should().Be("Mluv jako pirát.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BuildHistory_BlankOverride_FallsBackToDefault(string? blank)
    {
        var h = ChatPromptBuilder.BuildHistory(blank, "mistral", true, []);
        h[0].Content.Should().Be(ChatPromptBuilder.DefaultSystemPrompt);
    }

    // ── Qwen3 thinking mode ───────────────────────────────────────────────────

    [Fact]
    public void BuildHistory_Qwen3ThinkingOff_PrependsNoThink()
    {
        var h = ChatPromptBuilder.BuildHistory(
            "Pomoz mi.", "Qwen3-8B-Instruct", thinkingEnabled: false, priorMessages: []);

        h[0].Content.Should().StartWith("/no_think\n");
        h[0].Content.Should().Contain("Pomoz mi.");
    }

    [Fact]
    public void BuildHistory_Qwen3ThinkingOn_NoNoThinkPrefix()
    {
        var h = ChatPromptBuilder.BuildHistory(
            "Pomoz mi.", "Qwen3-8B", thinkingEnabled: true, priorMessages: []);

        h[0].Content.Should().NotStartWith("/no_think");
    }

    [Fact]
    public void BuildHistory_NonQwen3ThinkingOff_NoNoThinkPrefix()
    {
        // Thinking flag se aplikuje JEN na Qwen3 — u Llamy je irelevantní
        var h = ChatPromptBuilder.BuildHistory(
            "Pomoz mi.", "llama-3.1-8b", thinkingEnabled: false, priorMessages: []);

        h[0].Content.Should().NotStartWith("/no_think");
    }

    [Theory]
    [InlineData("Qwen3-8B", true)]
    [InlineData("qwen3-30b-a3b", true)]
    [InlineData("QWEN3-Coder", true)]
    [InlineData("Qwen2.5-32B", false)]
    [InlineData("llama-3.1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsQwen3Model_DetectsCorrectly(string? name, bool expected)
    {
        ChatPromptBuilder.IsQwen3Model(name).Should().Be(expected);
    }

    // ── Pořadí + role mapping ─────────────────────────────────────────────────

    [Fact]
    public void BuildHistory_PreservesMessageOrder()
    {
        var prior = new[]
        {
            ("user",      "Ahoj"),
            ("assistant", "Zdravím"),
            ("user",      "Jak se máš?"),
        };

        var h = ChatPromptBuilder.BuildHistory(null, "llama", true, prior);

        h.Should().HaveCount(4);                 // system + 3
        h[0].Role.Should().Be("system");
        h[1].Should().Be(("user", "Ahoj"));
        h[2].Should().Be(("assistant", "Zdravím"));
        h[3].Should().Be(("user", "Jak se máš?"));
    }

    [Theory]
    [InlineData("USER", "user")]
    [InlineData("Assistant", "assistant")]
    [InlineData("SYSTEM", "system")]
    [InlineData("tool", "user")]
    [InlineData("", "user")]
    [InlineData(null, "user")]
    public void NormalizeRole_MapsCorrectly(string? input, string expected)
    {
        ChatPromptBuilder.NormalizeRole(input).Should().Be(expected);
    }

    [Fact]
    public void BuildHistory_UnknownRole_MappedToUser()
    {
        var h = ChatPromptBuilder.BuildHistory(
            null, "llama", true, new[] { ("function", "data") });

        h[1].Role.Should().Be("user");
        h[1].Content.Should().Be("data");
    }
}
