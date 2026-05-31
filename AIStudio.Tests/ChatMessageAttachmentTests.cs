using AIStudio.App.ViewModels.Chat;
using FluentAssertions;

namespace AIStudio.Tests;

/// <summary>
/// Testy parsování příloh z markdown Contentu — náhledy v bublině se renderují
/// zvlášť, text se z markdownu vyřízne.
/// </summary>
public class ChatMessageAttachmentTests
{
    [Fact]
    public void ParseAttachmentPaths_SingleImage_ExtractsPath()
    {
        var content = @"![obrázek](E:\fotky\kocka.png)" + "\nudělej černobílou";
        var paths = ChatMessage.ParseAttachmentPaths(content);

        paths.Should().ContainSingle().Which.Should().Be(@"E:\fotky\kocka.png");
    }

    [Fact]
    public void ParseAttachmentPaths_MultipleImages_ExtractsAll()
    {
        var content = @"![obrázek](a.png)" + "\n" + @"![obrázek](b.jpg)" + "\ntext";
        var paths = ChatMessage.ParseAttachmentPaths(content);

        paths.Should().HaveCount(2);
        paths.Should().ContainInOrder("a.png", "b.jpg");
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("jen text bez obrázku", 0)]
    [InlineData(null, 0)]
    public void ParseAttachmentPaths_NoImages_Empty(string? content, int expected)
    {
        ChatMessage.ParseAttachmentPaths(content).Should().HaveCount(expected);
    }

    [Fact]
    public void StripAttachmentMarkdown_RemovesImageKeepsText()
    {
        var content = @"![obrázek](E:\x.png)" + "\nudělej černobílou";
        ChatMessage.StripAttachmentMarkdown(content).Should().Be("udělej černobílou");
    }

    [Fact]
    public void StripAttachmentMarkdown_OnlyImage_ReturnsEmpty()
    {
        ChatMessage.StripAttachmentMarkdown(@"![obrázek](a.png)").Should().BeEmpty();
    }

    [Fact]
    public void Content_RoundTrip_PropertiesReflectAttachments()
    {
        var msg = new ChatMessage { Content = @"![obrázek](a.png)" + "\nahoj" };
        msg.HasAttachments.Should().BeTrue();
        msg.AttachmentPaths.Should().ContainSingle().Which.Should().Be("a.png");
        msg.DisplayText.Should().Be("ahoj");
    }
}
