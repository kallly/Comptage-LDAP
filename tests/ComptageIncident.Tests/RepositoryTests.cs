// =====================================================================================================
//  RepositoryTests.cs : tests des règles métier (IncidentRepository) sur une vraie base SQLite temporaire
// =====================================================================================================
//  Vérifie les règles R1 à R5 décrites en tête de Data/IncidentRepository.cs : corbeille de 24 h (avec une
//  horloge simulée pour « avancer le temps »), purge, doublons, clôture, réintégration, journal d'audit.
// =====================================================================================================

using ComptageIncident.Data;
using ComptageIncident.Options;

namespace ComptageIncident.Tests;

public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan delta) => Now += delta;
}

public sealed class RepositoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "comptage-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero));
    private readonly IncidentRepository _repo;

    public RepositoryTests()
    {
        var db = new Database(Path.Combine(_dir, "test.db"));
        _repo = new IncidentRepository(db, _clock, Microsoft.Extensions.Options.Options.Create(new TrashOptions { RetentionHours = 24 }));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { System.IO.Directory.Delete(_dir, true); } catch { /* ignoré */ }
    }

    private Incident NewIncident() => _repo.CreateIncident("Incendie bâtiment B", "Incendie", "Parking", "DOM\\agent");

    [Fact]
    public void Ajout_interne_en_double_est_refuse_meme_avec_une_casse_differente()
    {
        var i = NewIncident();
        Assert.Equal(OpStatus.Ok, _repo.AddInternal(i.Id, new("jdupont", "Jean", "Dupont", null, "Prod"), "u").Status);

        var again = _repo.AddInternal(i.Id, new("JDUPONT", "Jean", "Dupont", null, null), "u");

        Assert.Equal(OpStatus.Conflict, again.Status);
        Assert.Equal(1, _repo.GetIncident(i.Id)!.InternalCount);
    }

    [Fact]
    public void Une_personne_retiree_peut_etre_reintegree_et_rajoutee()
    {
        var i = NewIncident();
        var p = _repo.AddInternal(i.Id, new("jdupont", "Jean", "Dupont", null, null), "u").Value!;
        var v = _repo.AddVisitor(i.Id, new("  Paul   Durand ", "ACME"), "u").Value!;
        Assert.Equal("Paul Durand", v.DisplayName);

        Assert.Equal(OpStatus.Ok, _repo.RemovePerson(i.Id, p.Id, "u").Status);
        Assert.Equal(0, _repo.GetIncident(i.Id)!.InternalCount);
        Assert.Equal(OpStatus.NotFound, _repo.RemovePerson(i.Id, p.Id, "u").Status);

        Assert.Equal(OpStatus.Ok, _repo.RestorePerson(i.Id, p.Id, "u").Status);
        Assert.Equal(1, _repo.GetIncident(i.Id)!.InternalCount);
        Assert.Equal(1, _repo.GetIncident(i.Id)!.VisitorCount);
    }

    [Fact]
    public void Reintegration_refusee_si_la_personne_a_ete_recomptee_entre_temps()
    {
        var i = NewIncident();
        var p = _repo.AddInternal(i.Id, new("jdupont", "Jean", "Dupont", null, null), "u").Value!;
        _repo.RemovePerson(i.Id, p.Id, "u");
        _repo.AddInternal(i.Id, new("jdupont", "Jean", "Dupont", null, null), "u");

        Assert.Equal(OpStatus.Conflict, _repo.RestorePerson(i.Id, p.Id, "u").Status);
    }

    [Fact]
    public void Incident_cloture_est_en_lecture_seule_jusqu_a_reouverture()
    {
        var i = NewIncident();
        Assert.Equal(OpStatus.Ok, _repo.CloseIncident(i.Id, "u").Status);

        Assert.Equal(OpStatus.Conflict, _repo.AddVisitor(i.Id, new("Paul Durand", null), "u").Status);
        Assert.Equal(OpStatus.Conflict, _repo.CloseIncident(i.Id, "u").Status);

        Assert.Equal(OpStatus.Ok, _repo.ReopenIncident(i.Id, "u").Status);
        Assert.Equal(OpStatus.Ok, _repo.AddVisitor(i.Id, new("Paul Durand", null), "u").Status);
    }

    [Fact]
    public void Incident_supprime_va_en_corbeille_et_se_restaure_pendant_24h()
    {
        var i = NewIncident();
        _repo.AddVisitor(i.Id, new("Paul Durand", null), "u");

        Assert.Equal(OpStatus.Ok, _repo.TrashIncident(i.Id, "u").Status);
        Assert.Null(_repo.GetIncident(i.Id));
        Assert.Empty(_repo.ListIncidents());
        Assert.Single(_repo.ListTrash());

        _clock.Advance(TimeSpan.FromHours(23));
        Assert.Equal(0, _repo.PurgeExpired());
        Assert.Equal(OpStatus.Ok, _repo.RestoreIncident(i.Id, "u").Status);

        var restored = _repo.GetIncident(i.Id)!;
        Assert.Equal(1, restored.VisitorCount);
        Assert.Empty(_repo.ListTrash());
    }

    [Fact]
    public void Apres_24h_la_restauration_est_refusee_et_la_purge_supprime_definitivement()
    {
        var i = NewIncident();
        var other = NewIncident();
        _repo.AddVisitor(i.Id, new("Paul Durand", null), "u");
        _repo.TrashIncident(i.Id, "u");

        _clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));
        Assert.Equal(OpStatus.Conflict, _repo.RestoreIncident(i.Id, "u").Status);

        Assert.Equal(1, _repo.PurgeExpired());
        Assert.Empty(_repo.ListTrash());
        Assert.Null(_repo.GetIncident(i.Id, includeDeleted: true));
        Assert.Empty(_repo.ListPersons(i.Id));
        Assert.NotNull(_repo.GetIncident(other.Id)); // les autres incidents ne sont pas touchés
    }

    [Fact]
    public void Chaque_action_est_journalisee()
    {
        var i = NewIncident();
        var p = _repo.AddInternal(i.Id, new("jdupont", "Jean", "Dupont", null, null), "DOM\\a").Value!;
        _repo.RemovePerson(i.Id, p.Id, "DOM\\b");

        var audit = _repo.ListAudit(i.Id);
        Assert.Equal(["Retrait interne", "Ajout interne", "Création incident"], audit.Select(a => a.Action));
        Assert.Equal("DOM\\b", audit[0].User);
    }
}
