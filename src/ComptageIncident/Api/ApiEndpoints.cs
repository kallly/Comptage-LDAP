// =====================================================================================================
//  Api/ApiEndpoints.cs : API REST utilisée par l'interface Web (wwwroot/js/app.js)
// =====================================================================================================
//  Toutes les routes sont sous /api et exigent un utilisateur Windows authentifié (règle globale définie
//  dans Program.cs : aucune route n'y déroge, il n'existe aucun [AllowAnonymous]).
//
//  Rôle de cette couche :
//    1. VALIDER les données reçues (champs obligatoires, longueurs maximales, type d'incident autorisé,
//       liste blanche de caractères pour les noms saisis : voir NameRules.cs) ;
//    2. récupérer l'identité de l'utilisateur (DOMAINE\login) fournie par l'authentification Windows :
//       elle n'est JAMAIS lue depuis le corps de la requête, donc impossible à usurper par le client ;
//    3. appeler IncidentRepository, qui applique les règles métier et écrit le journal d'audit ;
//    4. traduire le résultat en code HTTP :
//         200 OK / 201 Created   succès
//         400 Bad Request        données invalides, ou en-tête anti-CSRF absent
//         401 Unauthorized       non authentifié (géré par ASP.NET Core avant d'arriver ici)
//         404 Not Found          incident/personne inexistant ou en corbeille
//         409 Conflict           règle métier : doublon, incident clôturé, délai de restauration dépassé
//         503 Service Unavailable annuaire injoignable
//       Les erreurs sont renvoyées sous la forme { "error": "message en français" }.
//
//  Référence complète de l'API : docs/10-reference-api.md
// =====================================================================================================

using System.Globalization;
using System.Security.Claims;
using System.Text;
using ComptageIncident.Data;
using ComptageIncident.Directory;
using ComptageIncident.Options;
using Microsoft.Extensions.Options;

namespace ComptageIncident.Api;

public static class ApiEndpoints
{
    /// <summary>En-tête anti-CSRF exigé sur les requêtes de modification (voir UseCsrfHeaderCheck).</summary>
    public const string CsrfHeader = "X-Requested-With";
    public const string CsrfValue = "ComptageIncident";

    // Corps JSON attendus. Tous les champs sont « nullable » : la validation est faite explicitement ci-dessous
    // pour renvoyer des messages en français plutôt qu'une erreur technique de désérialisation.
    public sealed record CreateIncidentRequest(string? Title, string? Type, string? Location);
    public sealed record AddInternalRequest(string? Login, string? FirstName, string? LastName, string? DisplayName, string? Department);
    public sealed record AddVisitorRequest(string? FullName, string? Company);

    /// <summary>Déclare toutes les routes de l'API.</summary>
    public static void MapApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // -------------------------------------------------------------------------------- Informations
        // Utilisateur connecté (affiché en haut à droite de l'interface).
        api.MapGet("/me", (ClaimsPrincipal user) => new { name = UserName(user) });

        // Paramètres utiles à l'interface (aucune donnée sensible).
        api.MapGet("/config", (IOptions<DirectoryOptions> dir, IOptions<TrashOptions> trash) => new
        {
            minSearchLength = dir.Value.MinSearchLength,
            trashRetentionHours = trash.Value.RetentionHours,
            incidentTypes = IncidentTypes.All,
        });

        // -------------------------------------------------------------------------------- Incidents
        // Liste des incidents hors corbeille, avec leurs compteurs.
        api.MapGet("/incidents", (IncidentRepository repo) => repo.ListIncidents().Select(ToDto));

        // Création d'un incident.
        api.MapPost("/incidents", (CreateIncidentRequest req, IncidentRepository repo, ClaimsPrincipal user) =>
        {
            var title = req.Title?.Trim();
            if (string.IsNullOrEmpty(title)) return Problem("Le titre de l'incident est obligatoire.");
            if (title.Length > 200) return Problem("Le titre est trop long (200 caractères max).");
            // Le type doit appartenir à la liste fermée IncidentTypes.All (comparaison insensible à la casse).
            var type = IncidentTypes.All.FirstOrDefault(t => string.Equals(t, req.Type?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (type is null) return Problem("Type d'incident inconnu.");
            var location = req.Location?.Trim();
            if (location?.Length > 200) return Problem("Le lieu est trop long (200 caractères max).");

            var incident = repo.CreateIncident(title, type, string.IsNullOrEmpty(location) ? null : location, UserName(user));
            return Results.Created($"/api/incidents/{incident.Id}", ToDto(incident));
        });

        // Détail d'un incident : l'incident + les personnes actives + les personnes retirées.
        // L'interface appelle cette route toutes les 5 secondes pour rester à jour.
        api.MapGet("/incidents/{id:long}", (long id, IncidentRepository repo) =>
        {
            var incident = repo.GetIncident(id);
            if (incident is null) return Results.NotFound(new { error = "Incident introuvable." });
            var persons = repo.ListPersons(id);
            return Results.Ok(new
            {
                incident = ToDto(incident),
                persons = persons.Where(p => p.RemovedAt is null),
                removed = persons.Where(p => p.RemovedAt is not null).OrderByDescending(p => p.RemovedAt),
            });
        });

        // Clôture / réouverture.
        api.MapPost("/incidents/{id:long}/close", (long id, IncidentRepository repo, ClaimsPrincipal user) =>
            ToResult(repo.CloseIncident(id, UserName(user)), ToDto));

        api.MapPost("/incidents/{id:long}/reopen", (long id, IncidentRepository repo, ClaimsPrincipal user) =>
            ToResult(repo.ReopenIncident(id, UserName(user)), ToDto));

        // « Supprimer l'incident » : mise en CORBEILLE (pas de suppression physique).
        api.MapDelete("/incidents/{id:long}", (long id, IncidentRepository repo, ClaimsPrincipal user) =>
            ToResult(repo.TrashIncident(id, UserName(user)), ToDto));

        // Journal d'audit de l'incident.
        api.MapGet("/incidents/{id:long}/audit", (long id, IncidentRepository repo) =>
            repo.GetIncident(id) is null ? Results.NotFound(new { error = "Incident introuvable." }) : Results.Ok(repo.ListAudit(id)));

        // Export CSV des personnes présentes (ouvrable directement dans Excel).
        api.MapGet("/incidents/{id:long}/export.csv", (long id, IncidentRepository repo) =>
        {
            var incident = repo.GetIncident(id);
            if (incident is null) return Results.NotFound(new { error = "Incident introuvable." });
            var csv = BuildCsv(incident, repo.ListPersons(id).Where(p => p.RemovedAt is null));
            var fileName = $"comptage-incident-{incident.Id}-{DateTime.Now:yyyyMMdd-HHmm}.csv";
            // Préambule UTF-8 (BOM) : indispensable pour qu'Excel affiche correctement les accents.
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv; charset=utf-8", fileName);
        });

        // -------------------------------------------------------------------------------- Corbeille
        // Incidents en corbeille + date à laquelle chacun sera supprimé définitivement.
        api.MapGet("/trash", (IncidentRepository repo) =>
            repo.ListTrash().Select(i => new
            {
                incident = ToDto(i),
                purgeAt = i.DeletedAt!.Value + repo.Retention,
            }));

        // Restauration (refusée si le délai de rétention est dépassé).
        api.MapPost("/trash/{id:long}/restore", (long id, IncidentRepository repo, ClaimsPrincipal user) =>
            ToResult(repo.RestoreIncident(id, UserName(user)), ToDto));

        // -------------------------------------------------------------------------------- Personnes
        // Ajout d'une personne interne, à partir d'une suggestion de l'annuaire.
        // Remarque : les données transmises (nom, prénom, service) sont celles de la suggestion affichée.
        // Le serveur ne réinterroge pas l'annuaire, afin que l'ajout reste possible et rapide même si l'AD
        // ralentit pendant un incident. L'identifiant (login) sert de clé anti-doublon.
        api.MapPost("/incidents/{id:long}/persons/internal", (long id, AddInternalRequest req, IncidentRepository repo, ClaimsPrincipal user) =>
        {
            var login = req.Login?.Trim();
            if (string.IsNullOrEmpty(login) || login.Length > 256) return Problem("Identifiant de la personne manquant ou invalide.");
            if (TooLong(req.FirstName, req.LastName, req.DisplayName, req.Department)) return Problem("Champ trop long.");
            return ToResult(repo.AddInternal(id, new NewInternalPerson(login, req.FirstName, req.LastName, req.DisplayName, req.Department), UserName(user)),
                p => p, created: true);
        });

        // Ajout d'un visiteur (saisie manuelle du nom complet, 2 caractères minimum).
        // Le nom doit respecter la liste blanche de NameRules (lettres, accents, espace, apostrophe, tiret).
        api.MapPost("/incidents/{id:long}/persons/visitor", (long id, AddVisitorRequest req, IncidentRepository repo, ClaimsPrincipal user) =>
        {
            var name = NameRules.Normalize(req.FullName);
            if (name.Length < 2) return Problem("Le nom complet du visiteur est obligatoire.");
            if (TooLong(name, req.Company)) return Problem("Champ trop long.");
            if (!NameRules.IsValid(name)) return Problem($"Nom invalide : seuls les caractères suivants sont acceptés : {NameRules.AllowedDescription}.");
            return ToResult(repo.AddVisitor(id, new NewVisitor(name, req.Company), UserName(user)), p => p, created: true);
        });

        // « Supprimer » une personne : retrait LOGIQUE (réintégrable).
        api.MapDelete("/incidents/{id:long}/persons/{personId:long}", (long id, long personId, IncidentRepository repo, ClaimsPrincipal user) =>
            ToResult(repo.RemovePerson(id, personId, UserName(user)), p => p));

        // Réintégration d'une personne retirée.
        api.MapPost("/incidents/{id:long}/persons/{personId:long}/restore", (long id, long personId, IncidentRepository repo, ClaimsPrincipal user) =>
            ToResult(repo.RestorePerson(id, personId, UserName(user)), p => p));

        // -------------------------------------------------------------------------------- Annuaire
        // Recherche de suggestions. Contrôles faits ICI (et pas seulement dans l'interface), pour qu'un appel
        // direct à l'API y soit aussi soumis :
        //   1. longueur minimale (4 par défaut) : pas de recherche trop large ;
        //   2. longueur maximale (100) ;
        //   3. liste blanche de caractères (NameRules) : lettres, accents, espace, apostrophe, tiret.
        // L'échappement LDAP (DirectorySearch.EscapeLdap) reste appliqué ensuite, en défense en profondeur.
        api.MapGet("/directory/search", async (string? q, IDirectoryService directory, IOptions<DirectoryOptions> options,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var query = NameRules.Normalize(q);
            if (query.Length < options.Value.MinSearchLength)
                return Problem($"Saisissez au moins {options.Value.MinSearchLength} caractères.");
            if (query.Length > 100) return Problem("Recherche trop longue.");
            if (!NameRules.IsValid(query))
                return Problem($"Caractère non autorisé. Seuls sont acceptés : {NameRules.AllowedDescription}.");
            try
            {
                return Results.Ok(await directory.SearchAsync(query, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Détail technique dans le journal serveur ; message générique et solution de repli pour l'utilisateur.
                loggerFactory.CreateLogger("Directory").LogError(ex, "Recherche annuaire impossible");
                return Results.Json(new { error = "Annuaire indisponible. Utilisez « Ajouter un visiteur » en attendant." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
    }

    /// <summary>
    /// Protection contre la falsification de requête inter-sites (CSRF).
    /// <para>
    /// Le risque : le navigateur envoie AUTOMATIQUEMENT l'identité Windows à l'application. Un site
    /// malveillant visité par un agent pourrait donc tenter de déclencher, à son insu, une requête de
    /// modification (ex. supprimer un incident) vers l'application.
    /// </para>
    /// <para>
    /// La parade : toute requête /api autre que GET/HEAD/OPTIONS doit porter l'en-tête
    /// « X-Requested-With: ComptageIncident ». Un autre site ne peut pas ajouter d'en-tête personnalisé
    /// à une requête vers notre domaine sans une autorisation CORS préalable (« preflight ») ; or
    /// l'application n'autorise AUCUNE origine externe (pas de configuration CORS). La requête
    /// malveillante est donc soit bloquée par le navigateur, soit envoyée sans l'en-tête, et rejetée ici (400).
    /// </para>
    /// Les requêtes GET n'ont pas besoin de cette protection car elles ne modifient rien.
    /// </summary>
    public static IApplicationBuilder UseCsrfHeaderCheck(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api")
            && !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method) && !HttpMethods.IsOptions(ctx.Request.Method)
            && ctx.Request.Headers[CsrfHeader] != CsrfValue)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { error = "Requête refusée (en-tête de sécurité manquant)." });
            return;
        }
        await next();
    });

    /// <summary>
    /// Identité de l'utilisateur telle qu'établie par l'authentification Windows (DOMAINE\login).
    /// Elle est enregistrée dans chaque ligne créée/modifiée et dans le journal d'audit.
    /// </summary>
    private static string UserName(ClaimsPrincipal user) => user.Identity?.Name ?? "inconnu";

    /// <summary>Forme JSON d'un incident (inclut les compteurs calculés).</summary>
    private static object ToDto(Incident i) => new
    {
        i.Id, i.Title, i.Type, i.Location, i.CreatedAt, i.CreatedBy, i.ClosedAt, i.ClosedBy, i.DeletedAt, i.DeletedBy,
        i.InternalCount, i.VisitorCount, i.TotalCount, i.IsClosed,
    };

    /// <summary>Réponse 400 avec message.</summary>
    private static IResult Problem(string error) => Results.BadRequest(new { error });

    /// <summary>Limite de 256 caractères par champ texte (évite le stockage de contenus démesurés).</summary>
    private static bool TooLong(params string?[] values) => values.Any(v => v?.Length > 256);

    /// <summary>Traduit un OpResult métier en réponse HTTP (voir le tableau en tête de fichier).</summary>
    private static IResult ToResult<T>(OpResult<T> result, Func<T, object> map, bool created = false) => result.Status switch
    {
        OpStatus.Ok when created => Results.Json(map(result.Value!), statusCode: StatusCodes.Status201Created),
        OpStatus.Ok => Results.Ok(map(result.Value!)),
        OpStatus.NotFound => Results.NotFound(new { error = result.Error }),
        _ => Results.Conflict(new { error = result.Error }),
    };

    /// <summary>
    /// Produit le fichier CSV : un en-tête récapitulatif (incident, totaux), puis une ligne par personne
    /// présente (internes puis visiteurs, chacun par ordre alphabétique). Séparateur « ; » (Excel français).
    /// </summary>
    private static string BuildCsv(Incident incident, IEnumerable<Person> persons)
    {
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        var cmp = StringComparer.Create(fr, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);
        var sb = new StringBuilder();
        sb.AppendLine($"Incident;{Csv(incident.Title)}");
        sb.AppendLine($"Type;{Csv(incident.Type)}");
        sb.AppendLine($"Lieu;{Csv(incident.Location)}");
        sb.AppendLine($"Export;{DateTime.Now.ToString("dd/MM/yyyy HH:mm", fr)}");
        sb.AppendLine($"Internes;{incident.InternalCount}");
        sb.AppendLine($"Visiteurs;{incident.VisitorCount}");
        sb.AppendLine($"Total;{incident.TotalCount}");
        sb.AppendLine();
        sb.AppendLine("Catégorie;Nom;Prénom;Nom affiché;Identifiant;Service / Société;Ajouté le;Ajouté par");
        var ordered = persons
            .OrderBy(p => p.Kind == PersonKinds.Internal ? 0 : 1)
            .ThenBy(p => p.LastName ?? p.DisplayName, cmp)
            .ThenBy(p => p.FirstName ?? "", cmp);
        foreach (var p in ordered)
        {
            sb.AppendLine(string.Join(';',
                p.Kind == PersonKinds.Internal ? "Interne" : "Visiteur",
                Csv(p.LastName), Csv(p.FirstName), Csv(p.DisplayName), Csv(p.Login),
                Csv(p.Kind == PersonKinds.Internal ? p.Department : p.Company),
                p.AddedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", fr), Csv(p.AddedBy)));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Prépare une valeur pour le CSV :
    ///  - Protection contre l'injection de formules : une valeur commençant par = + - @ (ou tabulation /
    ///    retour chariot) serait interprétée comme une formule par Excel ; on la préfixe d'une apostrophe.
    ///    Exemple : un visiteur nommé « =HYPERLINK(...) » s'affiche comme du texte.
    ///  - Les valeurs contenant ; " ou un saut de ligne sont entourées de guillemets (guillemets doublés).
    /// </summary>
    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if ("=+-@\t\r".Contains(value[0])) value = "'" + value;
        return value.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}
