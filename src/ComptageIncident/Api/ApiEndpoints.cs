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
    public const string CsrfHeader = "X-Requested-With";
    public const string CsrfValue = "ComptageIncident";

    public sealed record CreateIncidentRequest(string? Title, string? Type, string? Location);
    public sealed record AddInternalRequest(string? Login, string? FirstName, string? LastName, string? DisplayName, string? Department);
    public sealed record AddVisitorRequest(string? FullName, string? Company);

    public static void MapApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/me", (ClaimsPrincipal user) => new { name = UserName(user) });

        api.MapGet("/config", (IOptions<DirectoryOptions> dir, IOptions<TrashOptions> trash) => new
        {
            minSearchLength = dir.Value.MinSearchLength,
            trashRetentionHours = trash.Value.RetentionHours,
            incidentTypes = IncidentTypes.All,
        });

        // ------------------------------------------------------------ Incidents

        api.MapGet("/incidents", (IncidentRepository repo) => repo.ListIncidents().Select(ToDto));

        api.MapPost("/incidents", (CreateIncidentRequest req, IncidentRepository repo, ClaimsPrincipal user) =>
        {
            var title = req.Title?.Trim();
            if (string.IsNullOrEmpty(title)) return Problem("Le titre de l'incident est obligatoire.");
            if (title.Length > 200) return Problem("Le titre est trop long (200 caractères max).");
            var type = IncidentTypes.All.FirstOrDefault(t => string.Equals(t, req.Type?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (type is null) return Problem("Type d'incident inconnu.");
            var location = req.Location?.Trim();
            if (location?.Length > 200) return Problem("Le lieu est trop long (200 caractères max).");

            var incident = repo.CreateIncident(title, type, string.IsNullOrEmpty(location) ? null : location, UserName(user));
            return Results.Created($"/api/incidents/{incident.Id}", ToDto(incident));
        });

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

        api.MapPost("/incidents/{id:long}/close", (long id, IncidentRepository repo, ClaimsPrincipal user) =>
            ToResult(repo.CloseIncident(id, UserName(user)), ToDto));

        api.MapPost("/incidents/{id:long}/reopen", (long id, IncidentRepository repo, ClaimsPrincipal user) =>
            ToResult(repo.ReopenIncident(id, UserName(user)), ToDto));

        api.MapDelete("/incidents/{id:long}", (long id, IncidentRepository repo, ClaimsPrincipal user) =>
            ToResult(repo.TrashIncident(id, UserName(user)), ToDto));

        api.MapGet("/incidents/{id:long}/audit", (long id, IncidentRepository repo) =>
            repo.GetIncident(id) is null ? Results.NotFound(new { error = "Incident introuvable." }) : Results.Ok(repo.ListAudit(id)));

        api.MapGet("/incidents/{id:long}/export.csv", (long id, IncidentRepository repo) =>
        {
            var incident = repo.GetIncident(id);
            if (incident is null) return Results.NotFound(new { error = "Incident introuvable." });
            var csv = BuildCsv(incident, repo.ListPersons(id).Where(p => p.RemovedAt is null));
            var fileName = $"comptage-incident-{incident.Id}-{DateTime.Now:yyyyMMdd-HHmm}.csv";
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv; charset=utf-8", fileName);
        });

        // ------------------------------------------------------------ Corbeille

        api.MapGet("/trash", (IncidentRepository repo) =>
            repo.ListTrash().Select(i => new
            {
                incident = ToDto(i),
                purgeAt = i.DeletedAt!.Value + repo.Retention,
            }));

        api.MapPost("/trash/{id:long}/restore", (long id, IncidentRepository repo, ClaimsPrincipal user) =>
            ToResult(repo.RestoreIncident(id, UserName(user)), ToDto));

        // ------------------------------------------------------------ Personnes

        api.MapPost("/incidents/{id:long}/persons/internal", (long id, AddInternalRequest req, IncidentRepository repo, ClaimsPrincipal user) =>
        {
            var login = req.Login?.Trim();
            if (string.IsNullOrEmpty(login) || login.Length > 256) return Problem("Identifiant de la personne manquant ou invalide.");
            if (TooLong(req.FirstName, req.LastName, req.DisplayName, req.Department)) return Problem("Champ trop long.");
            return ToResult(repo.AddInternal(id, new NewInternalPerson(login, req.FirstName, req.LastName, req.DisplayName, req.Department), UserName(user)),
                p => p, created: true);
        });

        api.MapPost("/incidents/{id:long}/persons/visitor", (long id, AddVisitorRequest req, IncidentRepository repo, ClaimsPrincipal user) =>
        {
            var name = req.FullName?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length < 2) return Problem("Le nom complet du visiteur est obligatoire.");
            if (TooLong(name, req.Company)) return Problem("Champ trop long.");
            return ToResult(repo.AddVisitor(id, new NewVisitor(name, req.Company), UserName(user)), p => p, created: true);
        });

        api.MapDelete("/incidents/{id:long}/persons/{personId:long}", (long id, long personId, IncidentRepository repo, ClaimsPrincipal user) =>
            ToResult(repo.RemovePerson(id, personId, UserName(user)), p => p));

        api.MapPost("/incidents/{id:long}/persons/{personId:long}/restore", (long id, long personId, IncidentRepository repo, ClaimsPrincipal user) =>
            ToResult(repo.RestorePerson(id, personId, UserName(user)), p => p));

        // ------------------------------------------------------------ Annuaire

        api.MapGet("/directory/search", async (string? q, IDirectoryService directory, IOptions<DirectoryOptions> options,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var query = q?.Trim() ?? "";
            if (query.Length < options.Value.MinSearchLength)
                return Problem($"Saisissez au moins {options.Value.MinSearchLength} caractères.");
            if (query.Length > 100) return Problem("Recherche trop longue.");
            try
            {
                return Results.Ok(await directory.SearchAsync(query, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                loggerFactory.CreateLogger("Directory").LogError(ex, "Recherche annuaire impossible");
                return Results.Json(new { error = "Annuaire indisponible. Utilisez « Ajouter manuellement » en attendant." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
    }

    /// <summary>
    /// Les identifiants Windows étant envoyés automatiquement par le navigateur, toute requête qui modifie des données
    /// doit porter un en-tête personnalisé (impossible à poser depuis un autre site sans préflight CORS).
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

    private static string UserName(ClaimsPrincipal user) => user.Identity?.Name ?? "inconnu";

    private static object ToDto(Incident i) => new
    {
        i.Id, i.Title, i.Type, i.Location, i.CreatedAt, i.CreatedBy, i.ClosedAt, i.ClosedBy, i.DeletedAt, i.DeletedBy,
        i.InternalCount, i.VisitorCount, i.TotalCount, i.IsClosed,
    };

    private static IResult Problem(string error) => Results.BadRequest(new { error });

    private static bool TooLong(params string?[] values) => values.Any(v => v?.Length > 256);

    private static IResult ToResult<T>(OpResult<T> result, Func<T, object> map, bool created = false) => result.Status switch
    {
        OpStatus.Ok when created => Results.Json(map(result.Value!), statusCode: StatusCodes.Status201Created),
        OpStatus.Ok => Results.Ok(map(result.Value!)),
        OpStatus.NotFound => Results.NotFound(new { error = result.Error }),
        _ => Results.Conflict(new { error = result.Error }),
    };

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

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        // Neutralise les formules Excel (injection CSV).
        if ("=+-@\t\r".Contains(value[0])) value = "'" + value;
        return value.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}
