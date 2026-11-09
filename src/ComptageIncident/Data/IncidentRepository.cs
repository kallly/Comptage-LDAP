// =====================================================================================================
//  Data/IncidentRepository.cs : règles métier et accès aux données
// =====================================================================================================
//  C'est le cœur fonctionnel de l'application. Toutes les écritures passent par ici, ce qui garantit
//  que les règles ci-dessous sont TOUJOURS appliquées, quel que soit l'appelant :
//
//    R1. Aucune suppression physique déclenchée par un utilisateur :
//          - « Supprimer un incident »  → mise en corbeille (DeletedAt), restaurable pendant 24 h ;
//          - « Supprimer une personne » → retrait logique (RemovedAt), réintégrable.
//    R2. Seule la purge automatique (TrashPurgeService) supprime physiquement, et uniquement les incidents
//        en corbeille depuis plus de Trash:RetentionHours (24 h par défaut).
//    R3. Une personne interne (même identifiant Windows) ne peut être comptée qu'une fois par incident.
//    R4. Un incident clôturé est en lecture seule (ajout / retrait / réintégration refusés).
//    R5. Chaque action est écrite dans le journal d'audit (AuditLog) DANS LA MÊME TRANSACTION que la
//        modification : soit les deux sont enregistrées, soit aucune (pas de modification sans trace).
//
//  Transactions : Microsoft.Data.Sqlite ouvre les transactions en mode « BEGIN IMMEDIATE » par défaut.
//  Le verrou d'écriture est donc pris DÈS le début de la transaction : deux agents qui ajoutent la même
//  personne au même instant sont traités l'un après l'autre, et le second reçoit « déjà comptée ».
//
//  Sécurité SQL : toutes les requêtes utilisent des paramètres ($nom). Aucune donnée saisie n'est jamais
//  concaténée dans le texte SQL : l'injection SQL est impossible.
// =====================================================================================================

using System.Globalization;
using ComptageIncident.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace ComptageIncident.Data;

/// <summary>Accès aux incidents, personnes comptées et journal d'audit.</summary>
public sealed class IncidentRepository
{
    private readonly Database _db;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _retention;

    public IncidentRepository(Database db, TimeProvider clock, IOptions<TrashOptions> trash)
    {
        _db = db;
        _clock = clock;
        // Garde-fou : même si la configuration indique 0 ou une valeur négative, la rétention est d'au moins 1 h.
        _retention = TimeSpan.FromHours(Math.Max(1, trash.Value.RetentionHours));
    }

    /// <summary>Durée de rétention effective de la corbeille.</summary>
    public TimeSpan Retention => _retention;

    /// <summary>Heure courante en UTC (fournie par l'horloge injectée, remplaçable dans les tests).</summary>
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    // =================================================================================================
    //  Incidents
    // =================================================================================================

    /// <summary>
    /// Requête de base des incidents : colonnes de la table + deux sous-requêtes qui comptent les personnes
    /// ACTIVES (RemovedAt IS NULL) par catégorie. Les compteurs affichés sont donc toujours recalculés à
    /// partir des listes réelles, et ne peuvent pas se désynchroniser.
    /// </summary>
    private const string IncidentSelect = """
        SELECT i.Id, i.Title, i.Type, i.Location, i.CreatedAt, i.CreatedBy, i.ClosedAt, i.ClosedBy, i.DeletedAt, i.DeletedBy,
               (SELECT COUNT(*) FROM Persons p WHERE p.IncidentId = i.Id AND p.RemovedAt IS NULL AND p.Kind = 'internal'),
               (SELECT COUNT(*) FROM Persons p WHERE p.IncidentId = i.Id AND p.RemovedAt IS NULL AND p.Kind = 'visitor')
        FROM Incidents i
        """;

    /// <summary>Incidents hors corbeille : en cours d'abord, puis clôturés ; les plus récents en premier.</summary>
    public IReadOnlyList<Incident> ListIncidents()
    {
        using var conn = _db.Open();
        return Query(conn, IncidentSelect + " WHERE i.DeletedAt IS NULL ORDER BY (i.ClosedAt IS NOT NULL), i.CreatedAt DESC", ReadIncident);
    }

    /// <summary>Incidents en corbeille, les plus récemment supprimés en premier.</summary>
    public IReadOnlyList<Incident> ListTrash()
    {
        using var conn = _db.Open();
        return Query(conn, IncidentSelect + " WHERE i.DeletedAt IS NOT NULL ORDER BY i.DeletedAt DESC", ReadIncident);
    }

    /// <summary>
    /// Lit un incident. Par défaut, un incident en corbeille est considéré comme inexistant (null) :
    /// il n'est plus consultable ni modifiable tant qu'il n'a pas été restauré.
    /// </summary>
    public Incident? GetIncident(long id, bool includeDeleted = false)
    {
        using var conn = _db.Open();
        return GetIncident(conn, id, includeDeleted);
    }

    private static Incident? GetIncident(SqliteConnection conn, long id, bool includeDeleted)
    {
        var sql = IncidentSelect + " WHERE i.Id = $id" + (includeDeleted ? "" : " AND i.DeletedAt IS NULL");
        return Query(conn, sql, ReadIncident, ("$id", id)).FirstOrDefault();
    }

    /// <summary>Crée un incident (les paramètres ont déjà été validés par l'API) et journalise la création.</summary>
    public Incident CreateIncident(string title, string type, string? location, string user)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        var id = Scalar<long>(conn, """
            INSERT INTO Incidents (Title, Type, Location, CreatedAt, CreatedBy)
            VALUES ($title, $type, $location, $now, $user);
            SELECT last_insert_rowid();
            """,
            ("$title", title), ("$type", type), ("$location", location), ("$now", Fmt(Now)), ("$user", user));
        Audit(conn, id, user, "Création incident", $"{type} — {title}" + (string.IsNullOrEmpty(location) ? "" : $" ({location})"));
        tx.Commit();
        return GetIncident(conn, id, false)!;
    }

    /// <summary>Clôture : l'incident passe en lecture seule (règle R4).</summary>
    public OpResult<Incident> CloseIncident(long id, string user) => SetClosed(id, user, close: true);

    /// <summary>Réouverture : les listes redeviennent modifiables.</summary>
    public OpResult<Incident> ReopenIncident(long id, string user) => SetClosed(id, user, close: false);

    private OpResult<Incident> SetClosed(long id, string user, bool close)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        var incident = GetIncident(conn, id, false);
        if (incident is null) return OpResult<Incident>.NotFound("Incident introuvable.");
        if (incident.IsClosed == close)
            return OpResult<Incident>.Conflict(close ? "L'incident est déjà clôturé." : "L'incident est déjà ouvert.");

        Exec(conn, close
                ? "UPDATE Incidents SET ClosedAt = $now, ClosedBy = $user WHERE Id = $id"
                : "UPDATE Incidents SET ClosedAt = NULL, ClosedBy = NULL WHERE Id = $id",
            ("$now", Fmt(Now)), ("$user", user), ("$id", id));
        Audit(conn, id, user, close ? "Clôture incident" : "Réouverture incident", incident.Title);
        tx.Commit();
        return OpResult<Incident>.Ok(GetIncident(conn, id, false)!);
    }

    /// <summary>
    /// « Supprimer l'incident » = mise en corbeille (règle R1). Rien n'est effacé : seule la colonne DeletedAt
    /// est renseignée. L'incident disparaît de la liste principale et apparaît dans la Corbeille.
    /// </summary>
    public OpResult<Incident> TrashIncident(long id, string user)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        var incident = GetIncident(conn, id, false);
        if (incident is null) return OpResult<Incident>.NotFound("Incident introuvable.");
        Exec(conn, "UPDATE Incidents SET DeletedAt = $now, DeletedBy = $user WHERE Id = $id",
            ("$now", Fmt(Now)), ("$user", user), ("$id", id));
        Audit(conn, id, user, "Mise en corbeille incident", incident.Title);
        tx.Commit();
        return OpResult<Incident>.Ok(GetIncident(conn, id, true)!);
    }

    /// <summary>
    /// Restaure un incident de la corbeille, tant que le délai de rétention n'est pas écoulé.
    /// Le contrôle du délai est fait ICI (côté serveur) : même si la purge n'est pas encore passée,
    /// un incident hors délai n'est plus restaurable, ce qui rend le comportement prévisible.
    /// </summary>
    public OpResult<Incident> RestoreIncident(long id, string user)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        var incident = GetIncident(conn, id, true);
        if (incident is null || incident.DeletedAt is null) return OpResult<Incident>.NotFound("Incident introuvable dans la corbeille.");
        if (incident.DeletedAt.Value + _retention <= Now)
            return OpResult<Incident>.Conflict("Le délai de restauration est dépassé.");
        Exec(conn, "UPDATE Incidents SET DeletedAt = NULL, DeletedBy = NULL WHERE Id = $id", ("$id", id));
        Audit(conn, id, user, "Restauration incident", incident.Title);
        tx.Commit();
        return OpResult<Incident>.Ok(GetIncident(conn, id, false)!);
    }

    /// <summary>
    /// Supprime DÉFINITIVEMENT les incidents en corbeille depuis plus que la durée de rétention (règle R2).
    /// Appelée uniquement par TrashPurgeService. Pour chaque incident purgé :
    ///   1. une ligne de journal « Suppression définitive » est écrite (titre, type, effectifs) ;
    ///   2. l'incident est supprimé ; ses personnes partent en cascade (clé étrangère ON DELETE CASCADE) ;
    ///      ses lignes de journal sont conservées avec IncidentId = NULL (ON DELETE SET NULL).
    /// </summary>
    /// <returns>Nombre d'incidents purgés.</returns>
    public int PurgeExpired()
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        var limit = Fmt(Now - _retention);
        // Comparaison de dates sous forme de texte : valable car le format ISO-8601 UTC à largeur fixe
        // (yyyy-MM-ddTHH:mm:ss.fffZ) se trie dans le même ordre que les dates elles-mêmes.
        var expired = Query(conn, IncidentSelect + " WHERE i.DeletedAt IS NOT NULL AND i.DeletedAt <= $limit", ReadIncident, ("$limit", limit));
        foreach (var incident in expired)
        {
            Audit(conn, incident.Id, "système", "Suppression définitive incident",
                $"#{incident.Id} {incident.Type} — {incident.Title} ({incident.InternalCount} internes, {incident.VisitorCount} visiteurs)");
            Exec(conn, "DELETE FROM Incidents WHERE Id = $id", ("$id", incident.Id));
        }
        tx.Commit();
        return expired.Count;
    }

    // =================================================================================================
    //  Personnes
    // =================================================================================================

    private const string PersonSelect = """
        SELECT Id, IncidentId, Kind, DisplayName, LastName, FirstName, Login, Department, Company, AddedAt, AddedBy, RemovedAt, RemovedBy
        FROM Persons
        """;

    /// <summary>Toutes les personnes d'un incident, actives ET retirées (l'API les sépare ensuite).</summary>
    public IReadOnlyList<Person> ListPersons(long incidentId)
    {
        using var conn = _db.Open();
        return Query(conn, PersonSelect + " WHERE IncidentId = $id ORDER BY Id", ReadPerson, ("$id", incidentId));
    }

    /// <summary>
    /// Ajoute une personne interne (choisie dans l'annuaire). Refusé si l'incident est clôturé (R4)
    /// ou si la personne est déjà comptée (R3, comparaison de l'identifiant insensible à la casse).
    /// </summary>
    public OpResult<Person> AddInternal(long incidentId, NewInternalPerson p, string user)
    {
        var login = p.Login.Trim();
        var first = Clean(p.FirstName);
        var last = Clean(p.LastName);
        // Nom affiché : celui de l'annuaire, sinon « NOM Prénom », sinon l'identifiant.
        var display = Clean(p.DisplayName) ?? string.Join(' ', new[] { last?.ToUpperInvariant(), first }.Where(s => !string.IsNullOrEmpty(s)));
        if (string.IsNullOrWhiteSpace(display)) display = login;

        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        var check = CheckWritable(conn, incidentId);
        if (check is { } failure) return Fail<Person>(failure);

        // Contrôle applicatif du doublon (message clair pour l'utilisateur). L'index unique
        // UX_Persons_ActiveLogin (voir Database.cs) constitue une seconde barrière au niveau de la base.
        var existing = Scalar<long?>(conn,
            "SELECT Id FROM Persons WHERE IncidentId = $id AND Kind = 'internal' AND RemovedAt IS NULL AND Login = $login COLLATE NOCASE",
            ("$id", incidentId), ("$login", login));
        if (existing is not null) return OpResult<Person>.Conflict($"{display} est déjà comptée dans cet incident.");

        long id;
        try
        {
            id = Scalar<long>(conn, """
                INSERT INTO Persons (IncidentId, Kind, DisplayName, LastName, FirstName, Login, Department, AddedAt, AddedBy)
                VALUES ($inc, 'internal', $display, $last, $first, $login, $dept, $now, $user);
                SELECT last_insert_rowid();
                """,
                ("$inc", incidentId), ("$display", display), ("$last", last), ("$first", first), ("$login", login),
                ("$dept", Clean(p.Department)), ("$now", Fmt(Now)), ("$user", user));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // 19 = SQLITE_CONSTRAINT (index unique)
        {
            return OpResult<Person>.Conflict($"{display} est déjà comptée dans cet incident.");
        }
        Audit(conn, incidentId, user, "Ajout interne", $"{display} ({login})");
        tx.Commit();
        return OpResult<Person>.Ok(GetPerson(conn, id)!);
    }

    /// <summary>
    /// Ajoute un visiteur (saisie manuelle). Pas de contrôle de doublon : deux visiteurs peuvent porter
    /// le même nom. Les espaces multiples sont normalisés (« Paul   Durand » → « Paul Durand »).
    /// </summary>
    public OpResult<Person> AddVisitor(long incidentId, NewVisitor v, string user)
    {
        var name = Clean(v.FullName)!;
        var company = Clean(v.Company);
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        var check = CheckWritable(conn, incidentId);
        if (check is { } failure) return Fail<Person>(failure);

        var id = Scalar<long>(conn, """
            INSERT INTO Persons (IncidentId, Kind, DisplayName, Company, AddedAt, AddedBy)
            VALUES ($inc, 'visitor', $name, $company, $now, $user);
            SELECT last_insert_rowid();
            """,
            ("$inc", incidentId), ("$name", name), ("$company", company), ("$now", Fmt(Now)), ("$user", user));
        Audit(conn, incidentId, user, "Ajout visiteur", name + (company is null ? "" : $" ({company})"));
        tx.Commit();
        return OpResult<Person>.Ok(GetPerson(conn, id)!);
    }

    /// <summary>
    /// « Supprimer » une personne = retrait logique (R1) : RemovedAt/RemovedBy sont renseignés.
    /// La personne n'est plus comptée mais reste visible dans « Personnes retirées » et réintégrable.
    /// On vérifie que la personne appartient bien à l'incident indiqué dans l'URL (pas de modification
    /// croisée entre incidents).
    /// </summary>
    public OpResult<Person> RemovePerson(long incidentId, long personId, string user)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        var check = CheckWritable(conn, incidentId);
        if (check is { } failure) return Fail<Person>(failure);

        var person = GetPerson(conn, personId);
        if (person is null || person.IncidentId != incidentId || person.RemovedAt is not null)
            return OpResult<Person>.NotFound("Personne introuvable dans la liste.");

        Exec(conn, "UPDATE Persons SET RemovedAt = $now, RemovedBy = $user WHERE Id = $id",
            ("$now", Fmt(Now)), ("$user", user), ("$id", personId));
        Audit(conn, incidentId, user, person.Kind == PersonKinds.Internal ? "Retrait interne" : "Retrait visiteur", Describe(person));
        tx.Commit();
        return OpResult<Person>.Ok(GetPerson(conn, personId)!);
    }

    /// <summary>
    /// Réintègre une personne retirée par erreur. Refusé si, entre-temps, la même personne interne
    /// a été ajoutée de nouveau (sinon elle serait comptée deux fois : règle R3).
    /// </summary>
    public OpResult<Person> RestorePerson(long incidentId, long personId, string user)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        var check = CheckWritable(conn, incidentId);
        if (check is { } failure) return Fail<Person>(failure);

        var person = GetPerson(conn, personId);
        if (person is null || person.IncidentId != incidentId || person.RemovedAt is null)
            return OpResult<Person>.NotFound("Personne introuvable parmi les personnes retirées.");

        if (person.Kind == PersonKinds.Internal && person.Login is not null)
        {
            var active = Scalar<long?>(conn,
                "SELECT Id FROM Persons WHERE IncidentId = $id AND Kind = 'internal' AND RemovedAt IS NULL AND Login = $login COLLATE NOCASE",
                ("$id", incidentId), ("$login", person.Login));
            if (active is not null) return OpResult<Person>.Conflict($"{person.DisplayName} est déjà comptée dans cet incident.");
        }

        Exec(conn, "UPDATE Persons SET RemovedAt = NULL, RemovedBy = NULL WHERE Id = $id", ("$id", personId));
        Audit(conn, incidentId, user, "Réintégration", Describe(person));
        tx.Commit();
        return OpResult<Person>.Ok(GetPerson(conn, personId)!);
    }

    private static Person? GetPerson(SqliteConnection conn, long id) =>
        Query(conn, PersonSelect + " WHERE Id = $id", ReadPerson, ("$id", id)).FirstOrDefault();

    /// <summary>Libellé utilisé dans le journal : « NOM Prénom (login) » ou « Nom (société) ».</summary>
    private static string Describe(Person p) =>
        p.DisplayName + (p.Login is not null ? $" ({p.Login})" : p.Company is not null ? $" ({p.Company})" : "");

    /// <summary>
    /// Vérifie qu'on peut modifier les listes d'un incident : il doit exister, ne pas être en corbeille
    /// et ne pas être clôturé. Renvoie null si c'est le cas, sinon le motif du refus.
    /// </summary>
    private static (OpStatus Status, string Error)? CheckWritable(SqliteConnection conn, long incidentId)
    {
        var incident = GetIncident(conn, incidentId, false);
        if (incident is null) return (OpStatus.NotFound, "Incident introuvable.");
        if (incident.IsClosed) return (OpStatus.Conflict, "L'incident est clôturé : rouvrez-le pour modifier les listes.");
        return null;
    }

    private static OpResult<T> Fail<T>((OpStatus Status, string Error) f) => new(f.Status, default, f.Error);

    // =================================================================================================
    //  Journal d'audit (règle R5)
    // =================================================================================================

    /// <summary>Journal d'un incident, du plus récent au plus ancien.</summary>
    public IReadOnlyList<AuditEntry> ListAudit(long incidentId)
    {
        using var conn = _db.Open();
        return Query(conn, "SELECT Id, IncidentId, At, User, Action, Details FROM AuditLog WHERE IncidentId = $id ORDER BY Id DESC",
            r => new AuditEntry(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), ParseDate(r.GetString(2)), r.GetString(3), r.GetString(4), NullableString(r, 5)),
            ("$id", incidentId));
    }

    /// <summary>
    /// Écrit une ligne de journal. Toujours appelée sur la connexion de la transaction en cours, avant
    /// tx.Commit() : la ligne de journal et la modification sont enregistrées ensemble ou pas du tout.
    /// C'est la SEULE instruction de l'application qui touche à la table AuditLog (INSERT uniquement).
    /// </summary>
    private void Audit(SqliteConnection conn, long? incidentId, string user, string action, string? details) =>
        Exec(conn, "INSERT INTO AuditLog (IncidentId, At, User, Action, Details) VALUES ($inc, $at, $user, $action, $details)",
            ("$inc", incidentId), ("$at", Fmt(Now)), ("$user", user), ("$action", action), ("$details", details));

    // =================================================================================================
    //  Utilitaires SQL
    // =================================================================================================

    /// <summary>Convertit une ligne SQL (ordre des colonnes de IncidentSelect) en objet Incident.</summary>
    private static Incident ReadIncident(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), NullableString(r, 3),
        ParseDate(r.GetString(4)), r.GetString(5),
        NullableDate(r, 6), NullableString(r, 7),
        NullableDate(r, 8), NullableString(r, 9),
        r.GetInt32(10), r.GetInt32(11));

    /// <summary>Convertit une ligne SQL (ordre des colonnes de PersonSelect) en objet Person.</summary>
    private static Person ReadPerson(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3),
        NullableString(r, 4), NullableString(r, 5), NullableString(r, 6), NullableString(r, 7), NullableString(r, 8),
        ParseDate(r.GetString(9)), r.GetString(10), NullableDate(r, 11), NullableString(r, 12));

    /// <summary>Supprime les espaces superflus ; une chaîne vide devient null (champ non renseigné).</summary>
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Format de stockage des dates : ISO-8601 UTC à largeur fixe (triable comme du texte).</summary>
    private static string Fmt(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static DateTime ParseDate(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static string? NullableString(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateTime? NullableDate(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : ParseDate(r.GetString(i));

    /// <summary>
    /// Prépare une commande SQL PARAMÉTRÉE. Les valeurs sont transmises séparément du texte SQL
    /// (AddWithValue) : SQLite ne les interprète jamais comme du code. La commande rejoint automatiquement
    /// la transaction en cours sur la connexion.
    /// </summary>
    private static SqliteCommand Command(SqliteConnection conn, string sql, (string Name, object? Value)[] parameters)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    /// <summary>Exécute une requête de lecture et convertit chaque ligne avec <paramref name="map"/>.</summary>
    private static List<T> Query<T>(SqliteConnection conn, string sql, Func<SqliteDataReader, T> map, params (string, object?)[] parameters)
    {
        using var cmd = Command(conn, sql, parameters);
        using var reader = cmd.ExecuteReader();
        var list = new List<T>();
        while (reader.Read()) list.Add(map(reader));
        return list;
    }

    /// <summary>Exécute une instruction sans résultat (INSERT/UPDATE/DELETE).</summary>
    private static void Exec(SqliteConnection conn, string sql, params (string, object?)[] parameters)
    {
        using var cmd = Command(conn, sql, parameters);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Exécute une requête et renvoie la première valeur (ou default si aucune ligne).</summary>
    private static T Scalar<T>(SqliteConnection conn, string sql, params (string, object?)[] parameters)
    {
        using var cmd = Command(conn, sql, parameters);
        var result = cmd.ExecuteScalar();
        if (result is null or DBNull) return default!;
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(result, target, CultureInfo.InvariantCulture);
    }
}
