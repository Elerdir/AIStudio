using Microsoft.Data.Sqlite;
using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Core.Services;

namespace AIStudio.Infrastructure.Services;

public sealed class SqliteChatRepository : SqliteRepositoryBase, IChatRepository
{
    private static readonly string DbPath = AppPaths.ConversationsDbPath;

    public SqliteChatRepository()
        : base($"Data Source={DbPath};Mode=ReadWriteCreate;Pooling=False") { }

    internal SqliteChatRepository(string connectionString)
        : base(connectionString) { }

    // ── Init ───────────────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);

        try
        {
            await using var conn = await OpenAsync();
            await using var cmd  = conn.CreateCommand();

            cmd.CommandText = """
                PRAGMA journal_mode = DELETE;
                PRAGMA synchronous  = FULL;
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

            // Debug: kolik zpráv máme v DB? Pomáhá detekovat scénáře, kdy se Conversations
            // ukládají, ale Messages se z nějakého důvodu nezapisují.
            await using var statCmd = conn.CreateCommand();
            statCmd.CommandText = "SELECT (SELECT COUNT(*) FROM Conversations), (SELECT COUNT(*) FROM Messages)";
            await using var statReader = await statCmd.ExecuteReaderAsync();
            if (await statReader.ReadAsync())
                Log.Information("SQLite stats: {Convs} konverzací, {Msgs} zpráv",
                                statReader.GetInt64(0), statReader.GetInt64(1));

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

            // ── Migrace pro chat → image gen (vygenerované obrázky inline v chatu) ──
            // ImagePath = cesta k vygenerovanému obrázku (NULL = klasická text zpráva).
            // ImageReferencePath = vstupní obrázek pro img2img follow-up (NULL = txt2img).
            await using var mig6 = conn.CreateCommand();
            mig6.CommandText = "ALTER TABLE Messages ADD COLUMN ImagePath TEXT NULL;";
            try { await mig6.ExecuteNonQueryAsync(); } catch { /* existuje */ }

            await using var mig7 = conn.CreateCommand();
            mig7.CommandText = "ALTER TABLE Messages ADD COLUMN ImageReferencePath TEXT NULL;";
            try { await mig7.ExecuteNonQueryAsync(); } catch { /* existuje */ }

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
                "SELECT Id, ConversationId, Role, Content, Timestamp, OrderIndex, ImagePath, ImageReferencePath " +
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
                    reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
            Log.Information("LoadMessages: conv={ConvId} → {Count} zpráv", conversationId, list.Count);
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
            // UPSERT místo INSERT OR REPLACE — REPLACE dělá DELETE+INSERT, což
            // by spustilo ON DELETE CASCADE na Messages a smazalo všechny zprávy
            // této konverzace! UPSERT (ON CONFLICT … DO UPDATE) provede UPDATE
            // bez DELETE, takže FK CASCADE neproběhne. Tohle byl ten záhadný
            // důvod, proč se zprávy „nezapisovaly".
            cmd.CommandText = """
                INSERT INTO Conversations
                    (Id, Title, ModelName, MaxTokens, SystemPrompt, CreatedAt, UpdatedAt, IsPinned, Temperature, IsThinkingEnabled, Draft)
                VALUES
                    ($id, $title, $model, $tokens, $sysPrompt, $created, $updated, $pinned, $temp, $thinking, $draft)
                ON CONFLICT(Id) DO UPDATE SET
                    Title             = excluded.Title,
                    ModelName         = excluded.ModelName,
                    MaxTokens         = excluded.MaxTokens,
                    SystemPrompt      = excluded.SystemPrompt,
                    UpdatedAt         = excluded.UpdatedAt,
                    IsPinned          = excluded.IsPinned,
                    Temperature       = excluded.Temperature,
                    IsThinkingEnabled = excluded.IsThinkingEnabled,
                    Draft             = excluded.Draft
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
                    (Id, ConversationId, Role, Content, Timestamp, OrderIndex, ImagePath, ImageReferencePath)
                VALUES
                    ($id, $cid, $role, $content, $ts, $order, $img, $imgref)
                """;
            cmd.Parameters.AddWithValue("$id",      message.Id);
            cmd.Parameters.AddWithValue("$cid",     message.ConversationId);
            cmd.Parameters.AddWithValue("$role",    message.Role);
            cmd.Parameters.AddWithValue("$content", message.Content);
            cmd.Parameters.AddWithValue("$ts",      message.Timestamp.ToString("o"));
            cmd.Parameters.AddWithValue("$order",   message.OrderIndex);
            cmd.Parameters.AddWithValue("$img",     (object?)message.ImagePath          ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$imgref",  (object?)message.ImageReferencePath ?? DBNull.Value);
            var rows = await cmd.ExecuteNonQueryAsync();

            // Read-back verify ve stejné connection: ihned se zeptáme, jestli
            // zápis je v DB. Pokud rows=1 ale verify=0, máme problém s transakcí.
            await using var verifyCmd = conn.CreateCommand();
            verifyCmd.CommandText = "SELECT COUNT(*) FROM Messages WHERE Id = $id";
            verifyCmd.Parameters.AddWithValue("$id", message.Id);
            var verifyCount = (long)(await verifyCmd.ExecuteScalarAsync() ?? 0L);

            // Plus: kolik celkem zpráv má teď ta konverzace v DB?
            await using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM Messages WHERE ConversationId = $cid";
            countCmd.Parameters.AddWithValue("$cid", message.ConversationId);
            var convCount = (long)(await countCmd.ExecuteScalarAsync() ?? 0L);

            Log.Information("SaveMessage: conv={ConvId} role={Role} order={Order} len={Len} " +
                            "→ {Rows} řádků (verify={Verify}, conv má teď {ConvCount} zpráv)",
                message.ConversationId, message.Role, message.OrderIndex,
                message.Content?.Length ?? 0, rows, verifyCount, convCount);
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
            var rows = await cmd.ExecuteNonQueryAsync();
            Log.Information("DeleteConversation: id={Id} → {Rows} řádků (CASCADE smazal zprávy)",
                conversationId, rows);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to delete conversation {Id}", conversationId);
            throw;
        }
    }

    public async Task ClearAllConversationsAsync()
    {
        try
        {
            await using var conn = await OpenAsync();
            // Pořadí: nejdřív zprávy (jistota i bez CASCADE), pak konverzace.
            // Použijeme transakci, aby se obě tabulky updatovaly atomicky;
            // jinak by se mohlo stát, že napůl smazaná data zůstanou v DB.
            await using var tx = (Microsoft.Data.Sqlite.SqliteTransaction)await conn.BeginTransactionAsync();
            try
            {
                await using (var cmdMsg = conn.CreateCommand())
                {
                    cmdMsg.Transaction = tx;
                    cmdMsg.CommandText = "DELETE FROM Messages";
                    var rowsMsg = await cmdMsg.ExecuteNonQueryAsync();
                    Log.Information("ClearAll: smazáno {Rows} zpráv", rowsMsg);
                }

                await using (var cmdConv = conn.CreateCommand())
                {
                    cmdConv.Transaction = tx;
                    cmdConv.CommandText = "DELETE FROM Conversations";
                    var rowsConv = await cmdConv.ExecuteNonQueryAsync();
                    Log.Information("ClearAll: smazáno {Rows} konverzací", rowsConv);
                }

                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ClearAllConversations selhalo");
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
            var rows = await cmd.ExecuteNonQueryAsync();
            Log.Information("DeleteMessagesFromIndex: conv={ConvId} fromIdx={Idx} → {Rows} řádků",
                conversationId, fromOrderIndex, rows);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to delete messages from index {Idx} in {ConvId}",
                fromOrderIndex, conversationId);
            throw;
        }
    }

}
