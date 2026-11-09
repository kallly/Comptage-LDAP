// =====================================================================================================
//  SecurityTests.cs : tests de conformité sécurité
// =====================================================================================================
//  Ces tests démarrent l'application en mémoire et vérifient les garanties décrites dans
//  docs/06-securite.md. Ils peuvent être relancés par un auditeur avec « dotnet test ».
// =====================================================================================================

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ComptageIncident.Tests;

/// <summary>
/// Démarre la VRAIE application (processus séparé, serveur Kestrel, comme en mode autonome) en
/// environnement PRODUCTION, donc avec l'authentification Windows « Negotiate » réelle.
/// Seul l'annuaire est remplacé par l'annuaire fictif (l'AD n'est pas disponible pendant les tests).
/// Tentative délibérée : Auth:DevUser est renseigné, il doit être IGNORÉ hors Development.
/// </summary>
public sealed class ProductionServerFixture : IDisposable
{
    private readonly System.Diagnostics.Process _process;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "comptage-prod-" + Guid.NewGuid().ToString("N"));

    public Uri BaseAddress { get; }

    public ProductionServerFixture()
    {
        // Port libre choisi par le système.
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        BaseAddress = new Uri($"http://127.0.0.1:{port}");

        // Dossier du projet (contient appsettings.json de production et wwwroot) = ContentRoot de l'application.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ComptageIncident.sln"))) dir = dir.Parent;
        var projectDir = Path.Combine(dir!.FullName, "src", "ComptageIncident");

        var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"\"{Path.Combine(AppContext.BaseDirectory, "ComptageIncident.dll")}\"")
        {
            WorkingDirectory = projectDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        psi.Environment["Directory__Provider"] = "Fake";
        psi.Environment["Database__Path"] = Path.Combine(_dir, "prod.db");
        psi.Environment["Auth__DevUser"] = "PIRATE\\intrus";
        psi.Environment["Kestrel__Endpoints__Http__Url"] = BaseAddress.ToString();
        _process = System.Diagnostics.Process.Start(psi)!;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        // Attente du démarrage (30 s max).
        using var probe = new HttpClient();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try { probe.GetAsync(BaseAddress).GetAwaiter().GetResult(); break; }
            catch when (DateTime.UtcNow < deadline && !_process.HasExited) { Thread.Sleep(200); }
        }
    }

    public HttpClient CreateClient() => new() { BaseAddress = BaseAddress };

    public void Dispose()
    {
        try { _process.Kill(entireProcessTree: true); _process.WaitForExit(5000); } catch { /* déjà arrêté */ }
        _process.Dispose();
        try { System.IO.Directory.Delete(_dir, true); } catch { /* ignoré */ }
    }
}

public sealed class ProductionSecurityTests(ProductionServerFixture server) : IClassFixture<ProductionServerFixture>
{
    [Theory]
    [InlineData("/")]                       // page d'accueil (fichier statique)
    [InlineData("/index.html")]
    [InlineData("/js/app.js")]
    [InlineData("/api/me")]
    [InlineData("/api/incidents")]
    [InlineData("/api/trash")]
    [InlineData("/api/directory/search?q=dupo")]
    public async Task Sans_authentification_Windows_tout_est_refuse_avec_un_defi_Negotiate(string url)
    {
        using var client = server.CreateClient();
        // En-tête X-Dev-User : ne doit avoir aucun effet en production.
        client.DefaultRequestHeaders.Add("X-Dev-User", "PIRATE\\intrus");

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // Le défi « Negotiate » déclenche la connexion automatique du navigateur avec la session Windows.
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Negotiate");
    }

    [Fact]
    public async Task Une_modification_sans_authentification_est_refusee()
    {
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "ComptageIncident");
        var response = await client.PostAsJsonAsync("/api/incidents", new { title = "x", type = "Incendie" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Les_entetes_de_securite_sont_presents_meme_sur_un_refus()
    {
        using var client = server.CreateClient();
        var response = await client.GetAsync("/");
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("script-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
    }
}

/// <summary>Contrôles de robustesse des données (environnement Development, utilisateur simulé).</summary>
public sealed class DataSecurityTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private HttpClient Client()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "ComptageIncident");
        return client;
    }

    [Fact]
    public async Task Une_tentative_d_injection_SQL_est_stockee_comme_du_texte()
    {
        var client = Client();
        var created = await client.PostAsJsonAsync("/api/incidents", new { title = "Test'); DROP TABLE Persons;--", type = "Autre" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        // La table Persons existe toujours et fonctionne ; le titre est conservé à l'identique.
        var add = await client.PostAsJsonAsync($"/api/incidents/{id}/persons/visitor", new { fullName = "Robert'); --" });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/incidents/{id}");
        Assert.Equal("Test'); DROP TABLE Persons;--", detail.GetProperty("incident").GetProperty("title").GetString());
        Assert.Equal("Robert'); --", detail.GetProperty("persons")[0].GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Le_HTML_saisi_est_renvoye_tel_quel_et_jamais_interprete_par_le_serveur()
    {
        var client = Client();
        var created = await client.PostAsJsonAsync("/api/incidents", new { title = "<script>alert(1)</script>", type = "Autre" });
        var json = await created.Content.ReadFromJsonAsync<JsonElement>();
        // Le serveur stocke la chaîne brute ; l'interface l'affiche via textContent (texte, jamais du HTML).
        Assert.Equal("<script>alert(1)</script>", json.GetProperty("title").GetString());
    }

    [Fact]
    public async Task L_export_CSV_neutralise_les_formules_Excel()
    {
        var client = Client();
        var created = await client.PostAsJsonAsync("/api/incidents", new { title = "CSV", type = "Autre" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await client.PostAsJsonAsync($"/api/incidents/{id}/persons/visitor", new { fullName = "=HYPERLINK(\"http://x\")", company = "+33" });

        var csv = await client.GetStringAsync($"/api/incidents/{id}/export.csv");

        Assert.Contains("'=HYPERLINK", csv);
        Assert.Contains("'+33", csv);
    }

    [Fact]
    public async Task Les_champs_trop_longs_sont_refuses()
    {
        var client = Client();
        var response = await client.PostAsJsonAsync("/api/incidents", new { title = new string('x', 201), type = "Autre" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Une_personne_ne_peut_pas_etre_retiree_via_un_autre_incident()
    {
        var client = Client();
        async Task<long> NewIncident()
        {
            var r = await client.PostAsJsonAsync("/api/incidents", new { title = "I", type = "Autre" });
            return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        }
        var a = await NewIncident();
        var b = await NewIncident();
        var add = await client.PostAsJsonAsync($"/api/incidents/{a}/persons/visitor", new { fullName = "Paul Durand" });
        var personId = (await add.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        // URL de l'incident B avec l'identifiant d'une personne de l'incident A : refusé.
        var response = await client.DeleteAsync($"/api/incidents/{b}/persons/{personId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
