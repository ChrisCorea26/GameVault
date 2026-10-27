using Microsoft.Data.Sqlite;

namespace GameVault.Library.Data;

/// <summary>
/// The library database (<c>library.db</c>): catalog, files, collection and playtime. Metadata only, never
/// ROM contents. The schema version is kept in <c>PRAGMA user_version</c> and migrated on open.
/// </summary>
public sealed class LibraryDatabase : IDisposable
{
    public const int SchemaVersion = 3;

    private readonly string _connectionString;
    private readonly SqliteConnection? _keepAlive;

    /// <param name="path">A file path, or ":memory:" for a private in-memory database (tests, sample data).</param>
    public LibraryDatabase(string path)
    {
        if (path == ":memory:")
        {
            // Shared-cache in-memory DB that lives as long as this object holds a connection open.
            _connectionString = $"Data Source=gv-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            _keepAlive = new SqliteConnection(_connectionString);
            _keepAlive.Open();
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        }

        Migrate();
    }

    /// <summary>Closes pooled connections so the file can be moved or deleted (Windows keeps it locked otherwise).</summary>
    public void Dispose()
    {
        using (var connection = new SqliteConnection(_connectionString))
        {
            SqliteConnection.ClearPool(connection);
        }

        _keepAlive?.Dispose();
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Migrate()
    {
        using var connection = Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode = WAL;";
            pragma.ExecuteNonQuery();
        }

        var version = Scalar<long>(connection, "PRAGMA user_version;");
        if (version > SchemaVersion)
        {
            throw new InvalidOperationException(
                $"The library was created by a newer version of GameVault (schema {version}).");
        }

        if (version < 1)
        {
            Execute(connection, """
                CREATE TABLE catalog (
                    key TEXT PRIMARY KEY,
                    title TEXT NOT NULL,
                    region INTEGER NOT NULL DEFAULT 0,
                    publisher TEXT,
                    developer TEXT,
                    year INTEGER,
                    genre TEXT,
                    players INTEGER NOT NULL DEFAULT 1,
                    accessories TEXT,
                    mapper INTEGER,
                    prg_size INTEGER,
                    chr_size INTEGER,
                    crc32 TEXT,
                    sha1 TEXT,
                    licensed INTEGER NOT NULL DEFAULT 1,
                    source TEXT NOT NULL
                );
                CREATE INDEX catalog_crc32 ON catalog(crc32);
                CREATE TABLE folders (path TEXT PRIMARY KEY);
                CREATE TABLE files (
                    path TEXT PRIMARY KEY,
                    size INTEGER NOT NULL,
                    mtime INTEGER NOT NULL,
                    crc32 TEXT NOT NULL,
                    sha1 TEXT NOT NULL,
                    game_key TEXT NOT NULL
                );
                CREATE INDEX files_game ON files(game_key);
                CREATE TABLE collection (
                    game_key TEXT PRIMARY KEY,
                    owned INTEGER NOT NULL DEFAULT 0,
                    status INTEGER NOT NULL DEFAULT 0,
                    favorite INTEGER NOT NULL DEFAULT 0,
                    rating INTEGER,
                    notes TEXT,
                    cover_path TEXT,
                    custom_title TEXT,
                    playtime_seconds REAL NOT NULL DEFAULT 0,
                    last_played INTEGER,
                    overrides TEXT
                );
                PRAGMA user_version = 1;
                """);
        }

        if (version < 2)
        {
            // Phase 11: games for more systems. Existing rows are NES (0).
            Execute(connection, """
                ALTER TABLE files ADD COLUMN system INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE catalog ADD COLUMN system INTEGER NOT NULL DEFAULT 0;
                PRAGMA user_version = 2;
                """);
        }

        if (version < 3)
        {
            // GV-011: the details of games no catalog lists (homebrew, unknown dumps) that the player fills in.
            Execute(connection, """
                ALTER TABLE collection ADD COLUMN custom_region INTEGER;
                ALTER TABLE collection ADD COLUMN custom_publisher TEXT;
                ALTER TABLE collection ADD COLUMN custom_developer TEXT;
                ALTER TABLE collection ADD COLUMN custom_year INTEGER;
                ALTER TABLE collection ADD COLUMN custom_genre TEXT;
                ALTER TABLE collection ADD COLUMN custom_players INTEGER;
                PRAGMA user_version = 3;
                """);
        }
    }

    internal static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    internal static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }
}
