using Microsoft.Data.Sqlite;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Sdílená základna pro SQLite repozitáře. Poskytuje OpenAsync() s automaticky
/// aktivovaným PRAGMA foreign_keys = ON při každém otevření spojení.
///
/// Pooling=False je záměrné — s poolem zůstávají connections v paměti a WAL
/// checkpoint se provede až při jejich fyzickém uzavření. Bez poolu garantujeme,
/// že connection.Close() (= using-block end) hned flushne transakce na disk.
/// </summary>
public abstract class SqliteRepositoryBase
{
    private readonly string _connectionString;

    protected SqliteRepositoryBase(string connectionString)
    {
        _connectionString = connectionString;
    }

    protected async Task<SqliteConnection> OpenAsync()
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        await pragma.ExecuteNonQueryAsync();

        return conn;
    }
}
