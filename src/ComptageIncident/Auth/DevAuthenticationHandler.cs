// =====================================================================================================
//  Auth/DevAuthenticationHandler.cs : authentification SIMULÉE, réservée au développement
// =====================================================================================================
//  POURQUOI ? Pour développer et tester l'application sur un poste hors domaine (ou sous Linux), où
//  l'authentification Windows n'est pas disponible.
//
//  COMMENT EST-ELLE NEUTRALISÉE EN PRODUCTION ? Program.cs n'enregistre ce gestionnaire que si :
//    1. l'environnement est « Development » (ASPNETCORE_ENVIRONMENT=Development), ET
//    2. Auth:DevUser est renseigné.
//  En production, web.config et le service fixent ASPNETCORE_ENVIRONMENT=Production, le fichier
//  appsettings.Development.json n'est pas chargé, et c'est l'authentification Windows (Negotiate) qui est
//  enregistrée À LA PLACE de celui-ci : les deux ne coexistent jamais. Ce gestionnaire n'existe donc tout
//  simplement pas dans l'application en production, et l'en-tête X-Dev-User y est sans effet.
// =====================================================================================================

using System.Security.Claims;
using System.Text.Encodings.Web;
using ComptageIncident.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ComptageIncident.Auth;

/// <summary>Authentifie chaque requête avec l'utilisateur fictif Auth:DevUser (développement uniquement).</summary>
public sealed class DevAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<DevAuthOptions> devOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Dev";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = devOptions.Value.DevUser;
        if (string.IsNullOrWhiteSpace(user)) return Task.FromResult(AuthenticateResult.NoResult());

        // En-tête facultatif permettant de simuler plusieurs agents différents pendant les tests.
        if (Request.Headers.TryGetValue("X-Dev-User", out var headerUser) && !string.IsNullOrWhiteSpace(headerUser))
            user = headerUser.ToString();

        // Même forme d'identité que l'authentification Windows : la revendication « Name » contient DOMAINE\login.
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
