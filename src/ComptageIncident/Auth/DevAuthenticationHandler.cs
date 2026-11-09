using System.Security.Claims;
using System.Text.Encodings.Web;
using ComptageIncident.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ComptageIncident.Auth;

/// <summary>
/// Authentification simulée, activée UNIQUEMENT en environnement Development lorsque Auth:DevUser est renseigné.
/// Permet de tester l'application hors d'un domaine Windows. Jamais utilisée en production.
/// </summary>
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

        // En-tête optionnel pour simuler plusieurs utilisateurs en développement.
        if (Request.Headers.TryGetValue("X-Dev-User", out var headerUser) && !string.IsNullOrWhiteSpace(headerUser))
            user = headerUser.ToString();

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
