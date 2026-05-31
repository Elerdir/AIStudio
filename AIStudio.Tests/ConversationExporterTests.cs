using AIStudio.Core.Services;
using FluentAssertions;

namespace AIStudio.Tests;

public class ConversationExporterTests
{
    private static readonly DateTime FixedTime = new(2026, 5, 31, 14, 30, 0);

    private static IReadOnlyList<ExportMessage> SampleConversation() =>
    [
        new ExportMessage("user",      "Ahoj, jak se máš?", FixedTime),
        new ExportMessage("assistant", "Dobře, děkuji!",     FixedTime.AddMinutes(1)),
    ];

    // ── Clipboard formát ──────────────────────────────────────────────────────

    [Fact]
    public void ToClipboardText_FormatsRolesAndContent()
    {
        var text = ConversationExporter.ToClipboardText(SampleConversation());

        text.Should().Contain("Já:");
        text.Should().Contain("Ahoj, jak se máš?");
        text.Should().Contain("Asistent:");
        text.Should().Contain("Dobře, děkuji!");
    }

    [Fact]
    public void ToClipboardText_Empty_ReturnsEmpty()
    {
        ConversationExporter.ToClipboardText([]).Should().BeEmpty();
    }

    // ── Markdown ──────────────────────────────────────────────────────────────

    [Fact]
    public void ToMarkdown_IncludesFrontmatterAndMessages()
    {
        var md = ConversationExporter.ToMarkdown(
            "Můj chat", "llama-3.1-8b", null, FixedTime, SampleConversation());

        md.Should().StartWith("# Můj chat");
        md.Should().Contain("**Model:** llama-3.1-8b");
        md.Should().Contain("**Exportováno:**");
        md.Should().Contain("👤 Uživatel");
        md.Should().Contain("🤖 Asistent");
        md.Should().Contain("Ahoj, jak se máš?");
    }

    [Fact]
    public void ToMarkdown_WithSystemPrompt_IncludesSection()
    {
        var md = ConversationExporter.ToMarkdown(
            "Chat", "model", "Mluv jako pirát.", FixedTime, SampleConversation());

        md.Should().Contain("## Systémový prompt");
        md.Should().Contain("Mluv jako pirát.");
    }

    [Fact]
    public void ToMarkdown_NoSystemPrompt_OmitsSection()
    {
        var md = ConversationExporter.ToMarkdown(
            "Chat", "model", null, FixedTime, SampleConversation());

        md.Should().NotContain("## Systémový prompt");
    }

    // ── Plain text ────────────────────────────────────────────────────────────

    [Fact]
    public void ToPlainText_IncludesHeaderAndMessages()
    {
        var txt = ConversationExporter.ToPlainText(
            "Můj chat", "mistral", null, FixedTime, SampleConversation());

        txt.Should().Contain("Chat:        Můj chat");
        txt.Should().Contain("Model:       mistral");
        txt.Should().Contain("[Uživatel]");
        txt.Should().Contain("[Asistent]");
        txt.Should().Contain("Ahoj, jak se máš?");
    }

    // ── SanitizeFileName ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("Normální název", "Normální název")]
    [InlineData("a/b\\c:d", "a_b_c_d")]
    [InlineData("file<>name", "file__name")]
    public void SanitizeFileName_ReplacesInvalidChars(string input, string expected)
    {
        ConversationExporter.SanitizeFileName(input).Should().Be(expected);
    }

    [Fact]
    public void SanitizeFileName_TruncatesLongNames()
    {
        var longTitle = new string('x', 100);
        ConversationExporter.SanitizeFileName(longTitle).Length.Should().Be(60);
    }

    [Fact]
    public void SanitizeFileName_Empty_ReturnsFallback()
    {
        ConversationExporter.SanitizeFileName("").Should().Be("chat");
    }
}
