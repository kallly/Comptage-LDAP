// =====================================================================================================
//  Program.cs : point d'entrée de l'application « Comptage incident »
// =====================================================================================================
//
//  Ce fichier assemble toute l'application. Il est lu de haut en bas au démarrage :
//
//    1. Création de l'hôte web (compatible IIS, service Windows et exécutable en console).
//    2. Chargement de la configuration (appsettings.json) dans des objets typés.
//    3. Choix du mécanisme d'authentification :
//         - Production  : authentification Windows (Negotiate = Kerberos, ou NTLM en repli).
//         - Development : utilisateur simulé (uniquement si Auth:DevUser est renseigné).
//    4. Règle d'autorisation globale : TOUT exige un utilisateur authentifié.
//    5. Enregistrement des services : base SQLite, dépôt de données, annuaire, purge de la corbeille.
//    6. Pipeline HTTP : en-têtes de sécurité → authentification → autorisation → contrôle CSRF
//       → fichiers statiques (interface) → API REST.
//
//  Voir docs/02-architecture.md et docs/03-authentification.md pour les explications détaillées.
// =====================================================================================================

using ComptageIncident.Api;
using ComptageIncident.Auth;
using ComptageIncident.Data;
using ComptageIncident.Directory;
using ComptageIncident.Options;
using ComptageIncident.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;

// -----------------------------------------------------------------------------------------------------
// 1. Création de l'hôte
// -----------------------------------------------------------------------------------------------------
// Le « ContentRoot » est le dossier où l'application cherche appsettings.json, wwwroot (l'interface) et
// App_Data (la base). Par défaut, ASP.NET Core prend le RÉPERTOIRE COURANT, ce qui pose problème hors IIS :
//   - un service Windows démarre avec C:\Windows\System32 comme répertoire courant ;
//   - un exe lancé par un raccourci, une tâche planifiée ou une invite de commande placée ailleurs démarre
//     dans ce dossier-là.
// Dans ces cas, l'interface renverrait 404, appsettings.json serait ignoré et la base serait créée au mauvais
// endroit. On fixe donc le ContentRoot sur le DOSSIER DE L'EXÉCUTABLE dès qu'il contient wwwroot, c'est-à-dire
// pour toute version publiée (IIS, service, exe), quel que soit le répertoire courant.
// En développement (dotnet run), wwwroot est servi depuis le dossier du projet : le comportement par défaut
// est conservé.
var exeDir = AppContext.BaseDirectory;
var isPublishedLayout = System.IO.Directory.Exists(Path.Combine(exeDir, "wwwroot"));
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = isPublishedLayout ? exeDir : null,
});

// Permet le fonctionnement en service Windows (mode autonome, voir deploy/Install-Service.ps1).
// Cet appel est sans effet quand l'application n'est pas lancée par le gestionnaire de services
// (IIS, console, tests) : il est donc sûr de le laisser en permanence.
builder.Host.UseWindowsService(o => o.ServiceName = "ComptageIncident");

// -----------------------------------------------------------------------------------------------------
// 2. Configuration typée
// -----------------------------------------------------------------------------------------------------
// Chaque section de appsettings.json est liée à une classe d'options (voir Options/AppOptions.cs).
// Les valeurs par défaut sont définies dans ces classes : l'application fonctionne même si une clé manque.
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.Section));
builder.Services.Configure<DirectoryOptions>(builder.Configuration.GetSection(DirectoryOptions.Section));
builder.Services.Configure<TrashOptions>(builder.Configuration.GetSection(TrashOptions.Section));
builder.Services.Configure<DevAuthOptions>(builder.Configuration.GetSection(DevAuthOptions.Section));

// -----------------------------------------------------------------------------------------------------
// 3. Authentification
// -----------------------------------------------------------------------------------------------------
// Le mode « Dev » n'est activé que si les DEUX conditions sont réunies :
//   - l'environnement est « Development » (variable ASPNETCORE_ENVIRONMENT) ;
//   - Auth:DevUser est renseigné (appsettings.Development.json).
// En production (web.config et service : ASPNETCORE_ENVIRONMENT=Production), appsettings.Development.json
// n'est même pas chargé, donc ce mode est impossible à activer par erreur.
var devUser = builder.Configuration[$"{DevAuthOptions.Section}:DevUser"];
if (builder.Environment.IsDevelopment() && !string.IsNullOrWhiteSpace(devUser))
{
    builder.Services.AddAuthentication(DevAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevAuthenticationHandler>(DevAuthenticationHandler.SchemeName, null);
}
else
{
    // Authentification Windows intégrée (« Negotiate ») :
    //   - Sous IIS : le gestionnaire détecte qu'IIS fait déjà l'authentification Windows et réutilise
    //     simplement l'identité fournie par IIS (IIS doit avoir « Authentification Windows » activée et
    //     « Anonyme » désactivée : c'est ce que font web.config et deploy/Install-IIS.ps1).
    //   - Sous Kestrel (service Windows / exe) : le gestionnaire réalise lui-même l'échange
    //     Kerberos (ou NTLM en repli) avec le navigateur, via l'API SSPI de Windows.
    // Dans les deux cas, aucun mot de passe ne transite ni n'est stocké par l'application.
    builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
}

// -----------------------------------------------------------------------------------------------------
// 4. Autorisation
// -----------------------------------------------------------------------------------------------------
// « FallbackPolicy = DefaultPolicy » signifie : toute requête qui ne déclare pas explicitement sa propre
// règle (c'est le cas de TOUTES les requêtes de cette application) exige un utilisateur authentifié.
// Cela couvre l'API ET les fichiers statiques (page HTML, JS, CSS) car UseAuthorization est placé avant
// UseStaticFiles plus bas. Un utilisateur non authentifié reçoit 401 + défi « Negotiate », ce qui
// déclenche la connexion automatique du navigateur avec la session Windows.
builder.Services.AddAuthorization(o => o.FallbackPolicy = o.DefaultPolicy);

// -----------------------------------------------------------------------------------------------------
// 5. Services applicatifs
// -----------------------------------------------------------------------------------------------------
// TimeProvider : horloge injectable (les tests la remplacent pour simuler l'écoulement des 24 h de corbeille).
builder.Services.AddSingleton(TimeProvider.System);
// Database : crée le fichier SQLite et son schéma au démarrage, puis fournit des connexions.
builder.Services.AddSingleton<Database>();
// IncidentRepository : toutes les règles métier (corbeille, doublons, clôture, journal d'audit).
builder.Services.AddSingleton<IncidentRepository>();
// Tâche de fond : supprime définitivement les incidents restés plus de 24 h en corbeille.
builder.Services.AddHostedService<TrashPurgeService>();

// Choix de l'annuaire :
//   - "Fake"            : annuaire fictif (développement / tests) ;
//   - "ActiveDirectory" : annuaire réel, uniquement possible sous Windows.
// On refuse de démarrer si l'annuaire réel est demandé hors Windows, plutôt que de tomber silencieusement
// sur l'annuaire fictif (ce qui serait trompeur en production).
var provider = builder.Configuration[$"{DirectoryOptions.Section}:Provider"] ?? "ActiveDirectory";
if (string.Equals(provider, "Fake", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IDirectoryService, FakeDirectoryService>();
}
else if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<IDirectoryService, AdDirectoryService>();
}
else
{
    throw new PlatformNotSupportedException(
        "Le fournisseur d'annuaire 'ActiveDirectory' nécessite Windows. Utilisez Directory:Provider=Fake pour le développement.");
}

// Sérialisation JSON en camelCase (ex. « internalCount ») : c'est ce qu'attend wwwroot/js/app.js.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

var app = builder.Build();

// On instancie la base immédiatement (et non à la première requête) : si le dossier App_Data n'est pas
// accessible en écriture, l'application échoue AU DÉMARRAGE avec un message clair dans les journaux,
// au lieu d'échouer plus tard pendant un incident réel.
var db = app.Services.GetRequiredService<Database>();
app.Logger.LogInformation("Base de données : {Path}", db.FilePath);

// -----------------------------------------------------------------------------------------------------
// 6. Pipeline HTTP (l'ordre des middlewares est important)
// -----------------------------------------------------------------------------------------------------

// 6.a En-têtes de sécurité ajoutés à TOUTES les réponses (voir docs/06-securite.md) :
//   - X-Content-Type-Options: nosniff  → le navigateur n'interprète pas un fichier autrement que son type.
//   - X-Frame-Options: DENY            → l'application ne peut pas être intégrée dans un cadre (clickjacking).
//   - Referrer-Policy: no-referrer     → les URL internes ne fuient pas vers d'autres sites.
//   - Content-Security-Policy          → seuls les scripts/styles servis par l'application elle-même sont
//                                        exécutés : aucun script en ligne, aucun CDN, aucune ressource externe.
//   - Cache-Control: no-store (API)    → les listes de personnes ne sont jamais conservées en cache.
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; style-src 'self'; script-src 'self'; img-src 'self' data:; frame-ancestors 'none'";
    if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.Headers.CacheControl = "no-store";
    await next();
});

// 6.b Identification de l'utilisateur (Windows ou Dev), puis 6.c application de la règle « authentifié requis ».
app.UseAuthentication();
app.UseAuthorization();

// 6.d Protection CSRF : toute requête /api qui modifie des données doit porter l'en-tête
//     X-Requested-With: ComptageIncident (voir Api/ApiEndpoints.cs, UseCsrfHeaderCheck).
app.UseCsrfHeaderCheck();

// 6.e Interface Web : « / » sert wwwroot/index.html. Placé APRÈS l'autorisation, donc protégé lui aussi.
//     « no-cache » : le navigateur revalide les fichiers à chaque chargement, de sorte qu'une mise à jour
//     de l'application est prise en compte immédiatement par tous les postes.
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});

// 6.f API REST (toutes les routes /api/...) : voir Api/ApiEndpoints.cs.
app.MapApi();

app.Run();

/// <summary>
/// Déclaration partielle rendant la classe Program visible depuis le projet de tests
/// (WebApplicationFactory&lt;Program&gt; démarre l'application en mémoire pour les tests d'API).
/// </summary>
public partial class Program;
