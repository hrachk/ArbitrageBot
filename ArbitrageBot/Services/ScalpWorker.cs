namespace ArbitrageBot.Services;

/// <summary>
/// Runs the scalper on its own loop so its order round trips never delay the hedge scan.
/// </summary>
public sealed class ScalpWorker(ScalpEngine scalp, DensityScalper density, ILogger<ScalpWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the market service connect first.
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await scalp.TickAsync(stoppingToken);
                density.Tick();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scalp tick failed");
            }
            try { await Task.Delay(200, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}
