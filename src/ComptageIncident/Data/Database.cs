using ComptageIncident.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace ComptageIncident.Data;

/// <summary>Fabrique de connexions SQLite et création du schéma.</summary>
public sealed class Database
{
    private readonly string _connectionString;

    public Database(IOptions<DatabaseOptions> options, IHostEnvironment env)
        : this(ResolvePath(options.Value.Path, env.ContentRootPath))
    {
    }

    public Database(string filePath)
    {
        FilePath = filePath;
        var dir = System.IO.Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
        Initialize();
    }

    public string FilePath { get; }

    private static string ResolvePath(string path, string contentRoot) =>
        System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.GetFullPath(System.IO.Path.Combine(contentRoot, path));

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS Incidents (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                Title       TEXT NOT NULL,
                Type        TEXT NOT NULL,
                Location    TEXT,
                CreatedAt   TEXT NOT NULL,
                CreatedBy   TEXT NOT NULL,
                ClosedAt    TEXT,
                ClosedBy    TEXT,
                DeletedAt   TEXT,
                DeletedBy   TEXT
            );

            CREATE TABLE IF NOT EXISTS Persons (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                IncidentId  INTEGER NOT NULL REFERENCES Incidents(Id) ON DELETE CASCADE,
                Kind        TEXT NOT NULL CHECK (Kind IN ('internal', 'visitor')),
                DisplayName TEXT NOT NULL,
                LastName    TEXT,
                FirstName   TEXT,
                Login       TEXT,
                Department  TEXT,
                Company     TEXT,
                AddedAt     TEXT NOT NULL,
                AddedBy     TEXT NOT NULL,
                RemovedAt   TEXT,
                RemovedBy   TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_Persons_Incident ON Persons(IncidentId);
            CREATE UNIQUE INDEX IF NOT EXISTS UX_Persons_ActiveLogin
                ON Persons(IncidentId, Login COLLATE NOCASE)
                WHERE Kind = 'internal' AND RemovedAt IS NULL;

            CREATE TABLE IF NOT EXISTS AuditLog (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                IncidentId  INTEGER REFERENCES Incidents(Id) ON DELETE SET NULL,
                At          TEXT NOT NULL,
                User        TEXT NOT NULL,
                Action      TEXT NOT NULL,
                Details     TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_AuditLog_Incident ON AuditLog(IncidentId);
            """;
        cmd.ExecuteNonQuery();
    }
}
