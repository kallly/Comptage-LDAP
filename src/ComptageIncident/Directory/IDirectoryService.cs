namespace ComptageIncident.Directory;

public sealed record DirectoryPerson(
    string Login,
    string? FirstName,
    string? LastName,
    string DisplayName,
    string? Department,
    string? Email);

public interface IDirectoryService
{
    /// <summary>Recherche des personnes dont le nom ou le prénom commence par chacun des mots saisis.</summary>
    Task<IReadOnlyList<DirectoryPerson>> SearchAsync(string query, CancellationToken cancellationToken);
}

public static class DirectorySearch
{
    /// <summary>Découpe la saisie en mots (ex. "dupo jea" → ["dupo", "jea"]).</summary>
    public static string[] Terms(string query) =>
        query.Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .Take(4)
             .ToArray();

    /// <summary>Échappe une valeur pour un filtre LDAP (RFC 4515).</summary>
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

    /// <summary>Construit le filtre LDAP : chaque mot doit commencer le nom, le prénom, le nom affiché ou le CN.</summary>
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
