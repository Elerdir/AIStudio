using Microsoft.Data.Sqlite;
using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;

namespace AIStudio.Infrastructure.Services;

public sealed class SqliteChatRepository : IChatRepository
{
    private static readonly string DbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AIStudio", "conversations.db");

    private readonly string _connectionString =
        $"Data Source={DbPath};Mode=ReadWriteCreate;Cache=Shared";

    // ── Init ───────────────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);

        try
        {
            await using var conn = await OpenAsync();
            await using var cmd  = conn.CreateCommand();

            cmd.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous  = NORMAL;
                PRAGMA cache_size   = -16384;
                PRAGMA temp_store   = MEMORY;

                CREATE TABLE IF NOT EXISTS Conversations (
                    Id           TEXT PRIMARY KEY,
                    Title        TEXT NOT NULL,
                    ModelName    TEXT NOT NULL DEFAULT '',
                    MaxTokens    INTEGER NOT NULL DEFAULT 4096,
                    SystemPrompt TEXT NOT NULL DEFAULT '',
                    CreatedAt    TEXT NOT NULL,
                    UpdatedAt    TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS Messages (
                    Id             TEXT PRIMARY KEY,
                    ConversationId TEXT NOT NULL REFERENCES Conversations(Id) ON DELETE CASCADE,
                    Role           TEXT NOT NULL,
                    Content        TEXT NOT NULL,
                    Timestamp      TEXT NOT NULL,
                    OrderIndex     INTEGER NOT NULL
                );

                CREATE INDEX IF NOT EXISTS IX_Messages_ConvId
                    ON Messages(ConversationId, OrderIndex);
                """;

            await cmd.ExecuteNonQueryAsync();

            // ── Migrace: přidej sloupce pokud ještě neexistují ──────────────────────
            await using var mig1 = conn.CreateCommand();
            mig1.CommandText = "ALTER TABLE Conversations ADD COLUMN SystemPrompt TEXT NOT NULL DEFAULT '';";
            try { await mig1.ExecuteNonQueryAsync(); } catch { /* existuje */ }

            await using var mig2 = conn.CreateCommand();
            mig2.CommandText = "ALTER TABLE Conversations ADD COLUMN IsPinned INTEGER NOT NULL DEFAULT 0;";
            try { await mig2.ExecuteNonQueryAsync(); } catch { /* existuje */ }

            await using var mig3 = conn.CreateCommand();
            mig3.CommandText = "ALTER TABLE Conversations ADD COLUMN Temperature REAL NOT NULL DEFAULT 0.7;";
            try { await mig3.ExecuteNonQueryAsync(); } catch { /* existuje */ }

            await using var mig4 = conn.CreateCommand();
            mig4.CommandText = "ALTER TABLE Conversations ADD COLUMN IsThinkingEnabled INTEGER NOT NULL DEFAULT 1;";
            try { await mig4.ExecuteNonQueryAsync(); } catch { /* existuje */ }

            await using var mig5 = conn.CreateCommand();
            mig5.CommandText = "ALTER TABLE Conversations ADD COLUMN Draft TEXT NOT NULL DEFAULT '';";
            try { await mig5.ExecuteNonQueryAsync(); } catch { /* existuje */ }

            Log.Information("SQLite initialized at {DbPath}", DbPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize SQLite database at {DbPath}", DbPath);
            throw;
        }
    }

    // ── Read ───────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ConversationRecord>> LoadAllConversationsAsync()
    {
        try
        {
            await using var conn   = await OpenAsync();
            await using var cmd    = conn.CreateCommand();
            cmd.CommandText =
                "SELECT Id, Title, ModelName, MaxTokens, SystemPrompt, CreatedAt, UpdatedAt, IsPinned, Temperature, IsThinkingEnabled, Draft " +
                "FROM Conversations ORDER BY IsPinned DESC, UpdatedAt DESC";

            var list = new List<ConversationRecord>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new ConversationRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetString(4),
                    DateTime.Parse(reader.GetString(5)),
                    DateTime.Parse(reader.GetString(6)),
                    reader.GetInt32(7) != 0,
                    (float)reader.GetDouble(8),
                    reader.GetInt32(9) != 0,
                    reader.GetString(10)));
            }
            Log.Debug("Loaded {Count} conversations from DB", list.Count);
            return list;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load conversations from DB");
            return Array.Empty<ConversationRecord>();
        }
    }

    public async Task<IReadOnlyList<MessageRecord>> LoadMessagesAsync(string conversationId)
    {
        try
        {
            await using var conn = await OpenAsync();
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText =
                "SELECT Id, ConversationId, Role, Content, Timestamp, OrderIndex " +
                "FROM Messages WHERE ConversationId = $cid ORDER BY OrderIndex";
            cmd.Parameters.AddWithValue("$cid", conversationId);

            var list = new List<MessageRecord>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new MessageRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    DateTime.Parse(reader.GetString(4)),
                    reader.GetInt32(5)));
            }
            return list;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load messages for conversation {ConversationId}", conversationId);
            return Array.Empty<MessageRecord>();
        }
    }

    // ── Write ──────────────────────────────────────────────────────────────────

    public async Task SaveConversationAsync(ConversationRecord conversation)
    {
        try
        {
            await using var conn = await OpenAsync();
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO Conversations
                    (Id, Title, ModelName, MaxTokens, SystemPrompt, CreatedAt, UpdatedAt, IsPinned, Temperature, IsThinkingEnabled, Draft)
                VALUES
                    ($id, $title, $model, $tokens, $sysPrompt, $created, $updated, $pinned, $temp, $thinking, $draft)
                """;
            cmd.Parameters.AddWithValue("$id",        conversation.Id);
            cmd.Parameters.AddWithValue("$title",     conversation.Title);
            cmd.Parameters.AddWithValue("$model",     conversation.ModelName);
            cmd.Parameters.AddWithValue("$tokens",    conversation.MaxTokens);
            cmd.Parameters.AddWithValue("$sysPrompt", conversation.SystemPrompt);
            cmd.Parameters.AddWithValue("$created",   conversation.CreatedAt.ToString("o"));
            cmd.Parameters.AddWithValue("$updated",   conversation.UpdatedAt.ToString("o"));
            cmd.Parameters.AddWithValue("$pinned",    conversation.IsPinned ? 1 : 0);
            cmd.Parameters.AddWithValue("$temp",      conversation.Temperature);
            cmd.Parameters.AddWithValue("$thinking",  conversation.IsThinkingEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$draft",     conversation.Draft);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save conversation {Id}", conversation.Id);
            throw;
        }
    }

    public async Task SaveMessageAsync(MessageRecord message)
    {
        try
        {
            await using var conn = await OpenAsync();
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO Messages
                    (Id, ConversationId, Role, Content, Timestamp, OrderIndex)
                VALUES
                    ($id, $cid, $role, $content, $ts, $order)
                """;
            cmd.Parameters.AddWithValue("$id",      message.Id);
            cmd.Parameters.AddWithValue("$cid",     message.ConversationId);
            cmd.Parameters.AddWithValue("$role",    message.Role);
            cmd.Parameters.AddWithValue("$content", message.Content);
            cmd.Parameters.AddWithValue("$ts",      message.Timestamp.ToString("o"));
            cmd.Parameters.AddWithValue("$order",   message.OrderIndex);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save message {Id} for conversation {ConvId}",
                message.Id, message.ConversationId);
            throw;
        }
    }

    public async Task DeleteConversationAsync(string conversationId)
    {
        try
        {
            await using var conn = await OpenAsync();
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Conversations WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", conversationId);
            await cmd.ExecuteNonQueryAsync();
            Log.Debug("Deleted conversation {Id} (CASCADE removed messages)", conversationId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to delete conversation {Id}", conversationId);
            throw;
        }
    }

    public async Task DeleteMessagesFromIndexAsync(string conversationId, int fromOrderIndex)
    {
        try
        {
            await using var conn = await OpenAsync();
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText =
                "DELETE FROM Messages WHERE ConversationId = $cid AND OrderIndex >= $idx";
            cmd.Parameters.AddWithValue("$cid", conversationId);
            cmd.Parameters.AddWithValue("$idx", fromOrderIndex);
            await cmd.ExecuteNonQueryAsync();
            Log.Debug("Deleted messages from index {Idx} in {ConvId}", fromOrderIndex, conversationId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to delete messages from index {Idx} in {ConvId}",
                fromOrderIndex, conversationId);
            throw;
        }
    }

    // ── Helper ─────────────────────────────────────────────────────────────────

    private async Task<SqliteConnection> OpenAsync()
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        await pragma.ExecuteNonQueryAsync();

        return conn;
    }
}
