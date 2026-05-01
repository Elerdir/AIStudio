using Microsoft.Data.Sqlite;
using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;

namespace AIStudio.Infrastructure.Services;

public sealed class SqliteImageRepository : IImageRepository
{
    private static readonly string DbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AIStudio", "images.db");

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
            Log.Information("SQLite image repository initialized at {DbPath}", DbPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize SQLite image database at {DbPath}", DbPath);
            throw;
        }
    }

    // ── Read ───────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ImageRecord>> LoadAllImagesAsync()
    {
        try
        {
            await using var conn = await OpenAsync();
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText =
                "SELECT Id, FilePath, Prompt, ModelName, Seed, Width, Height, Steps, Cfg, GeneratedAt " +
                "FROM Images ORDER BY GeneratedAt DESC";

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
                    DateTime.Parse(reader.GetString(9))));
            }

            Log.Debug("Loaded {Count} image records from DB", list.Count);
            return list;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load images from DB");
            return Array.Empty<ImageRecord>();
        }
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
                    (Id, FilePath, Prompt, ModelName, Seed, Width, Height, Steps, Cfg, GeneratedAt)
                VALUES
                    ($id, $path, $prompt, $model, $seed, $w, $h, $steps, $cfg, $gen)
                """;
            cmd.Parameters.AddWithValue("$id",     image.Id);
            cmd.Parameters.AddWithValue("$path",   image.FilePath);
            cmd.Parameters.AddWithValue("$prompt", image.Prompt);
            cmd.Parameters.AddWithValue("$model",  image.ModelName);
            cmd.Parameters.AddWithValue("$seed",   image.Seed);
            cmd.Parameters.AddWithValue("$w",      image.Width);
            cmd.Parameters.AddWithValue("$h",      image.Height);
            cmd.Parameters.AddWithValue("$steps",  image.Steps);
            cmd.Parameters.AddWithValue("$cfg",    image.Cfg);
            cmd.Parameters.AddWithValue("$gen",    image.GeneratedAt.ToString("o"));
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
