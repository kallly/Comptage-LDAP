// =====================================================================================================
//  Directory/AdDirectoryService.cs : recherche dans Active Directory (production)
// =====================================================================================================
//  QUEL COMPTE INTERROGE L'ANNUAIRE ?
//  Aucun identifiant n'est configuré : la connexion LDAP est authentifiée avec l'identité du PROCESSUS
//  (mécanisme « Secure / Negotiate » de Windows) :
//
//    Mode d'hébergement                          Compte vu par le contrôleur de domaine
//    ------------------------------------------  -------------------------------------------
//    IIS, pool en ApplicationPoolIdentity        compte ordinateur du serveur  DOMAINE\SERVEUR$
//    Service Windows LocalSystem/NetworkService  compte ordinateur du serveur  DOMAINE\SERVEUR$
//    Service Windows avec compte de domaine      ce compte de domaine
//    ComptageIncident.exe lancé dans une session l'utilisateur de la session
//
//  IMPORTANT : la recherche n'est PAS faite avec l'identité de l'utilisateur du navigateur (pas de
//  délégation Kerberos). Tous les utilisateurs voient donc les mêmes résultats, et aucune délégation n'est
//  à configurer dans l'AD. Par défaut, tout compte du domaine (y compris un compte ordinateur) peut lire
//  les attributs utilisés ici.
//
//  ATTRIBUTS LUS (lecture seule) : sAMAccountName, givenName, sn, displayName, department, mail.
//  Aucun autre attribut n'est demandé ; rien n'est jamais écrit dans l'annuaire.
//
//  Détails : docs/04-annuaire-ldap.md
// =====================================================================================================

using System.DirectoryServices;
using System.Runtime.Versioning;
using ComptageIncident.Options;
using Microsoft.Extensions.Options;

namespace ComptageIncident.Directory;

/// <summary>Recherche dans Active Directory avec l'identité du processus (voir l'en-tête du fichier).</summary>
[SupportedOSPlatform("windows")]
public sealed class AdDirectoryService : IDirectoryService
{
    /// <summary>Liste exhaustive des attributs demandés au contrôleur de domaine.</summary>
    private static readonly string[] Properties = ["sAMAccountName", "givenName", "sn", "displayName", "department", "mail"];

    private readonly DirectoryOptions _options;
    private readonly ILogger<AdDirectoryService> _logger;

    public AdDirectoryService(IOptions<DirectoryOptions> options, ILogger<AdDirectoryService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// L'API System.DirectoryServices est synchrone (bloquante) : on l'exécute sur un thread du pool pour
    /// ne pas bloquer le traitement des autres requêtes pendant l'interrogation du contrôleur de domaine.
    /// </summary>
    public Task<IReadOnlyList<DirectoryPerson>> SearchAsync(string query, CancellationToken cancellationToken) =>
        Task.Run(() => Search(query), cancellationToken);

    private IReadOnlyList<DirectoryPerson> Search(string query)
    {
        // Filtre construit avec échappement LDAP (voir DirectorySearch.BuildFilter) : pas d'injection possible.
        var filter = DirectorySearch.BuildFilter(_options.BaseFilter, query);
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds));

        // DirectoryEntry SANS chemin ni identifiants :
        //   - le domaine est celui du compte d'exécution (découverte automatique via le DNS / RootDSE) ;
        //   - l'authentification utilise l'identité du processus (AuthenticationTypes.Secure par défaut).
        // Avec Directory:SearchRoot (ex. LDAP://OU=Personnel,DC=entreprise,DC=local), la recherche est
        // limitée à cette branche de l'annuaire, toujours avec l'identité du processus.
        using var root = string.IsNullOrWhiteSpace(_options.SearchRoot)
            ? new DirectoryEntry()
            : new DirectoryEntry(_options.SearchRoot);

        using var searcher = new DirectorySearcher(root, filter, Properties, SearchScope.Subtree)
        {
            SizeLimit = Math.Max(1, _options.MaxResults),     // pas plus de N résultats
            ClientTimeout = timeout,                           // délai côté application
            ServerTimeLimit = timeout,                         // délai demandé au contrôleur de domaine
            ReferralChasing = ReferralChasingOption.None,      // ne pas suivre les renvois vers d'autres domaines
        };
        searcher.Sort = new SortOption("sn", SortDirection.Ascending); // tri par nom de famille

        var results = new List<DirectoryPerson>();
        try
        {
            using var found = searcher.FindAll();
            foreach (SearchResult r in found)
            {
                var login = Get(r, "sAMAccountName");
                if (string.IsNullOrEmpty(login)) continue; // objet sans identifiant : ignoré
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
            // Le détail technique va dans le journal du serveur ; l'utilisateur reçoit un message générique
            // (HTTP 503) l'invitant à utiliser l'ajout manuel en attendant (voir ApiEndpoints).
            _logger.LogError(ex, "Échec de la recherche LDAP (filtre {Filter})", filter);
            throw new DirectoryUnavailableException("L'annuaire est injoignable ou la recherche a échoué.", ex);
        }
        return results;
    }

    /// <summary>Lit la première valeur d'un attribut, ou null s'il est absent.</summary>
    private static string? Get(SearchResult r, string name) =>
        r.Properties.Contains(name) && r.Properties[name].Count > 0 ? r.Properties[name][0]?.ToString() : null;
}

/// <summary>Levée quand l'annuaire ne répond pas (contrôleur injoignable, droits insuffisants, délai dépassé…).</summary>
public sealed class DirectoryUnavailableException(string message, Exception inner) : Exception(message, inner);
