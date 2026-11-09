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
        _retention = TimeSpan.FromHours(Math.Max(1, trash.Value.RetentionHours));
    }

    public TimeSpan Retention => _retention;

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    // ------------------------------------------------------------------ Incidents

    private const string IncidentSelect = """
        SELECT i.Id, i.Title, i.Type, i.Location, i.CreatedAt, i.CreatedBy, i.ClosedAt, i.ClosedBy, i.DeletedAt, i.DeletedBy,
               (SELECT COUNT(*) FROM Persons p WHERE p.IncidentId = i.Id AND p.RemovedAt IS NULL AND p.Kind = 'internal'),
               (SELECT COUNT(*) FROM Persons p WHERE p.IncidentId = i.Id AND p.RemovedAt IS NULL AND p.Kind = 'visitor')
        FROM Incidents i
        """;

    public IReadOnlyList<Incident> ListIncidents()
    {
        using var conn = _db.Open();
        return Query(conn, IncidentSelect + " WHERE i.DeletedAt IS NULL ORDER BY (i.ClosedAt IS NOT NULL), i.CreatedAt DESC", ReadIncident);
    }

    public IReadOnlyList<Incident> ListTrash()
    {
        using var conn = _db.Open();
        return Query(conn, IncidentSelect + " WHERE i.DeletedAt IS NOT NULL ORDER BY i.DeletedAt DESC", ReadIncident);
    }

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

    public OpResult<Incident> CloseIncident(long id, string user) => SetClosed(id, user, close: true);

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

    /// <summary>Met l'incident en corbeille (suppression logique, restaurable pendant la durée de rétention).</summary>
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

    /// <summary>Supprime définitivement les incidents restés en corbeille au-delà de la rétention.</summary>
    public int PurgeExpired()
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        var limit = Fmt(Now - _retention);
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

    // ------------------------------------------------------------------ Personnes

    private const string PersonSelect = """
        SELECT Id, IncidentId, Kind, DisplayName, LastName, FirstName, Login, Department, Company, AddedAt, AddedBy, RemovedAt, RemovedBy
        FROM Persons
        """;

    public IReadOnlyList<Person> ListPersons(long incidentId)
    {
        using var conn = _db.Open();
        return Query(conn, PersonSelect + " WHERE IncidentId = $id ORDER BY Id", ReadPerson, ("$id", incidentId));
    }

    public OpResult<Person> AddInternal(long incidentId, NewInternalPerson p, string user)
    {
        var login = p.Login.Trim();
        var first = Clean(p.FirstName);
        var last = Clean(p.LastName);
        var display = Clean(p.DisplayName) ?? string.Join(' ', new[] { last?.ToUpperInvariant(), first }.Where(s => !string.IsNullOrEmpty(s)));
        if (string.IsNullOrWhiteSpace(display)) display = login;

        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        var check = CheckWritable(conn, incidentId);
        if (check is { } failure) return Fail<Person>(failure);

        var existing = Scalar<long?>(conn,
            "SELECT Id FROM Persons WHERE IncidentId = $id AND Kind = 'internal' AND RemovedAt IS NULL AND Login = $login COLLATE NOCASE",
            ("$id", incidentId), ("$login", login));
        if (existing is not null) return OpResult<Person>.Conflict($"{display} est déjà comptée dans cet incident.");

        var id = Scalar<long>(conn, """
            INSERT INTO Persons (IncidentId, Kind, DisplayName, LastName, FirstName, Login, Department, AddedAt, AddedBy)
            VALUES ($inc, 'internal', $display, $last, $first, $login, $dept, $now, $user);
            SELECT last_insert_rowid();
            """,
            ("$inc", incidentId), ("$display", display), ("$last", last), ("$first", first), ("$login", login),
            ("$dept", Clean(p.Department)), ("$now", Fmt(Now)), ("$user", user));
        Audit(conn, incidentId, user, "Ajout interne", $"{display} ({login})");
        tx.Commit();
        return OpResult<Person>.Ok(GetPerson(conn, id)!);
    }

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

    /// <summary>Retire une personne de la liste (suppression logique : elle reste réintégrable).</summary>
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

    private static string Describe(Person p) =>
        p.DisplayName + (p.Login is not null ? $" ({p.Login})" : p.Company is not null ? $" ({p.Company})" : "");

    /// <summary>Renvoie null si l'incident accepte les modifications, sinon le motif du refus.</summary>
    private static (OpStatus Status, string Error)? CheckWritable(SqliteConnection conn, long incidentId)
    {
        var incident = GetIncident(conn, incidentId, false);
        if (incident is null) return (OpStatus.NotFound, "Incident introuvable.");
        if (incident.IsClosed) return (OpStatus.Conflict, "L'incident est clôturé : rouvrez-le pour modifier les listes.");
        return null;
    }

    private static OpResult<T> Fail<T>((OpStatus Status, string Error) f) => new(f.Status, default, f.Error);

    // ------------------------------------------------------------------ Audit

    public IReadOnlyList<AuditEntry> ListAudit(long incidentId)
    {
        using var conn = _db.Open();
        return Query(conn, "SELECT Id, IncidentId, At, User, Action, Details FROM AuditLog WHERE IncidentId = $id ORDER BY Id DESC",
            r => new AuditEntry(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), ParseDate(r.GetString(2)), r.GetString(3), r.GetString(4), NullableString(r, 5)),
            ("$id", incidentId));
    }

    private void Audit(SqliteConnection conn, long? incidentId, string user, string action, string? details) =>
        Exec(conn, "INSERT INTO AuditLog (IncidentId, At, User, Action, Details) VALUES ($inc, $at, $user, $action, $details)",
            ("$inc", incidentId), ("$at", Fmt(Now)), ("$user", user), ("$action", action), ("$details", details));

    // ------------------------------------------------------------------ Helpers SQL

    private static Incident ReadIncident(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), NullableString(r, 3),
        ParseDate(r.GetString(4)), r.GetString(5),
        NullableDate(r, 6), NullableString(r, 7),
        NullableDate(r, 8), NullableString(r, 9),
        r.GetInt32(10), r.GetInt32(11));

    private static Person ReadPerson(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3),
        NullableString(r, 4), NullableString(r, 5), NullableString(r, 6), NullableString(r, 7), NullableString(r, 8),
        ParseDate(r.GetString(9)), r.GetString(10), NullableDate(r, 11), NullableString(r, 12));

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Fmt(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static DateTime ParseDate(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static string? NullableString(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateTime? NullableDate(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : ParseDate(r.GetString(i));

    private static SqliteCommand Command(SqliteConnection conn, string sql, (string Name, object? Value)[] parameters)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private static List<T> Query<T>(SqliteConnection conn, string sql, Func<SqliteDataReader, T> map, params (string, object?)[] parameters)
    {
        using var cmd = Command(conn, sql, parameters);
        using var reader = cmd.ExecuteReader();
        var list = new List<T>();
        while (reader.Read()) list.Add(map(reader));
        return list;
    }

    private static void Exec(SqliteConnection conn, string sql, params (string, object?)[] parameters)
    {
        using var cmd = Command(conn, sql, parameters);
        cmd.ExecuteNonQuery();
    }

    private static T Scalar<T>(SqliteConnection conn, string sql, params (string, object?)[] parameters)
    {
        using var cmd = Command(conn, sql, parameters);
        var result = cmd.ExecuteScalar();
        if (result is null or DBNull) return default!;
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(result, target, CultureInfo.InvariantCulture);
    }
}
