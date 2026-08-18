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
    private readonly ISymbolDiscoveryService _discovery;
    private readonly ActiveMarketContext _markets;
    private readonly ArbitrageOptions _options;
    private readonly ArbitrageState _state;
    private readonly IHubContext<ArbitrageHub> _hub;
    private readonly ILogger<ArbitrageWorker> _logger;

    public ArbitrageWorker(
        IMarketDataService marketData,
        IOrderBookService orderBooks,
        IPaperExecutionService paper,
        ISymbolDiscoveryService discovery,
        ActiveMarketContext markets,
        IOptions<ArbitrageOptions> options,
        ArbitrageState state,
        IHubContext<ArbitrageHub> hub,
        ILogger<ArbitrageWorker> logger)
    {
        _marketData = marketData;
        _orderBooks = orderBooks;
        _paper = paper;
        _discovery = discovery;
        _markets = markets;
        _options = options.Value;
        _state = state;
        _hub = hub;
        _logger = logger;

        _state.Mode = _options.PaperTrading ? "PAPER" : "LIVE";
        _state.MinProfitPercent = _options.MinProfitPercent;
        _state.QuoteSize = _options.QuoteSize;
        _state.DynamicSymbols = _options.DynamicSymbols;
        _state.StrategyNote =
            "Inventory arbitrage: buy on cheap exchange + sell on expensive exchange using pre-funded balances. " +
            "No asset transfers between exchanges.";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _markets.SetExchanges(_options.NormalizedExchanges);
        _state.Exchanges = _markets.Exchanges;

        await RefreshSymbolsAsync(stoppingToken);

        _logger.LogInformation(
            "Worker start | Mode={Mode} | Dynamic={Dyn} | Symbols={Symbols} | Exchanges={Ex} | AutoPaper={Auto}",
            _state.Mode,
            _options.DynamicSymbols,
            string.Join(", ", _markets.Symbols),
            string.Join(", ", _markets.Exchanges),
            _options.PaperAutoExecute);

        // Paper inventory on each exchange (USDT + base) — no transfers needed
        _paper.Initialize(_markets.Exchanges, _markets.Symbols);
        PushPaperState();

        try
        {
            await _orderBooks.StartAsync(stoppingToken);
            _logger.LogInformation("Order books: {Status}",
                string.Join("; ", _orderBooks.ConnectionStatus.Select(kv => $"{kv.Key}={kv.Value}")));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Order books failed");
            _state.SetError("Order books failed: " + ex.Message);
        }

        await Task.Delay(2500, stoppingToken);
        var lastDiscover = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Optional periodic rediscovery (does not restart WS books in this version — restart app to apply new set)
                if (_options.DynamicSymbols && _options.DynamicRefreshMinutes > 0 &&
                    (DateTime.UtcNow - lastDiscover).TotalMinutes >= _options.DynamicRefreshMinutes)
                {
                    _logger.LogInformation("Periodic symbol refresh (metadata only until restart of books)");
                    await RefreshSymbolsAsync(stoppingToken);
                    lastDiscover = DateTime.UtcNow;
                }

                if (_state.IsPaused)
                {
                    PushPaperState();
                    await _hub.Clients.All.SendAsync("Snapshot", _state.GetSnapshot(), stoppingToken);
                    await Task.Delay(_options.ScanIntervalMs, stoppingToken);
                    continue;
                }

                var opportunities = await _marketData.ScanOpportunitiesAsync(stoppingToken);

                var tickersBySymbol = new Dictionary<string, Dictionary<string, BookTicker>>(StringComparer.OrdinalIgnoreCase);
                foreach (var symbol in _markets.Symbols)
                {
                    var books = await _marketData.GetBookTickersAsync(symbol, stoppingToken);
                    if (books.Count > 0)
                        tickersBySymbol[symbol] = books;
                }

                _state.UpdateScan(opportunities, tickersBySymbol);
                _state.SetConnectionStatus(_orderBooks.ConnectionStatus);

                if (opportunities.Count > 0)
                {
                    _logger.LogInformation("Opportunities: {Count}", opportunities.Count);
                    foreach (var opp in opportunities.Take(3))
                        _logger.LogInformation("  → {Opp}", opp.ToString());

                    if (_options.PaperTrading && _options.PaperAutoExecute)
                    {
                        foreach (var opp in opportunities)
                        {
                            if (_options.PaperRequireFullFill && !opp.FullyFilled)
                                continue;

                            var trade = _paper.TryExecute(opp);
                            if (trade.Success)
                            {
                                _logger.LogInformation(
                                    "PAPER inventory trade {Sym} {Buy}->{Sell} pnl={Pnl:F4}",
                                    trade.Symbol, trade.BuyExchange, trade.SellExchange, trade.NetPnlQuote);
                                break;
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
                _logger.LogError(ex, "Scan cycle error");
                _state.SetError(ex.Message);
                await _hub.Clients.All.SendAsync("Snapshot", _state.GetSnapshot(), stoppingToken);
            }

            await Task.Delay(_options.ScanIntervalMs, stoppingToken);
        }

        await _orderBooks.StopAsync(stoppingToken);
        _logger.LogInformation("Worker stopped");
    }

    private async Task RefreshSymbolsAsync(CancellationToken ct)
    {
        IReadOnlyList<DiscoveredSymbol> discovered;

        if (_options.DynamicSymbols)
        {
            _logger.LogInformation("Discovering liquid USDT pairs on {Ex}…",
                string.Join(", ", _markets.Exchanges));
            discovered = await _discovery.DiscoverAsync(_markets.Exchanges, ct);
        }
        else
        {
            discovered = _options.NormalizedSymbols.Select(s => new DiscoveredSymbol
            {
                Symbol = s,
                BaseAsset = s.EndsWith("USDT") ? s[..^4] : s,
                QuoteAsset = "USDT",
                MedianQuoteVolume = 0,
                ExchangeCount = _markets.Exchanges.Count,
                Exchanges = _markets.Exchanges.ToList()
            }).ToList();
        }

        var symbols = discovered.Select(d => d.Symbol).ToList();
        if (symbols.Count == 0)
            symbols = ["BTCUSDT", "ETHUSDT", "SOLUSDT"];

        _markets.SetSymbols(symbols, discovered);
        _state.Symbols = _markets.Symbols;
        _state.DiscoveredSymbols = discovered.Select(d => (object)new
        {
            d.Symbol,
            d.BaseAsset,
            d.QuoteAsset,
            medianQuoteVolume = d.MedianQuoteVolume,
            d.ExchangeCount,
            exchanges = d.Exchanges
        }).ToList();

        // Ensure paper base units exist for new assets
        foreach (var d in discovered)
        {
            if (!_options.PaperStartingBaseUnits.ContainsKey(d.BaseAsset))
            {
                // Heuristic starting inventory by typical price tier
                _options.PaperStartingBaseUnits[d.BaseAsset] =
                    d.BaseAsset is "BTC" ? 0.05m :
                    d.BaseAsset is "ETH" ? 0.5m :
                    d.BaseAsset is "SOL" ? 5m : 100m;
            }
        }

        _logger.LogInformation("Active symbols ({N}): {List}",
            symbols.Count, string.Join(", ", symbols));
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
