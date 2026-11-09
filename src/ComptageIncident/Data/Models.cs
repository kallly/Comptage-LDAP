namespace ComptageIncident.Data;

public static class IncidentTypes
{
    public static readonly string[] All = ["Incendie", "Fuite chimique", "Exercice", "Autre"];
}

public static class PersonKinds
{
    public const string Internal = "internal";
    public const string Visitor = "visitor";
}

public sealed record Incident(
    long Id,
    string Title,
    string Type,
    string? Location,
    DateTime CreatedAt,
    string CreatedBy,
    DateTime? ClosedAt,
    string? ClosedBy,
    DateTime? DeletedAt,
    string? DeletedBy,
    int InternalCount,
    int VisitorCount)
{
    public bool IsClosed => ClosedAt is not null;
    public int TotalCount => InternalCount + VisitorCount;
}

public sealed record Person(
    long Id,
    long IncidentId,
    string Kind,
    string DisplayName,
    string? LastName,
    string? FirstName,
    string? Login,
    string? Department,
    string? Company,
    DateTime AddedAt,
    string AddedBy,
    DateTime? RemovedAt,
    string? RemovedBy);

public sealed record AuditEntry(long Id, long? IncidentId, DateTime At, string User, string Action, string? Details);

public sealed record NewInternalPerson(string Login, string? FirstName, string? LastName, string? DisplayName, string? Department);

public sealed record NewVisitor(string FullName, string? Company);

/// <summary>Résultat d'une opération métier : succès, introuvable ou conflit (avec message).</summary>
public enum OpStatus { Ok, NotFound, Conflict }

public sealed record OpResult<T>(OpStatus Status, T? Value = default, string? Error = null)
{
    public static OpResult<T> Ok(T value) => new(OpStatus.Ok, value);
    public static OpResult<T> NotFound(string error = "Élément introuvable.") => new(OpStatus.NotFound, default, error);
    public static OpResult<T> Conflict(string error) => new(OpStatus.Conflict, default, error);
}
