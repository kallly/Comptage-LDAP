// =====================================================================================================
//  Data/Models.cs : objets métier échangés entre la base, l'API et l'interface
// =====================================================================================================
//  Ces « records » sont des objets immuables : une fois lus depuis la base, ils ne sont jamais modifiés
//  en mémoire. Toute modification passe par IncidentRepository, qui écrit en base ET dans le journal d'audit.
//  Toutes les dates sont en UTC ; l'interface les convertit en heure locale du poste pour l'affichage.
// =====================================================================================================

namespace ComptageIncident.Data;

/// <summary>Types d'incident autorisés. Toute autre valeur est refusée par l'API (400).</summary>
public static class IncidentTypes
{
    public static readonly string[] All = ["Incendie", "Fuite chimique", "Exercice", "Autre"];
}

/// <summary>Catégories de personnes comptées (valeurs stockées dans la colonne Persons.Kind).</summary>
public static class PersonKinds
{
    /// <summary>Personne interne, sélectionnée dans l'annuaire (possède un identifiant Windows « Login »).</summary>
    public const string Internal = "internal";

    /// <summary>Visiteur ou personne hors annuaire, saisi manuellement (nom complet + société facultative).</summary>
    public const string Visitor = "visitor";
}

/// <summary>Un incident et ses compteurs.</summary>
/// <param name="Id">Identifiant numérique unique.</param>
/// <param name="Title">Titre saisi à la création (ex. « Incendie bâtiment B »).</param>
/// <param name="Type">Un des <see cref="IncidentTypes"/>.</param>
/// <param name="Location">Lieu / point de rassemblement (facultatif).</param>
/// <param name="CreatedAt">Date de création (UTC).</param>
/// <param name="CreatedBy">Compte Windows du créateur (DOMAINE\login).</param>
/// <param name="ClosedAt">Date de clôture ; null = incident en cours (listes modifiables).</param>
/// <param name="ClosedBy">Compte ayant clôturé.</param>
/// <param name="DeletedAt">Date de mise en corbeille ; null = incident actif.</param>
/// <param name="DeletedBy">Compte ayant supprimé.</param>
/// <param name="InternalCount">Nombre de personnes internes ACTUELLEMENT comptées (hors personnes retirées).</param>
/// <param name="VisitorCount">Nombre de visiteurs ACTUELLEMENT comptés (hors personnes retirées).</param>
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
    /// <summary>Vrai si l'incident est clôturé (lecture seule).</summary>
    public bool IsClosed => ClosedAt is not null;

    /// <summary>Total des personnes présentes (internes + visiteurs).</summary>
    public int TotalCount => InternalCount + VisitorCount;
}

/// <summary>Une personne comptée dans un incident.</summary>
/// <remarks>
/// Une personne n'est jamais supprimée physiquement par l'utilisateur : « Supprimer » renseigne
/// <see cref="RemovedAt"/>/<see cref="RemovedBy"/> (suppression logique), ce qui permet la réintégration
/// et conserve la trace. La suppression physique n'a lieu qu'avec la purge de l'incident entier.
/// </remarks>
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

/// <summary>Une ligne du journal d'audit. IncidentId devient null après la purge définitive de l'incident.</summary>
public sealed record AuditEntry(long Id, long? IncidentId, DateTime At, string User, string Action, string? Details);

/// <summary>Données nécessaires pour ajouter une personne interne (issues de la suggestion d'annuaire choisie).</summary>
public sealed record NewInternalPerson(string Login, string? FirstName, string? LastName, string? DisplayName, string? Department);

/// <summary>Données saisies pour ajouter un visiteur.</summary>
public sealed record NewVisitor(string FullName, string? Company);

/// <summary>Issue d'une opération métier.</summary>
public enum OpStatus
{
    /// <summary>Opération réalisée (HTTP 200/201).</summary>
    Ok,
    /// <summary>Élément inexistant, supprimé ou n'appartenant pas à l'incident (HTTP 404).</summary>
    NotFound,
    /// <summary>Opération contraire à une règle métier : doublon, incident clôturé, délai dépassé… (HTTP 409).</summary>
    Conflict,
}

/// <summary>Résultat d'une opération : statut + valeur (si succès) ou message d'erreur en français (si échec).</summary>
public sealed record OpResult<T>(OpStatus Status, T? Value = default, string? Error = null)
{
    public static OpResult<T> Ok(T value) => new(OpStatus.Ok, value);
    public static OpResult<T> NotFound(string error = "Élément introuvable.") => new(OpStatus.NotFound, default, error);
    public static OpResult<T> Conflict(string error) => new(OpStatus.Conflict, default, error);
}
