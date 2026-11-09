// =====================================================================================================
//  NameRulesTests.cs : liste blanche des caractères autorisés dans les noms saisis (Api/NameRules.cs)
// =====================================================================================================
//  Vérifie que les noms réels (accents, apostrophes, traits d'union, noms composés, langues européennes)
//  sont acceptés, que tout autre caractère est refusé, et que la règle est bien appliquée par l'API
//  (recherche dans l'annuaire et ajout d'un visiteur).
// =====================================================================================================

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ComptageIncident.Api;

namespace ComptageIncident.Tests;

public sealed class NameRulesTests
{
    [Theory]
    [InlineData("Dupont")]
    [InlineData("Jean-Pierre")]
    [InlineData("O'Brien")]
    [InlineData("D’Arc")]                     // apostrophe typographique (Word, smartphones, Mac)
    [InlineData("Émilie Lefèvre")]
    [InlineData("Zoë Noël")]
    [InlineData("Françoise Bœuf")]
    [InlineData("Œdipe Cœur")]
    [InlineData("Ÿvette")]
    [InlineData("Müller Groß")]
    [InlineData("Søren Ærø Åsa")]
    [InlineData("Łukasz Wójcik")]             // polonais
    [InlineData("Dvořák Čapek Šťastný")]      // tchèque
    [InlineData("Şükrü Ağaoğlu")]             // turc
    [InlineData("Ștefan Țepeș")]              // roumain
    [InlineData("Núñez García")]              // espagnol
    [InlineData("De La Fontaine")]
    [InlineData("dupo jea")]                  // recherche sur plusieurs mots
    public void Les_noms_reels_sont_acceptes(string value) =>
        Assert.True(NameRules.IsValid(NameRules.Normalize(value)), value);

    [Theory]
    [InlineData("dupo*")]
    [InlineData("du(pont)")]
    [InlineData("a\\b")]
    [InlineData("Dupont2")]
    [InlineData("<script>")]
    [InlineData("Robert'); --")]
    [InlineData("=HYPERLINK")]
    [InlineData("jean@exemple")]
    [InlineData("Paul\"Durand")]
    [InlineData("Paul;Durand")]
    [InlineData("Paul_Durand")]
    [InlineData("Paul\tDurand")]               // tabulation
    [InlineData("A×B")]                        // signe de multiplication, situé entre deux plages d'accents
    [InlineData("A÷B")]
    [InlineData("--- '' ---")]                 // aucune lettre
    [InlineData("")]
    public void Les_autres_caracteres_sont_refuses(string value) =>
        Assert.False(NameRules.IsValid(NameRules.Normalize(value)), value);

    [Fact]
    public void Un_accent_saisi_en_deux_caracteres_NFD_est_accepte()
    {
        var nfd = "Léa Lefèvre"; // « Léa Lefèvre » en forme décomposée (e + accent combinant)
        var normalized = NameRules.Normalize(nfd);
        Assert.Equal("Léa Lefèvre".Normalize(NormalizationForm.FormC), normalized);
        Assert.True(NameRules.IsValid(normalized));
    }
}

public sealed class NameRulesApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private HttpClient Client()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "ComptageIncident");
        return client;
    }

    [Theory]
    [InlineData("dupo*", HttpStatusCode.BadRequest)]
    [InlineData("dupo)(", HttpStatusCode.BadRequest)]
    [InlineData("1234", HttpStatusCode.BadRequest)]
    [InlineData("dupo", HttpStatusCode.OK)]
    [InlineData("Émil", HttpStatusCode.OK)]
    [InlineData("d'ar", HttpStatusCode.OK)]
    [InlineData("jean-p", HttpStatusCode.OK)]
    public async Task La_recherche_annuaire_applique_la_liste_blanche(string q, HttpStatusCode expected)
    {
        var response = await Client().GetAsync($"/api/directory/search?q={Uri.EscapeDataString(q)}");
        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("Paul O’Brien-Durand", HttpStatusCode.Created)]
    [InlineData("Éléonore D'Aubigné", HttpStatusCode.Created)]
    [InlineData("Paul Durand 2", HttpStatusCode.BadRequest)]
    [InlineData("<b>Paul</b>", HttpStatusCode.BadRequest)]
    public async Task Le_nom_du_visiteur_applique_la_liste_blanche(string fullName, HttpStatusCode expected)
    {
        var client = Client();
        var created = await client.PostAsJsonAsync("/api/incidents", new { title = "Visiteurs", type = "Exercice" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        var response = await client.PostAsJsonAsync($"/api/incidents/{id}/persons/visitor", new { fullName, company = "3M S.A." });

        Assert.Equal(expected, response.StatusCode);
    }
}
