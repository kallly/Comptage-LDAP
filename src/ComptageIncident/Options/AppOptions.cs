// =====================================================================================================
//  Options/AppOptions.cs : paramètres de l'application
// =====================================================================================================
//  Chaque classe correspond à une section de appsettings.json. Les valeurs ci-dessous sont les valeurs
//  PAR DÉFAUT, utilisées si la clé est absente du fichier de configuration.
//  Référence complète des paramètres : docs/09-exploitation.md (section « Configuration »).
// =====================================================================================================

namespace ComptageIncident.Options;

/// <summary>Section « Database » : emplacement de la base SQLite.</summary>
public sealed class DatabaseOptions
{
    public const string Section = "Database";

    /// <summary>
    /// Chemin du fichier SQLite. Relatif au dossier de l'application (ContentRoot) ou absolu.
    /// Le dossier est créé automatiquement. Le compte qui exécute l'application (pool IIS ou service)
    /// doit y avoir le droit « Modifier » : les scripts d'installation le configurent.
    /// </summary>
    public string Path { get; set; } = "App_Data/comptage.db";
}

/// <summary>Section « Directory » : recherche des personnes dans l'annuaire.</summary>
public sealed class DirectoryOptions
{
    public const string Section = "Directory";

    /// <summary>
    /// "ActiveDirectory" (production, Windows uniquement) ou "Fake" (annuaire fictif de développement).
    /// </summary>
    public string Provider { get; set; } = "ActiveDirectory";

    /// <summary>
    /// Racine de recherche LDAP optionnelle, ex. "LDAP://OU=Utilisateurs,DC=entreprise,DC=local".
    /// Vide = tout le domaine du compte qui exécute l'application.
    /// </summary>
    public string? SearchRoot { get; set; }

    /// <summary>
    /// Filtre LDAP appliqué à TOUTES les recherches, combiné (ET logique) avec les mots saisis.
    /// Par défaut : objets « personne » de classe « user », en excluant les comptes désactivés
    /// (bit 2 « ACCOUNTDISABLE » de userAccountControl, testé avec la règle de comparaison binaire
    /// 1.2.840.113556.1.4.803 = « ET bit à bit »).
    /// </summary>
    public string BaseFilter { get; set; } =
        "(&(objectCategory=person)(objectClass=user)(!(userAccountControl:1.2.840.113556.1.4.803:=2)))";

    /// <summary>Nombre minimal de caractères avant de lancer une recherche (contrôlé côté client ET serveur).</summary>
    public int MinSearchLength { get; set; } = 4;

    /// <summary>Nombre maximal de suggestions renvoyées (limite aussi la charge sur les contrôleurs de domaine).</summary>
    public int MaxResults { get; set; } = 20;

    /// <summary>Délai maximal d'une recherche LDAP, en secondes.</summary>
    public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>Section « Trash » : corbeille des incidents supprimés.</summary>
public sealed class TrashOptions
{
    public const string Section = "Trash";

    /// <summary>Durée (heures) pendant laquelle un incident supprimé reste restaurable. Minimum effectif : 1 h.</summary>
    public int RetentionHours { get; set; } = 24;

    /// <summary>Fréquence (minutes) à laquelle la tâche de fond purge les incidents expirés. Minimum effectif : 1 min.</summary>
    public int PurgeIntervalMinutes { get; set; } = 15;
}

/// <summary>Section « Auth » : réglage réservé au DÉVELOPPEMENT.</summary>
public sealed class DevAuthOptions
{
    public const string Section = "Auth";

    /// <summary>
    /// Utilisateur simulé. Pris en compte UNIQUEMENT si l'environnement est « Development »
    /// (voir Program.cs). Ignoré en production même s'il est renseigné.
    /// </summary>
    public string? DevUser { get; set; }
}
