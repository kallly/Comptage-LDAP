// =====================================================================================================
//  Directory/IDirectoryService.cs : contrat de recherche dans l'annuaire + construction du filtre LDAP
// =====================================================================================================
//  L'application ne dépend que de cette interface : l'annuaire réel (AdDirectoryService) et l'annuaire
//  fictif de développement (FakeDirectoryService) sont interchangeables via Directory:Provider.
//
//  L'annuaire est utilisé UNIQUEMENT en LECTURE, et uniquement pour proposer des suggestions pendant la
//  saisie. Aucune donnée n'est écrite dans l'AD. Détails : docs/04-annuaire-ldap.md
// =====================================================================================================

namespace ComptageIncident.Directory;

/// <summary>Personne trouvée dans l'annuaire (renvoyée à l'interface comme suggestion).</summary>
/// <param name="Login">Identifiant Windows (sAMAccountName) : sert de clé pour détecter les doublons.</param>
/// <param name="FirstName">Prénom (givenName).</param>
/// <param name="LastName">Nom (sn).</param>
/// <param name="DisplayName">Nom affiché (displayName).</param>
/// <param name="Email">Adresse e-mail (mail), non stockée en base.</param>
public sealed record DirectoryPerson(
    string Login,
    string? FirstName,
    string? LastName,
    string DisplayName,
    string? Email);

public interface IDirectoryService
{
    /// <summary>
    /// Recherche les personnes dont le nom, le prénom, le nom affiché ou le CN COMMENCE par chacun des
    /// mots saisis. Exemple : « dupo jea » trouve « DUPONT Jean » mais pas « DUPOND Marie ».
    /// </summary>
    Task<IReadOnlyList<DirectoryPerson>> SearchAsync(string query, CancellationToken cancellationToken);
}

/// <summary>Fonctions pures (sans accès réseau) de préparation de la recherche, testées unitairement.</summary>
public static class DirectorySearch
{
    /// <summary>
    /// Découpe la saisie en mots (séparateurs : espace, tabulation, virgule, point-virgule).
    /// Au plus 4 mots sont retenus, pour borner la taille du filtre LDAP.
    /// </summary>
    public static string[] Terms(string query) =>
        query.Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .Take(4)
             .ToArray();

    /// <summary>
    /// Échappe une valeur avant de l'insérer dans un filtre LDAP (RFC 4515, section 3).
    /// Sans cet échappement, un utilisateur pourrait saisir « * » ou « )(… » pour modifier le sens du
    /// filtre (injection LDAP). Les 5 caractères spéciaux sont remplacés par leur code hexadécimal :
    ///   \  → \5c    *  → \2a    (  → \28    )  → \29    NUL → \00
    /// </summary>
    public static string EscapeLdap(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(c switch
            {
                '\\' => @"\5c",
                '*' => @"\2a",
                '(' => @"\28",
                ')' => @"\29",
                '\0' => @"\00",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }

    /// <summary>
    /// Construit le filtre LDAP final.
    /// Pour la saisie « dupo jea » et le filtre de base B, le résultat est :
    /// <code>
    /// (&amp; B
    ///    (|(sn=dupo*)(givenName=dupo*)(displayName=dupo*)(cn=dupo*))
    ///    (|(sn=jea*)(givenName=jea*)(displayName=jea*)(cn=jea*)) )
    /// </code>
    /// Autrement dit : (filtre de base) ET (chaque mot commence l'un des 4 attributs).
    /// Le seul « * » présent est celui ajouté ICI (recherche « commence par ») ; ceux saisis par
    /// l'utilisateur sont échappés et donc traités comme du texte.
    /// </summary>
    public static string BuildFilter(string baseFilter, string query)
    {
        var termFilters = Terms(query).Select(t =>
        {
            var e = EscapeLdap(t);
            return $"(|(sn={e}*)(givenName={e}*)(displayName={e}*)(cn={e}*))";
        });
        return $"(&{baseFilter}{string.Concat(termFilters)})";
    }
}
