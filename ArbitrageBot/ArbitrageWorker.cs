using ArbitrageBot.Configuration;
using ArbitrageBot.Services;
using Microsoft.Extensions.Options;

namespace ArbitrageBot;

public class ArbitrageWorker : BackgroundService
{
    private readonly IMarketDataService _marketData;
    private readonly ArbitrageOptions _options;
    private readonly ILogger<ArbitrageWorker> _logger;

    public ArbitrageWorker(
        IMarketDataService marketData,
        IOptions<ArbitrageOptions> options,
        ILogger<ArbitrageWorker> logger)
    {
        _marketData = marketData;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "ArbitrageWorker started. Mode: {Mode} | Symbols: {Symbols} | Exchanges: {Exchanges} | MinProfit: {MinProfit}%",
            _options.PaperTrading ? "PAPER" : "LIVE",
            string.Join(", ", _options.Symbols),
            string.Join(", ", _options.Exchanges),
            _options.MinProfitPercent);

        // Small delay so host fully starts
        await Task.Delay(1500, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var opportunities = await _marketData.ScanOpportunitiesAsync(stoppingToken);

                if (opportunities.Count > 0)
                {
                    _logger.LogInformation("Found {Count} opportunity(ies):", opportunities.Count);
                    foreach (var opp in opportunities.Take(10))
                    {
                        _logger.LogInformation("  → {Opportunity}", opp.ToString());
                    }
                }
                else
                {
                    _logger.LogDebug("No opportunities above {MinProfit}% threshold", _options.MinProfitPercent);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during scan cycle");
            }

            await Task.Delay(_options.ScanIntervalMs, stoppingToken);
        }

        _logger.LogInformation("ArbitrageWorker stopped");
    }
}
