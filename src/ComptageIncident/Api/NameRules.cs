// =====================================================================================================
//  Api/NameRules.cs : liste blanche des caractères autorisés dans les noms SAISIS par l'utilisateur
// =====================================================================================================
//  S'applique à :
//    - la recherche dans l'annuaire (GET /api/directory/search?q=…) ;
//    - le nom complet d'un visiteur (POST /api/incidents/{id}/persons/visitor).
//  Ne s'applique PAS :
//    - à la société du visiteur ni au titre / lieu de l'incident (chiffres et ponctuation légitimes :
//      « 3M », « Bât. B2 ») ;
//    - aux noms provenant de l'annuaire (ils ne sont pas saisis, et refuser un collègue réel pendant une
//      évacuation à cause d'un caractère inattendu dans l'AD serait plus grave que le risque couvert).
//
//  Caractères autorisés :
//    A-Z a-z                 lettres latines de base
//    À-Ö Ø-ö ø-ÿ             lettres accentuées Latin-1 : é è ê ë à â ä ç ï î ô ö ù û ü ÿ ñ ß æ ø å…
//                            (les plages excluent volontairement × U+00D7 et ÷ U+00F7)
//    U+0100–U+017F           Latin étendu A : œ Œ Ÿ ł ń ś ź ż č ř š ž ő ű ğ ş ı İ ā ē…
//    U+0218–U+021B           ș Ș ț Ț (roumain)
//    '  ’                    apostrophe droite U+0027 et apostrophe typographique U+2019 (O'Brien, D’Arc)
//    -                       trait d'union (Jean-Pierre)
//    espace                  noms composés (De La Fontaine) et recherche sur plusieurs mots (dupo jea)
//  Tout autre caractère (chiffres, * ( ) \ < > " ; = @ …) est refusé avec une erreur 400.
//
//  Normalisation : certains claviers (macOS, iOS) envoient un « é » en deux caractères (e + accent
//  combinant, forme NFD). La saisie est d'abord recomposée en forme NFC, sinon ces noms seraient refusés.
//
//  Cette liste blanche s'AJOUTE aux protections existantes (échappement LDAP, requêtes SQL paramétrées,
//  affichage en textContent) : elle ne les remplace pas. Voir docs/06-securite.md (S20).
//  La même règle est reproduite dans wwwroot/js/app.js (NAME_PATTERN) pour prévenir l'utilisateur avant
//  l'envoi, mais c'est ce contrôle côté serveur qui fait foi.
// =====================================================================================================

using System.Text;
using System.Text.RegularExpressions;

namespace ComptageIncident.Api;

public static partial class NameRules
{
    /// <summary>Description lisible des caractères autorisés (utilisée dans les messages d'erreur).</summary>
    public const string AllowedDescription = "lettres (accents compris), espace, apostrophe et trait d'union";

    /// <summary>Liste blanche : la chaîne entière ne doit contenir que des caractères autorisés.</summary>
    [GeneratedRegex(@"^[A-Za-zÀ-ÖØ-öø-ÿĀ-ſȘ-ț'’\- ]+$", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedPattern();

    /// <summary>Au moins une lettre (refuse une saisie composée uniquement d'espaces, d'apostrophes ou de tirets).</summary>
    [GeneratedRegex(@"[A-Za-zÀ-ÖØ-öø-ÿĀ-ſȘ-ț]", RegexOptions.CultureInvariant)]
    private static partial Regex LetterPattern();

    /// <summary>Recompose les caractères accentués (forme NFC) et retire les espaces en début et fin.</summary>
    public static string Normalize(string? value) => (value ?? "").Normalize(NormalizationForm.FormC).Trim();

    /// <summary>Vrai si la valeur (déjà normalisée) ne contient que des caractères autorisés et au moins une lettre.</summary>
    public static bool IsValid(string value) => AllowedPattern().IsMatch(value) && LetterPattern().IsMatch(value);
}
