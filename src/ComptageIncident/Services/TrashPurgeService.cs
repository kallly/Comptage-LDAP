// =====================================================================================================
//  Services/TrashPurgeService.cs : purge automatique de la corbeille
// =====================================================================================================
//  Tâche de fond démarrée avec l'application. Elle s'exécute une première fois au démarrage, puis toutes
//  les Trash:PurgeIntervalMinutes minutes (15 par défaut). À chaque passage, elle supprime définitivement
//  les incidents restés en corbeille plus de Trash:RetentionHours heures (24 par défaut).
//
//  C'est le SEUL code de l'application qui supprime physiquement des données (voir
//  IncidentRepository.PurgeExpired). Chaque suppression est tracée dans le journal d'audit.
//
//  Remarque IIS : un pool d'applications arrêté pour inactivité suspend aussi cette tâche. La purge est
//  alors simplement différée au prochain démarrage (aucune donnée n'est perdue ni supprimée trop tôt).
//  Install-IIS.ps1 désactive le délai d'inactivité du pool pour éviter ce cas.
// =====================================================================================================

using ComptageIncident.Data;
using ComptageIncident.Options;
using Microsoft.Extensions.Options;

namespace ComptageIncident.Services;

/// <summary>Supprime définitivement les incidents restés en corbeille au-delà du délai de rétention.</summary>
public sealed class TrashPurgeService(
    IncidentRepository repository,
    IOptions<TrashOptions> options,
    ILogger<TrashPurgeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.PurgeIntervalMinutes));
        using var timer = new PeriodicTimer(interval);
        // Boucle « faire… tant que » : un premier passage immédiat, puis un passage à chaque tic du minuteur,
        // jusqu'à l'arrêt de l'application (stoppingToken).
        do
        {
            try
            {
                var purged = repository.PurgeExpired();
                if (purged > 0) logger.LogInformation("{Count} incident(s) supprimé(s) définitivement de la corbeille", purged);
            }
            catch (Exception ex)
            {
                // Une erreur (base verrouillée, disque plein…) ne doit pas arrêter l'application :
                // elle est journalisée et la purge sera retentée au prochain passage.
                logger.LogError(ex, "Échec de la purge de la corbeille");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
