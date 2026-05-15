using Microsoft.Data.Sqlite;
using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;

namespace AIStudio.Infrastructure.Services;

public sealed class SqliteImageRepository : SqliteRepositoryBase, IImageRepository
{
    private static readonly string DbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AIStudio", "images.db");

    public SqliteImageRepository()
        : base($"Data Source={DbPath};Mode=ReadWriteCreate;Pooling=False") { }

    internal SqliteImageRepository(string connectionString)
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
                PRAGMA cache_size   = -8192;
                PRAGMA temp_store   = MEMORY;

                CREATE TABLE IF NOT EXISTS Images (
                    Id          TEXT PRIMARY KEY,
                    FilePath    TEXT NOT NULL,
                    Prompt      TEXT NOT NULL DEFAULT '',
                    ModelName   TEXT NOT NULL DEFAULT '',
                    Seed        INTEGER NOT NULL DEFAULT 0,
                    Width       INTEGER NOT NULL DEFAULT 0,
                    Height      INTEGER NOT NULL DEFAULT 0,
                    Steps       INTEGER NOT NULL DEFAULT 0,
                    Cfg         REAL    NOT NULL DEFAULT 0,
                    GeneratedAt TEXT    NOT NULL
                );

                CREATE INDEX IF NOT EXISTS IX_Images_GeneratedAt
                    ON Images(GeneratedAt DESC);
                """;

            await cmd.ExecuteNonQueryAsync();

            // Migration: add Sampler/Scheduler columns if they don't exist yet
            cmd.CommandText = "ALTER TABLE Images ADD COLUMN Sampler   TEXT NOT NULL DEFAULT ''";
            try { await cmd.ExecuteNonQueryAsync(); } catch { /* column already exists */ }
            cmd.CommandText = "ALTER TABLE Images ADD COLUMN Scheduler TEXT NOT NULL DEFAULT ''";
            try { await cmd.ExecuteNonQueryAsync(); } catch { /* column already exists */ }

            cmd.CommandText = "SELECT 1"; // reset so outer ExecuteNonQueryAsync is a no-op

            await cmd.ExecuteNonQueryAsync();
            Log.Information("SQLite image repository initialized at {DbPath}", DbPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize SQLite image database at {DbPath}", DbPath);
            throw;
        }
    }

    // ── Read ───────────────────────────────────────────────────────────────────

    private const string SelectColumns =
        "Id, FilePath, Prompt, ModelName, Seed, Width, Height, Steps, Cfg, Sampler, Scheduler, GeneratedAt";

    public async Task<IReadOnlyList<ImageRecord>> LoadAllImagesAsync()
    {
        try
        {
            await using var conn = await OpenAsync();
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = $"SELECT {SelectColumns} FROM Images ORDER BY GeneratedAt DESC";

            var list = await ReadImagesAsync(cmd);
            Log.Debug("Loaded {Count} image records from DB (LoadAll)", list.Count);
            return list;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load images from DB");
            return Array.Empty<ImageRecord>();
        }
    }

    public async Task<IReadOnlyList<ImageRecord>> LoadImagesPagedAsync(int skip, int take)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) return Array.Empty<ImageRecord>();

        try
        {
            await using var conn = await OpenAsync();
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = $"SELECT {SelectColumns} FROM Images " +
                              "ORDER BY GeneratedAt DESC LIMIT $take OFFSET $skip";
            cmd.Parameters.AddWithValue("$take", take);
            cmd.Parameters.AddWithValue("$skip", skip);

            var list = await ReadImagesAsync(cmd);
            Log.Debug("Loaded {Count} image records (skip={Skip}, take={Take})",
                      list.Count, skip, take);
            return list;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load paged images from DB (skip={Skip}, take={Take})", skip, take);
            return Array.Empty<ImageRecord>();
        }
    }

    public async Task<int> CountImagesAsync()
    {
        try
        {
            await using var conn = await OpenAsync();
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Images";
            var raw = await cmd.ExecuteScalarAsync();
            return raw is long l ? (int)l : Convert.ToInt32(raw);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to count images in DB");
            return 0;
        }
    }

    /// <summary>Společný čtecí helper — všechny SELECT mají stejný column order (viz <see cref="SelectColumns"/>).</summary>
    private static async Task<List<ImageRecord>> ReadImagesAsync(SqliteCommand cmd)
    {
        var list = new List<ImageRecord>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new ImageRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetDouble(8),
                reader.GetString(9),
                reader.GetString(10),
                DateTime.Parse(reader.GetString(11))));
        }
        return list;
    }

    // ── Write ──────────────────────────────────────────────────────────────────

    public async Task SaveImageAsync(ImageRecord image)
    {
        try
        {
            await using var conn = await OpenAsync();
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO Images
                    (Id, FilePath, Prompt, ModelName, Seed, Width, Height, Steps, Cfg, Sampler, Scheduler, GeneratedAt)
                VALUES
                    ($id, $path, $prompt, $model, $seed, $w, $h, $steps, $cfg, $sampler, $scheduler, $gen)
                """;
            cmd.Parameters.AddWithValue("$id",        image.Id);
            cmd.Parameters.AddWithValue("$path",      image.FilePath);
            cmd.Parameters.AddWithValue("$prompt",    image.Prompt);
            cmd.Parameters.AddWithValue("$model",     image.ModelName);
            cmd.Parameters.AddWithValue("$seed",      image.Seed);
            cmd.Parameters.AddWithValue("$w",         image.Width);
            cmd.Parameters.AddWithValue("$h",         image.Height);
            cmd.Parameters.AddWithValue("$steps",     image.Steps);
            cmd.Parameters.AddWithValue("$cfg",       image.Cfg);
            cmd.Parameters.AddWithValue("$sampler",   image.Sampler);
            cmd.Parameters.AddWithValue("$scheduler", image.Scheduler);
            cmd.Parameters.AddWithValue("$gen",       image.GeneratedAt.ToString("o"));
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save image record {Id}", image.Id);
            throw;
        }
    }

    public async Task DeleteImageAsync(string id)
    {
        try
        {
            await using var conn = await OpenAsync();
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Images WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync();
            Log.Debug("Deleted image record {Id}", id);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to delete image record {Id}", id);
            throw;
        }
    }

}
