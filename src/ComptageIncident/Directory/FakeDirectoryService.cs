// =====================================================================================================
//  Directory/FakeDirectoryService.cs : annuaire FICTIF (développement et tests uniquement)
// =====================================================================================================
//  Activé seulement si Directory:Provider = "Fake" (appsettings.Development.json). La configuration de
//  production (appsettings.json) utilise "ActiveDirectory".
//  Il reproduit le comportement de l'annuaire réel (recherche « commence par » sur nom/prénom, insensible
//  aux accents et à la casse, tri par nom, limite de résultats) pour tester l'interface hors domaine.
//  Les noms ci-dessous sont inventés.
// =====================================================================================================

using System.Globalization;
using System.Text;
using ComptageIncident.Options;
using Microsoft.Extensions.Options;

namespace ComptageIncident.Directory;

/// <summary>Annuaire fictif pour le développement et les tests (hors domaine Windows).</summary>
public sealed class FakeDirectoryService : IDirectoryService
{
    /// <summary>Personnes fictives (prénom, nom, service).</summary>
    private static readonly (string First, string Last, string Dept)[] People =
    [
        ("Jean", "Dupont", "Production"), ("Marie", "Dupond", "Qualité"), ("Pierre", "Martin", "Maintenance"),
        ("Sophie", "Bernard", "RH"), ("Luc", "Thomas", "Logistique"), ("Julie", "Petit", "Production"),
        ("Nicolas", "Robert", "Informatique"), ("Camille", "Richard", "Laboratoire"), ("Antoine", "Durand", "Sécurité"),
        ("Émilie", "Dubois", "Comptabilité"), ("Hugo", "Moreau", "Maintenance"), ("Léa", "Laurent", "Achats"),
        ("Thomas", "Simon", "Production"), ("Chloé", "Michel", "Laboratoire"), ("Maxime", "Lefèvre", "Logistique"),
        ("Manon", "Leroy", "Qualité"), ("Julien", "Roux", "Informatique"), ("Sarah", "David", "Direction"),
        ("Alexandre", "Bertrand", "Production"), ("Laura", "Morel", "RH"), ("Romain", "Fournier", "Sécurité"),
        ("Pauline", "Girard", "Laboratoire"), ("Kevin", "Bonnet", "Maintenance"), ("Céline", "Dupuis", "Achats"),
        ("Mathieu", "Lambert", "Production"), ("Anaïs", "Fontaine", "Qualité"), ("Sébastien", "Rousseau", "Logistique"),
        ("Clara", "Vincent", "Comptabilité"), ("François", "Muller", "Production"), ("Inès", "Lefebvre", "Informatique"),
        ("Benoît", "Faure", "Maintenance"), ("Océane", "André", "Laboratoire"), ("Damien", "Mercier", "Sécurité"),
        ("Jeanne", "Blanc", "Direction"), ("Yann", "Guérin", "Production"), ("Élodie", "Boyer", "RH"),
        ("Jean-Pierre", "Garnier", "Maintenance"), ("Marie-Claire", "Chevalier", "Qualité"), ("Olivier", "François", "Logistique"),
        ("Jeannine", "Dupontel", "Production"),
    ];

    private readonly int _maxResults;

    public FakeDirectoryService(IOptions<DirectoryOptions> options) => _maxResults = Math.Max(1, options.Value.MaxResults);

    public Task<IReadOnlyList<DirectoryPerson>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var terms = DirectorySearch.Terms(query).Select(Normalize).ToArray();
        IReadOnlyList<DirectoryPerson> result = People
            // Chaque mot doit commencer le prénom, le nom ou une partie d'un prénom composé.
            .Where(p => terms.All(t =>
                Normalize(p.First).StartsWith(t, StringComparison.Ordinal) ||
                Normalize(p.Last).StartsWith(t, StringComparison.Ordinal) ||
                Normalize(p.First).Split('-').Any(part => part.StartsWith(t, StringComparison.Ordinal))))
            .OrderBy(p => p.Last, StringComparer.Create(CultureInfo.GetCultureInfo("fr-FR"), true))
            .ThenBy(p => p.First)
            .Take(_maxResults)
            .Select(p => new DirectoryPerson(
                Login: Normalize(p.First)[..1] + Normalize(p.Last).Replace(" ", ""), // ex. « jdupont »
                FirstName: p.First,
                LastName: p.Last,
                DisplayName: $"{p.Last.ToUpperInvariant()} {p.First}",
                Department: p.Dept,
                Email: $"{Normalize(p.First)}.{Normalize(p.Last)}@exemple.local"))
            .ToList();
        return Task.FromResult(result);
    }

    /// <summary>Minuscules et suppression des accents (« Émilie » → « emilie »).</summary>
    private static string Normalize(string s)
    {
        var decomposed = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
