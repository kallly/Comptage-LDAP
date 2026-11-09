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
        do
        {
            try
            {
                var purged = repository.PurgeExpired();
                if (purged > 0) logger.LogInformation("{Count} incident(s) supprimé(s) définitivement de la corbeille", purged);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Échec de la purge de la corbeille");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
