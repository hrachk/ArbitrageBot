using ArbitrageBot.Configuration;
using ArbitrageBot.Hubs;
using ArbitrageBot.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace ArbitrageBot;

public class ArbitrageWorker : BackgroundService
{
    private readonly IMarketDataService _marketData;
    private readonly IOrderBookService _orderBooks;
    private readonly ArbitrageOptions _options;
    private readonly ArbitrageState _state;
    private readonly IHubContext<ArbitrageHub> _hub;
    private readonly ILogger<ArbitrageWorker> _logger;

    public ArbitrageWorker(
        IMarketDataService marketData,
        IOrderBookService orderBooks,
        IOptions<ArbitrageOptions> options,
        ArbitrageState state,
        IHubContext<ArbitrageHub> hub,
        ILogger<ArbitrageWorker> logger)
    {
        _marketData = marketData;
        _orderBooks = orderBooks;
        _options = options.Value;
        _state = state;
        _hub = hub;
        _logger = logger;

        _state.Mode = _options.PaperTrading ? "PAPER" : "LIVE";
        _state.Symbols = _options.Symbols;
        _state.Exchanges = _options.Exchanges;
        _state.MinProfitPercent = _options.MinProfitPercent;
        _state.QuoteSize = _options.QuoteSize;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "ArbitrageWorker starting. Mode: {Mode} | Symbols: {Symbols} | Exchanges: {Exchanges}",
            _state.Mode,
            string.Join(", ", _options.Symbols),
            string.Join(", ", _options.Exchanges));

        // Start WebSocket order books
        try
        {
            await _orderBooks.StartAsync(stoppingToken);
            _logger.LogInformation("Order books started. Status: {Status}",
                string.Join("; ", _orderBooks.ConnectionStatus.Select(kv => $"{kv.Key}={kv.Value}")));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start order books");
            _state.SetError("Order books failed: " + ex.Message);
        }

        // Wait a bit for first snapshots
        await Task.Delay(2500, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var opportunities = await _marketData.ScanOpportunitiesAsync(stoppingToken);

                var tickersBySymbol = new Dictionary<string, Dictionary<string, BookTicker>>(StringComparer.OrdinalIgnoreCase);
                foreach (var symbol in _options.Symbols)
                {
                    var books = await _marketData.GetBookTickersAsync(symbol, stoppingToken);
                    if (books.Count > 0)
                        tickersBySymbol[symbol] = books;
                }

                _state.UpdateScan(opportunities, tickersBySymbol);
                _state.SetConnectionStatus(_orderBooks.ConnectionStatus);

                if (opportunities.Count > 0)
                {
                    _logger.LogInformation("Found {Count} opportunity(ies)", opportunities.Count);
                    foreach (var opp in opportunities.Take(5))
                        _logger.LogInformation("  → {Opportunity}", opp.ToString());
                }

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

        await _orderBooks.StopAsync(stoppingToken);
        _logger.LogInformation("ArbitrageWorker stopped");
    }
}
