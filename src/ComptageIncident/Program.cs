using ComptageIncident.Api;
using ComptageIncident.Auth;
using ComptageIncident.Data;
using ComptageIncident.Directory;
using ComptageIncident.Options;
using ComptageIncident.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // En service Windows, le répertoire courant est C:\Windows\System32 : on se cale sur le dossier de l'exe.
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});

// Mode autonome : peut tourner comme service Windows (sans effet sous IIS ou en console).
builder.Host.UseWindowsService(o => o.ServiceName = "ComptageIncident");

builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.Section));
builder.Services.Configure<DirectoryOptions>(builder.Configuration.GetSection(DirectoryOptions.Section));
builder.Services.Configure<TrashOptions>(builder.Configuration.GetSection(TrashOptions.Section));
builder.Services.Configure<DevAuthOptions>(builder.Configuration.GetSection(DevAuthOptions.Section));

// ---------------------------------------------------------------- Authentification Windows
var devUser = builder.Configuration[$"{DevAuthOptions.Section}:DevUser"];
if (builder.Environment.IsDevelopment() && !string.IsNullOrWhiteSpace(devUser))
{
    builder.Services.AddAuthentication(DevAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevAuthenticationHandler>(DevAuthenticationHandler.SchemeName, null);
}
else
{
    // Sous IIS / HTTP.sys : délègue à l'authentification Windows du serveur.
    // Sous Kestrel (mode autonome) : gère Kerberos / NTLM directement.
    builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
}
builder.Services.AddAuthorization(o => o.FallbackPolicy = o.DefaultPolicy);

// ---------------------------------------------------------------- Données & services
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<IncidentRepository>();
builder.Services.AddHostedService<TrashPurgeService>();

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

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

var app = builder.Build();

// Crée la base au démarrage pour détecter immédiatement un problème de droits sur le dossier.
var db = app.Services.GetRequiredService<Database>();
app.Logger.LogInformation("Base de données : {Path}", db.FilePath);

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

app.UseAuthentication();
app.UseAuthorization();          // Politique par défaut : utilisateur authentifié requis (fichiers statiques compris).
app.UseCsrfHeaderCheck();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});

app.MapApi();

app.Run();

public partial class Program;
