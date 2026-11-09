namespace ComptageIncident.Options;

public sealed class DatabaseOptions
{
    public const string Section = "Database";

    /// <summary>Chemin du fichier SQLite (relatif au dossier de l'application ou absolu).</summary>
    public string Path { get; set; } = "App_Data/comptage.db";
}

public sealed class DirectoryOptions
{
    public const string Section = "Directory";

    /// <summary>"ActiveDirectory" (production, Windows) ou "Fake" (développement / tests).</summary>
    public string Provider { get; set; } = "ActiveDirectory";

    /// <summary>Racine de recherche LDAP optionnelle, ex. "LDAP://OU=Utilisateurs,DC=entreprise,DC=local".
    /// Vide = domaine du compte qui exécute l'application.</summary>
    public string? SearchRoot { get; set; }

    /// <summary>Filtre de base appliqué à toutes les recherches (comptes utilisateurs actifs par défaut).</summary>
    public string BaseFilter { get; set; } =
        "(&(objectCategory=person)(objectClass=user)(!(userAccountControl:1.2.840.113556.1.4.803:=2)))";

    public int MinSearchLength { get; set; } = 4;
    public int MaxResults { get; set; } = 20;
    public int TimeoutSeconds { get; set; } = 10;
}

public sealed class TrashOptions
{
    public const string Section = "Trash";

    /// <summary>Durée pendant laquelle un incident supprimé reste restaurable.</summary>
    public int RetentionHours { get; set; } = 24;

    public int PurgeIntervalMinutes { get; set; } = 15;
}

public sealed class DevAuthOptions
{
    public const string Section = "Auth";

    /// <summary>Utilisateur simulé (uniquement en environnement Development, pour tester hors domaine).</summary>
    public string? DevUser { get; set; }
}
