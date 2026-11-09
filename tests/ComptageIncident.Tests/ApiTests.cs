// =====================================================================================================
//  ApiTests.cs : tests de l'API REST de bout en bout (application démarrée en mémoire)
// =====================================================================================================
//  L'application tourne en environnement Development avec l'utilisateur simulé TEST\agent et l'annuaire
//  fictif, sur une base SQLite temporaire. Chaque test envoie de vraies requêtes HTTP et vérifie les codes
//  de retour et les données (anti-CSRF, longueur minimale de recherche, doublons, corbeille, clôture…).
// =====================================================================================================

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ComptageIncident.Directory;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ComptageIncident.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public string DbDir { get; } = Path.Combine(Path.GetTempPath(), "comptage-api-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Database:Path", Path.Combine(DbDir, "api.db"));
        builder.UseSetting("Directory:Provider", "Fake");
        builder.UseSetting("Auth:DevUser", "TEST\\agent");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { System.IO.Directory.Delete(DbDir, true); } catch { /* ignoré */ }
    }
}

public sealed class ApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private HttpClient Client(bool csrf = true)
    {
        var client = factory.CreateClient();
        if (csrf) client.DefaultRequestHeaders.Add("X-Requested-With", "ComptageIncident");
        return client;
    }

    private static async Task<long> CreateIncident(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/incidents", new { title = "Exercice évacuation", type = "Exercice" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }

    [Fact]
    public async Task Requete_de_modification_sans_entete_csrf_est_refusee()
    {
        var response = await Client(csrf: false).PostAsJsonAsync("/api/incidents", new { title = "x", type = "Incendie" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Utilisateur_courant_est_renvoye()
    {
        var me = await Client().GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Equal("TEST\\agent", me.GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("dup", HttpStatusCode.BadRequest)]
    [InlineData("dupo", HttpStatusCode.OK)]
    public async Task Recherche_annuaire_exige_4_caracteres(string q, HttpStatusCode expected)
    {
        var response = await Client().GetAsync($"/api/directory/search?q={q}");
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Recherche_sur_nom_et_prenom_combines()
    {
        var results = await Client().GetFromJsonAsync<List<DirectoryPerson>>("/api/directory/search?q=dupo%20jea");
        Assert.NotNull(results);
        Assert.Contains(results, p => p.Login == "jdupont");
        Assert.DoesNotContain(results, p => p.Login == "mdupond");
    }

    [Fact]
    public async Task Scenario_complet_ajout_doublon_retrait_et_corbeille()
    {
        var client = Client();
        var id = await CreateIncident(client);

        var add = await client.PostAsJsonAsync($"/api/incidents/{id}/persons/internal",
            new { login = "jdupont", firstName = "Jean", lastName = "Dupont", department = "Production" });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        var dup = await client.PostAsJsonAsync($"/api/incidents/{id}/persons/internal", new { login = "jdupont" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        var visitor = await client.PostAsJsonAsync($"/api/incidents/{id}/persons/visitor", new { fullName = "Paul Durand", company = "ACME" });
        Assert.Equal(HttpStatusCode.Created, visitor.StatusCode);
        var visitorId = (await visitor.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        var empty = await client.PostAsJsonAsync($"/api/incidents/{id}/persons/visitor", new { fullName = " " });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/incidents/{id}/persons/{visitorId}")).StatusCode);

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/incidents/{id}");
        Assert.Equal(1, detail.GetProperty("incident").GetProperty("internalCount").GetInt32());
        Assert.Equal(0, detail.GetProperty("incident").GetProperty("visitorCount").GetInt32());
        Assert.Equal(1, detail.GetProperty("removed").GetArrayLength());

        var csv = await client.GetStringAsync($"/api/incidents/{id}/export.csv");
        Assert.Contains("Interne;Dupont;Jean", csv);

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/incidents/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/incidents/{id}")).StatusCode);

        var trash = await client.GetFromJsonAsync<JsonElement>("/api/trash");
        Assert.Contains(trash.EnumerateArray(), t => t.GetProperty("incident").GetProperty("id").GetInt64() == id);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/trash/{id}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/incidents/{id}")).StatusCode);
    }

    [Fact]
    public async Task Incident_cloture_refuse_les_ajouts()
    {
        var client = Client();
        var id = await CreateIncident(client);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/incidents/{id}/close", null)).StatusCode);

        var add = await client.PostAsJsonAsync($"/api/incidents/{id}/persons/visitor", new { fullName = "Paul Durand" });
        Assert.Equal(HttpStatusCode.Conflict, add.StatusCode);
    }

    [Fact]
    public async Task Type_incident_inconnu_est_refuse()
    {
        var response = await Client().PostAsJsonAsync("/api/incidents", new { title = "Test", type = "Inondation" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void Filtre_ldap_echappe_les_caracteres_speciaux()
    {
        var filter = DirectorySearch.BuildFilter("(objectClass=user)", "du*(pont)");
        Assert.Equal(@"(&(objectClass=user)(|(sn=du\2a\28pont\29*)(givenName=du\2a\28pont\29*)(displayName=du\2a\28pont\29*)(cn=du\2a\28pont\29*)))", filter);
    }
}
