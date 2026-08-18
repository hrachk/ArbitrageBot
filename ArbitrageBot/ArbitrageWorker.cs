using ArbitrageBot.Configuration;
using ArbitrageBot.Hubs;
using ArbitrageBot.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace ArbitrageBot;

public class ArbitrageWorker : BackgroundService
{
    private readonly IMarketDataService _marketData;
    private readonly ArbitrageOptions _options;
    private readonly ArbitrageState _state;
    private readonly IHubContext<ArbitrageHub> _hub;
    private readonly ILogger<ArbitrageWorker> _logger;

    public ArbitrageWorker(
        IMarketDataService marketData,
        IOptions<ArbitrageOptions> options,
        ArbitrageState state,
        IHubContext<ArbitrageHub> hub,
        ILogger<ArbitrageWorker> logger)
    {
        _marketData = marketData;
        _options = options.Value;
        _state = state;
        _hub = hub;
        _logger = logger;

        _state.Mode = _options.PaperTrading ? "PAPER" : "LIVE";
        _state.Symbols = _options.Symbols;
        _state.Exchanges = _options.Exchanges;
        _state.MinProfitPercent = _options.MinProfitPercent;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "ArbitrageWorker started. Mode: {Mode} | Symbols: {Symbols} | Exchanges: {Exchanges} | MinProfit: {MinProfit}%",
            _state.Mode,
            string.Join(", ", _options.Symbols),
            string.Join(", ", _options.Exchanges),
            _options.MinProfitPercent);

        await Task.Delay(2000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var opportunities = await _marketData.ScanOpportunitiesAsync(stoppingToken);

                // Also collect latest tickers for UI
                var tickersBySymbol = new Dictionary<string, Dictionary<string, BookTicker>>(StringComparer.OrdinalIgnoreCase);
                foreach (var symbol in _options.Symbols)
                {
                    var books = await _marketData.GetBookTickersAsync(symbol, stoppingToken);
                    if (books.Count > 0)
                        tickersBySymbol[symbol] = books;
                }

                _state.UpdateScan(opportunities, tickersBySymbol);

                if (opportunities.Count > 0)
                {
                    _logger.LogInformation("Found {Count} opportunity(ies)", opportunities.Count);
                    foreach (var opp in opportunities.Take(5))
                        _logger.LogInformation("  → {Opportunity}", opp.ToString());
                }

                // Push live update to all connected browsers
                await _hub.Clients.All.SendAsync("Snapshot", _state.GetSnapshot(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during scan cycle");
                _state.SetError(ex.Message);
                await _hub.Clients.All.SendAsync("Snapshot", _state.GetSnapshot(), stoppingToken);
            }

            await Task.Delay(_options.ScanIntervalMs, stoppingToken);
        }

        _logger.LogInformation("ArbitrageWorker stopped");
    }
}
