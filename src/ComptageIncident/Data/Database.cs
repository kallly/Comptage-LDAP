// =====================================================================================================
//  Data/Database.cs : fichier SQLite, connexions et schéma
// =====================================================================================================
//  - Une seule base locale (fichier unique) : pas de serveur de base de données à installer ni à sécuriser.
//  - Le schéma est créé automatiquement au premier démarrage (CREATE TABLE IF NOT EXISTS) : une mise à jour
//    de l'application ne touche jamais aux données existantes.
//  - Le mode WAL (Write-Ahead Logging) permet des lectures simultanées pendant une écriture : plusieurs
//    agents peuvent consulter et compter en même temps sans blocage.
//  Description détaillée des tables : docs/05-donnees-et-corbeille.md
// =====================================================================================================

using ComptageIncident.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace ComptageIncident.Data;

/// <summary>Fabrique de connexions SQLite et création du schéma.</summary>
public sealed class Database
{
    private readonly string _connectionString;

    /// <summary>Constructeur utilisé par l'application : chemin lu dans la configuration (Database:Path).</summary>
    public Database(IOptions<DatabaseOptions> options, IHostEnvironment env)
        : this(ResolvePath(options.Value.Path, env.ContentRootPath))
    {
    }

    /// <summary>Constructeur direct (utilisé par les tests avec un fichier temporaire).</summary>
    public Database(string filePath)
    {
        FilePath = filePath;

        // Crée le dossier (ex. App_Data) s'il n'existe pas. Lève une exception explicite si le compte
        // d'exécution n'a pas les droits : l'erreur apparaît alors dès le démarrage.
        var dir = System.IO.Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadWriteCreate, // crée le fichier s'il n'existe pas
            Pooling = true,                        // réutilise les connexions (performance)
            DefaultTimeout = 30,                   // délai max (s) d'une commande SQL
        }.ToString();

        Initialize();
    }

    /// <summary>Chemin absolu du fichier de base (affiché dans les journaux au démarrage).</summary>
    public string FilePath { get; }

    /// <summary>Un chemin relatif est résolu par rapport au dossier de l'application.</summary>
    private static string ResolvePath(string path, string contentRoot) =>
        System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.GetFullPath(System.IO.Path.Combine(contentRoot, path));

    /// <summary>
    /// Ouvre une connexion prête à l'emploi. L'appelant DOIT la libérer (« using »).
    /// Deux réglages sont appliqués à chaque connexion car SQLite ne les mémorise pas dans le fichier :
    ///  - foreign_keys = ON  : active les clés étrangères (suppression en cascade des personnes d'un incident purgé,
    ///                         mise à NULL de l'incident dans le journal d'audit) ;
    ///  - busy_timeout = 5000 : si un autre agent écrit au même moment, on attend jusqu'à 5 s au lieu d'échouer.
    /// </summary>
    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    /// <summary>Crée les tables et index s'ils n'existent pas encore (idempotent, sans perte de données).</summary>
    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            -- Journalisation WAL : mémorisée dans le fichier, lectures concurrentes possibles pendant une écriture.
            PRAGMA journal_mode = WAL;

            -- Un incident (incendie, fuite…). DeletedAt non NULL = en corbeille. ClosedAt non NULL = clôturé.
            CREATE TABLE IF NOT EXISTS Incidents (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                Title       TEXT NOT NULL,
                Type        TEXT NOT NULL,
                Location    TEXT,
                CreatedAt   TEXT NOT NULL,          -- dates au format ISO-8601 UTC (ex. 2026-11-09T22:17:49.785Z)
                CreatedBy   TEXT NOT NULL,          -- compte Windows DOMAINE\login
                ClosedAt    TEXT,
                ClosedBy    TEXT,
                DeletedAt   TEXT,
                DeletedBy   TEXT
            );

            -- Une personne comptée. RemovedAt non NULL = retirée de la liste (réintégrable).
            -- ON DELETE CASCADE : quand un incident est purgé définitivement, ses personnes le sont aussi.
            CREATE TABLE IF NOT EXISTS Persons (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                IncidentId  INTEGER NOT NULL REFERENCES Incidents(Id) ON DELETE CASCADE,
                Kind        TEXT NOT NULL CHECK (Kind IN ('internal', 'visitor')),
                DisplayName TEXT NOT NULL,
                LastName    TEXT,                   -- internes uniquement (attribut AD « sn »)
                FirstName   TEXT,                   -- internes uniquement (attribut AD « givenName »)
                Login       TEXT,                   -- internes uniquement (attribut AD « sAMAccountName »)
                Department  TEXT,                   -- internes uniquement (attribut AD « department »)
                Company     TEXT,                   -- visiteurs uniquement (saisie libre)
                AddedAt     TEXT NOT NULL,
                AddedBy     TEXT NOT NULL,
                RemovedAt   TEXT,
                RemovedBy   TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_Persons_Incident ON Persons(IncidentId);

            -- Garantie d'unicité au niveau de la base (en plus du contrôle applicatif) :
            -- un même identifiant Windows ne peut être compté qu'UNE fois parmi les personnes actives d'un incident,
            -- même si deux agents cliquent au même instant. Comparaison insensible à la casse (NOCASE).
            CREATE UNIQUE INDEX IF NOT EXISTS UX_Persons_ActiveLogin
                ON Persons(IncidentId, Login COLLATE NOCASE)
                WHERE Kind = 'internal' AND RemovedAt IS NULL;

            -- Journal d'audit : une ligne par action. Aucune fonction de l'application ne modifie ni ne supprime
            -- une ligne de ce journal. ON DELETE SET NULL : après la purge d'un incident, ses lignes de journal
            -- sont conservées (IncidentId devient NULL) afin de garder la trace de ce qui a existé.
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
