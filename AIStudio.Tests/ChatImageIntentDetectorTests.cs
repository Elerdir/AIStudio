using AIStudio.Core.Models;
using AIStudio.Infrastructure.Services;
using FluentAssertions;

namespace AIStudio.Tests;

public class ChatImageIntentDetectorTests
{
    private readonly ChatImageIntentDetector _detector = new();

    // ── Klasický chat — žádné keywords ────────────────────────────────────────

    [Theory]
    [InlineData("Ahoj, jak se máš?")]
    [InlineData("Co je hlavní město Francie?")]
    [InlineData("Napiš mi funkci v Pythonu pro reverzaci stringu.")]
    [InlineData("")]
    [InlineData("   ")]
    public void Detect_PlainChat_ReturnsChat(string message)
    {
        _detector.Detect(message, lastAssistantHadImage: false)
                 .Should().Be(ChatImageIntent.Chat);
    }

    // ── Silná slovesa stačí sama (nakresli, namaluj, draw, paint) ─────────────

    [Theory]
    [InlineData("Nakresli mi kočku na střeše.")]
    [InlineData("nakresli draka")]
    [InlineData("Namaluj západ slunce nad mořem.")]
    [InlineData("Vyfotografuj horskou krajinu v noci.")]
    [InlineData("Vyfoť mi auto na pláži")]
    [InlineData("Draw a cat sitting on the roof.")]
    [InlineData("Paint a sunset over the sea.")]
    [InlineData("Sketch a dragon breathing fire.")]
    public void Detect_StrongImageVerb_ReturnsGenerateImage(string message)
    {
        _detector.Detect(message, lastAssistantHadImage: false)
                 .Should().Be(ChatImageIntent.GenerateImage);
    }

    // ── Generické sloveso vyžaduje image substantivum ─────────────────────────

    [Theory]
    [InlineData("Vygeneruj obrázek psa.")]
    [InlineData("Vygeneruj mi obrázek psa.")]
    [InlineData("Vygeneruj mi nějaký obrázek krajiny.")]
    [InlineData("Vytvoř scénu z lesa.")]
    [InlineData("Udělej fotku auta.")]
    [InlineData("Udělej mi obrázek západu slunce.")]
    [InlineData("Ukaž mi obrázek hor.")]
    [InlineData("Generate an image of a dog.")]
    [InlineData("Create a picture of a forest.")]
    [InlineData("Make a photo of a sunset.")]
    [InlineData("Show me a scene with mountains.")]
    [InlineData("Render an image of a spaceship.")]
    public void Detect_GenericVerbWithImageNoun_ReturnsGenerateImage(string message)
    {
        _detector.Detect(message, lastAssistantHadImage: false)
                 .Should().Be(ChatImageIntent.GenerateImage);
    }

    // ── Přirozené obraty bez slovesa generování (chci/potřebuju/dej mi/pošli mi) ───
    // "Chci obrázek psa" je v chatu úplně normální fráze — nezůstává v chatu.

    [Theory]
    [InlineData("Chci obrázek psa.")]
    [InlineData("Chtěl bych obrázek hor.")]
    [InlineData("Potřebuju fotku auta.")]
    [InlineData("Potřebuji ilustraci draka.")]
    [InlineData("Dej mi obrázek západu slunce.")]
    [InlineData("Pošli mi obrázek koťátka.")]
    [InlineData("I want an image of a cat.")]
    [InlineData("I need a picture of a house.")]
    [InlineData("Give me a photo of a mountain.")]
    [InlineData("Send me an illustration of a robot.")]
    public void Detect_NaturalRequestPhrases_ReturnsGenerateImage(string message)
    {
        _detector.Detect(message, lastAssistantHadImage: false)
                 .Should().Be(ChatImageIntent.GenerateImage);
    }

    // ── Plurál a další pády musí taky chytit ──────────────────────────────────

    [Theory]
    [InlineData("Vygeneruj mi nějaké obrázky koček.")]
    [InlineData("Ukaž mi fotky z dovolené.")]
    [InlineData("Vytvoř ilustrace k pohádce.")]
    [InlineData("Generate some pictures of dogs.")]
    public void Detect_PluralAndCases_ReturnsGenerateImage(string message)
    {
        _detector.Detect(message, lastAssistantHadImage: false)
                 .Should().Be(ChatImageIntent.GenerateImage);
    }

    // ── Generické sloveso BEZ image substantiva → zůstává chat ────────────────
    // (false-positive prevention: "vygeneruj kód" je code generation, ne obrázek)

    [Theory]
    [InlineData("Vygeneruj kód pro Fibonacciho posloupnost.")]
    [InlineData("Vytvoř plán cesty po Evropě.")]
    [InlineData("Udělej mi seznam úkolů.")]
    [InlineData("Generate a SQL query.")]
    [InlineData("Create a Python function.")]
    public void Detect_GenericVerbWithoutImageNoun_ReturnsChat(string message)
    {
        _detector.Detect(message, lastAssistantHadImage: false)
                 .Should().Be(ChatImageIntent.Chat);
    }

    // ── Edit follow-up jen pokud byl předchozí obrázek ────────────────────────

    [Theory]
    [InlineData("Udělej to v noci.")]
    [InlineData("Změň barvu na červenou.")]
    [InlineData("Uprav to, ať je tam i pes.")]
    [InlineData("Ale s modrou oblohou.")]
    [InlineData("Místo kočky dej tam psa.")]
    [InlineData("Ještě jednou ale tmavší.")]
    [InlineData("Make it darker.")]
    [InlineData("Change the color to blue.")]
    [InlineData("But with a dog.")]
    [InlineData("Now with mountains.")]
    public void Detect_EditKeywords_WithImageContext_ReturnsEdit(string message)
    {
        _detector.Detect(message, lastAssistantHadImage: true)
                 .Should().Be(ChatImageIntent.EditPreviousImage);
    }

    [Theory]
    [InlineData("Udělej to v noci.")]
    [InlineData("Změň přístup, použij rekurzi.")]
    [InlineData("Uprav ten kód.")]
    public void Detect_EditKeywords_WithoutImageContext_ReturnsChat(string message)
    {
        // Bez předchozího obrázku jsou edit slova bez váhy — vrátí Chat
        _detector.Detect(message, lastAssistantHadImage: false)
                 .Should().Be(ChatImageIntent.Chat);
    }

    // ── Edit má prioritu nad GenerateImage když je kontext obrázek ────────────

    [Fact]
    public void Detect_EditWinsOverGenerate_WhenImageContext()
    {
        // "Nakresli místo kočky psa." — má strong verb i edit keyword.
        // Pokud byl předchozí obrázek, intent je úprava existujícího (img2img).
        _detector.Detect("Místo kočky tam dej psa.", lastAssistantHadImage: true)
                 .Should().Be(ChatImageIntent.EditPreviousImage);
    }

    // ── Case-insensitivita + interpunkce ──────────────────────────────────────

    [Theory]
    [InlineData("NAKRESLI KOCKU!")]
    [InlineData("nakresli, prosím, koťátko.")]
    [InlineData("Můžeš mi nakreslit psa?")]
    public void Detect_CaseAndPunctuation_DoesntMatter(string message)
    {
        _detector.Detect(message, lastAssistantHadImage: false)
                 .Should().Be(ChatImageIntent.GenerateImage);
    }
}
