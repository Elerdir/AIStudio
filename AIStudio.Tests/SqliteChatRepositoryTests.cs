using AIStudio.Core.Models;
using AIStudio.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace AIStudio.Tests;

/// <summary>
/// Integrace se SQLite přes pojmenovanou sdílenou :memory: databázi.
/// Trick: udržujeme jednu "anchor" connection otevřenou po celou dobu testu —
/// jinak SQLite zahodí in-memory DB při zavření poslední connection.
/// </summary>
public class SqliteChatRepositoryTests : IAsyncLifetime
{
    // Unikátní název pro každou instanci třídy (xUnit vytváří novou instanci per test)
    private readonly string _dbName = $"chattest_{Guid.NewGuid():N}";
    private string ConnStr => $"Data Source=file:{_dbName}?mode=memory&cache=shared";

    private SqliteChatRepository _repo = null!;
    private SqliteConnection     _anchor = null!; // udržuje in-memory DB naživu

    public async Task InitializeAsync()
    {
        _anchor = new SqliteConnection(ConnStr);
        await _anchor.OpenAsync(); // otevřeno po celý test

        _repo = new SqliteChatRepository(ConnStr);
        await _repo.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _anchor.CloseAsync();
        _anchor.Dispose();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ConversationRecord MakeConversation(string? id = null) => new(
        Id:           id ?? Guid.NewGuid().ToString(),
        Title:        "Test konverzace",
        ModelName:    "Phi-4 Q4_K_M",
        MaxTokens:    2048,
        SystemPrompt: "",
        CreatedAt:    DateTime.UtcNow,
        UpdatedAt:    DateTime.UtcNow);

    private static MessageRecord MakeMessage(string conversationId, int order, string role = "user") => new(
        Id:             Guid.NewGuid().ToString(),
        ConversationId: conversationId,
        Role:           role,
        Content:        $"Zpráva #{order}",
        Timestamp:      DateTime.UtcNow,
        OrderIndex:     order);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAllConversations_EmptyDb_ReturnsEmpty()
    {
        var result = await _repo.LoadAllConversationsAsync();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveConversation_ThenLoad_RoundTrips()
    {
        var conv = MakeConversation();
        await _repo.SaveConversationAsync(conv);

        var all = await _repo.LoadAllConversationsAsync();

        all.Should().HaveCount(1);
        var loaded = all[0];
        loaded.Id.Should().Be(conv.Id);
        loaded.Title.Should().Be(conv.Title);
        loaded.ModelName.Should().Be(conv.ModelName);
        loaded.MaxTokens.Should().Be(conv.MaxTokens);
    }

    [Fact]
    public async Task SaveConversation_UpdateExisting_OverwritesTitle()
    {
        var conv = MakeConversation();
        await _repo.SaveConversationAsync(conv);

        var updated = conv with { Title = "Přejmenováno" };
        await _repo.SaveConversationAsync(updated);

        var all = await _repo.LoadAllConversationsAsync();
        all.Should().HaveCount(1);
        all[0].Title.Should().Be("Přejmenováno");
    }

    [Fact]
    public async Task SaveMessage_ThenLoadMessages_RoundTrips()
    {
        var conv = MakeConversation();
        await _repo.SaveConversationAsync(conv);

        var msg = MakeMessage(conv.Id, 0);
        await _repo.SaveMessageAsync(msg);

        var messages = await _repo.LoadMessagesAsync(conv.Id);
        messages.Should().HaveCount(1);
        messages[0].Content.Should().Be(msg.Content);
        messages[0].Role.Should().Be("user");
    }

    [Fact]
    public async Task LoadMessages_OrderedByOrderIndex()
    {
        var conv = MakeConversation();
        await _repo.SaveConversationAsync(conv);

        // Uložíme v obrácené pořadí
        await _repo.SaveMessageAsync(MakeMessage(conv.Id, 2, "assistant"));
        await _repo.SaveMessageAsync(MakeMessage(conv.Id, 0, "user"));
        await _repo.SaveMessageAsync(MakeMessage(conv.Id, 1, "assistant"));

        var messages = await _repo.LoadMessagesAsync(conv.Id);
        messages.Should().HaveCount(3);
        messages.Select(m => m.OrderIndex).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task DeleteConversation_RemovesConvAndMessages()
    {
        var conv = MakeConversation();
        await _repo.SaveConversationAsync(conv);
        await _repo.SaveMessageAsync(MakeMessage(conv.Id, 0));
        await _repo.SaveMessageAsync(MakeMessage(conv.Id, 1));

        await _repo.DeleteConversationAsync(conv.Id);

        var all = await _repo.LoadAllConversationsAsync();
        all.Should().BeEmpty();

        var msgs = await _repo.LoadMessagesAsync(conv.Id);
        msgs.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteMessagesFromIndex_RemovesOnlyTailMessages()
    {
        var conv = MakeConversation();
        await _repo.SaveConversationAsync(conv);

        for (int i = 0; i < 5; i++)
            await _repo.SaveMessageAsync(MakeMessage(conv.Id, i));

        await _repo.DeleteMessagesFromIndexAsync(conv.Id, fromOrderIndex: 3);

        var messages = await _repo.LoadMessagesAsync(conv.Id);
        messages.Should().HaveCount(3);
        messages.Select(m => m.OrderIndex).Should().Equal(0, 1, 2);
    }

    [Fact]
    public async Task ClearAllConversations_EmptiesEverything()
    {
        var conv1 = MakeConversation();
        var conv2 = MakeConversation();
        await _repo.SaveConversationAsync(conv1);
        await _repo.SaveConversationAsync(conv2);
        await _repo.SaveMessageAsync(MakeMessage(conv1.Id, 0));
        await _repo.SaveMessageAsync(MakeMessage(conv2.Id, 0));

        await _repo.ClearAllConversationsAsync();

        (await _repo.LoadAllConversationsAsync()).Should().BeEmpty();
        (await _repo.LoadMessagesAsync(conv1.Id)).Should().BeEmpty();
        (await _repo.LoadMessagesAsync(conv2.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task MultipleConversations_LoadedNewestFirst()
    {
        var older = MakeConversation() with { UpdatedAt = DateTime.UtcNow.AddHours(-2) };
        var newer = MakeConversation() with { UpdatedAt = DateTime.UtcNow };
        await _repo.SaveConversationAsync(older);
        await _repo.SaveConversationAsync(newer);

        var all = await _repo.LoadAllConversationsAsync();
        all.Should().HaveCount(2);
        all[0].Id.Should().Be(newer.Id);
        all[1].Id.Should().Be(older.Id);
    }
}
