using System.DirectoryServices;
using System.Runtime.Versioning;
using ComptageIncident.Options;
using Microsoft.Extensions.Options;

namespace ComptageIncident.Directory;

/// <summary>
/// Recherche dans Active Directory avec l'identité du processus :
/// identité du pool d'applications sous IIS (compte machine sur le réseau),
/// compte du service Windows ou utilisateur de la session en mode autonome.
/// Aucun identifiant n'est stocké dans la configuration.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AdDirectoryService : IDirectoryService
{
    private static readonly string[] Properties = ["sAMAccountName", "givenName", "sn", "displayName", "department", "mail"];

    private readonly DirectoryOptions _options;
    private readonly ILogger<AdDirectoryService> _logger;

    public AdDirectoryService(IOptions<DirectoryOptions> options, ILogger<AdDirectoryService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task<IReadOnlyList<DirectoryPerson>> SearchAsync(string query, CancellationToken cancellationToken) =>
        Task.Run(() => Search(query), cancellationToken);

    private IReadOnlyList<DirectoryPerson> Search(string query)
    {
        var filter = DirectorySearch.BuildFilter(_options.BaseFilter, query);
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds));

        // Sans chemin ni identifiants : domaine courant, authentification avec le compte du processus.
        using var root = string.IsNullOrWhiteSpace(_options.SearchRoot)
            ? new DirectoryEntry()
            : new DirectoryEntry(_options.SearchRoot);

        using var searcher = new DirectorySearcher(root, filter, Properties, SearchScope.Subtree)
        {
            SizeLimit = Math.Max(1, _options.MaxResults),
            ClientTimeout = timeout,
            ServerTimeLimit = timeout,
            ReferralChasing = ReferralChasingOption.None,
        };
        searcher.Sort = new SortOption("sn", SortDirection.Ascending);

        var results = new List<DirectoryPerson>();
        try
        {
            using var found = searcher.FindAll();
            foreach (SearchResult r in found)
            {
                var login = Get(r, "sAMAccountName");
                if (string.IsNullOrEmpty(login)) continue;
                var first = Get(r, "givenName");
                var last = Get(r, "sn");
                var display = Get(r, "displayName")
                              ?? string.Join(' ', new[] { last?.ToUpperInvariant(), first }.Where(s => !string.IsNullOrEmpty(s)));
                results.Add(new DirectoryPerson(login, first, last, string.IsNullOrWhiteSpace(display) ? login : display,
                    Get(r, "department"), Get(r, "mail")));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Échec de la recherche LDAP (filtre {Filter})", filter);
            throw new DirectoryUnavailableException("L'annuaire est injoignable ou la recherche a échoué.", ex);
        }
        return results;
    }

    private static string? Get(SearchResult r, string name) =>
        r.Properties.Contains(name) && r.Properties[name].Count > 0 ? r.Properties[name][0]?.ToString() : null;
}

public sealed class DirectoryUnavailableException(string message, Exception inner) : Exception(message, inner);
