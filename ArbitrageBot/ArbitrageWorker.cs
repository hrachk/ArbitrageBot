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
    private readonly IPaperExecutionService _paper;
    private readonly ArbitrageOptions _options;
    private readonly ArbitrageState _state;
    private readonly IHubContext<ArbitrageHub> _hub;
    private readonly ILogger<ArbitrageWorker> _logger;

    public ArbitrageWorker(
        IMarketDataService marketData,
        IOrderBookService orderBooks,
        IPaperExecutionService paper,
        IOptions<ArbitrageOptions> options,
        ArbitrageState state,
        IHubContext<ArbitrageHub> hub,
        ILogger<ArbitrageWorker> logger)
    {
        _marketData = marketData;
        _orderBooks = orderBooks;
        _paper = paper;
        _options = options.Value;
        _state = state;
        _hub = hub;
        _logger = logger;

        _state.Mode = _options.PaperTrading ? "PAPER" : "LIVE";
        _state.Symbols = _options.NormalizedSymbols;
        _state.Exchanges = _options.NormalizedExchanges;
        _state.MinProfitPercent = _options.MinProfitPercent;
        _state.QuoteSize = _options.QuoteSize;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "ArbitrageWorker starting. Mode: {Mode} | Symbols: {Symbols} | Exchanges: {Exchanges} | PaperAuto={Auto}",
            _state.Mode,
            string.Join(", ", _options.NormalizedSymbols),
            string.Join(", ", _options.NormalizedExchanges),
            _options.PaperAutoExecute);

        _paper.Initialize(_options.NormalizedExchanges, _options.NormalizedSymbols);
        PushPaperState();

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

        await Task.Delay(2500, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_state.IsPaused)
                {
                    PushPaperState();
                    await _hub.Clients.All.SendAsync("Snapshot", _state.GetSnapshot(), stoppingToken);
                    await Task.Delay(_options.ScanIntervalMs, stoppingToken);
                    continue;
                }

                var opportunities = await _marketData.ScanOpportunitiesAsync(stoppingToken);

                var tickersBySymbol = new Dictionary<string, Dictionary<string, BookTicker>>(StringComparer.OrdinalIgnoreCase);
                foreach (var symbol in _options.NormalizedSymbols)
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
                    foreach (var opp in opportunities.Take(3))
                        _logger.LogInformation("  → {Opportunity}", opp.ToString());

                    if (_options.PaperTrading && _options.PaperAutoExecute)
                    {
                        // Best opportunity first
                        foreach (var opp in opportunities)
                        {
                            if (_options.PaperRequireFullFill && !opp.FullyFilled)
                                continue;

                            var trade = _paper.TryExecute(opp);
                            if (trade.Success)
                            {
                                _logger.LogInformation("Paper trade OK: {Msg}", trade.ToString());
                                break; // one trade per scan cycle
                            }
                        }
                    }
                }

                PushPaperState();
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

    private void PushPaperState()
    {
        _state.UpdatePaper(
            _paper.RealizedPnlQuote,
            _paper.TradeCount,
            _paper.SuccessCount,
            _paper.GetRecentTrades(40),
            _paper.GetBalances());
    }
}
