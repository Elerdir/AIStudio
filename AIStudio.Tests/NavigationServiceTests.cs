using AIStudio.Core.Enums;
using AIStudio.Infrastructure.Services;
using FluentAssertions;

namespace AIStudio.Tests;

public class NavigationServiceTests
{
    // ── Navigate vyvolá PageChanged ───────────────────────────────────────────

    [Fact]
    public void Navigate_FiresPageChangedWithCorrectPage()
    {
        var svc      = new NavigationService();
        NavigationPage? received = null;
        svc.PageChanged += p => received = p;

        svc.Navigate(NavigationPage.Chat);

        received.Should().Be(NavigationPage.Chat);
    }

    [Theory]
    [InlineData(NavigationPage.Chat)]
    [InlineData(NavigationPage.ImageStudio)]
    [InlineData(NavigationPage.Models)]
    [InlineData(NavigationPage.Settings)]
    [InlineData(NavigationPage.System)]
    public void Navigate_AllPages_FireCorrectValue(NavigationPage page)
    {
        var svc = new NavigationService();
        NavigationPage? received = null;
        svc.PageChanged += p => received = p;

        svc.Navigate(page);

        received.Should().Be(page);
    }

    // ── Více odběratelů — každý dostane event ─────────────────────────────────

    [Fact]
    public void Navigate_MultipleSubscribers_AllReceiveEvent()
    {
        var svc  = new NavigationService();
        var log1 = new List<NavigationPage>();
        var log2 = new List<NavigationPage>();

        svc.PageChanged += log1.Add;
        svc.PageChanged += log2.Add;

        svc.Navigate(NavigationPage.Models);
        svc.Navigate(NavigationPage.Settings);

        log1.Should().Equal(NavigationPage.Models, NavigationPage.Settings);
        log2.Should().Equal(NavigationPage.Models, NavigationPage.Settings);
    }

    // ── Odhlášení odběru ──────────────────────────────────────────────────────

    [Fact]
    public void Navigate_AfterUnsubscribe_DoesNotReceiveEvent()
    {
        var svc      = new NavigationService();
        var received = new List<NavigationPage>();

        void Handler(NavigationPage p) => received.Add(p);
        svc.PageChanged += Handler;
        svc.Navigate(NavigationPage.Chat);

        svc.PageChanged -= Handler;
        svc.Navigate(NavigationPage.Settings);

        received.Should().ContainSingle().Which.Should().Be(NavigationPage.Chat);
    }

    // ── Žádní odběratelé — žádná výjimka ─────────────────────────────────────

    [Fact]
    public void Navigate_NoSubscribers_DoesNotThrow()
    {
        var svc = new NavigationService();

        var act = () => svc.Navigate(NavigationPage.ImageStudio);

        act.Should().NotThrow();
    }

    // ── Pořadí událostí ───────────────────────────────────────────────────────

    [Fact]
    public void Navigate_MultipleCalls_EventsInOrder()
    {
        var svc = new NavigationService();
        var log = new List<NavigationPage>();
        svc.PageChanged += log.Add;

        svc.Navigate(NavigationPage.Chat);
        svc.Navigate(NavigationPage.Models);
        svc.Navigate(NavigationPage.ImageStudio);

        log.Should().Equal(
            NavigationPage.Chat,
            NavigationPage.Models,
            NavigationPage.ImageStudio);
    }
}
