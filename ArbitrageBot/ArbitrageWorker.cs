using ArbitrageBot.Configuration;
using ArbitrageBot.Hubs;
using ArbitrageBot.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace ArbitrageBot;

public class ArbitrageWorker : BackgroundService
{
    private readonly IMarketDataService _spotMarket;
    private readonly IOrderBookService _spotBooks;
    private readonly IPaperExecutionService _spotPaper;
    private readonly IFuturesMarketService _futMarket;
    private readonly IFuturesPaperService _futPaper;
    private readonly ISymbolDiscoveryService _discovery;
    private readonly ActiveMarketContext _markets;
    private readonly ArbitrageOptions _options;
    private readonly ArbitrageState _state;
    private readonly IHubContext<ArbitrageHub> _hub;
    private readonly ILogger<ArbitrageWorker> _logger;
    private readonly IPaperAnalyticsStore _analytics;
    private DateTime _lastSymbolRefreshUtc = DateTime.UtcNow;

    public ArbitrageWorker(
        IMarketDataService spotMarket,
        IOrderBookService spotBooks,
        IPaperExecutionService spotPaper,
        IFuturesMarketService futMarket,
        IFuturesPaperService futPaper,
        ISymbolDiscoveryService discovery,
        ActiveMarketContext markets,
        IOptions<ArbitrageOptions> options,
        ArbitrageState state,
        IHubContext<ArbitrageHub> hub,
        ILogger<ArbitrageWorker> logger,
        IPaperAnalyticsStore analytics)
    {
        _spotMarket = spotMarket;
        _spotBooks = spotBooks;
        _spotPaper = spotPaper;
        _futMarket = futMarket;
        _futPaper = futPaper;
        _discovery = discovery;
        _markets = markets;
        _options = options.Value;
        _state = state;
        _hub = hub;
        _logger = logger;
        _analytics = analytics;

        _state.StrategyMode = _options.StrategyMode;
        _state.Mode = _options.PaperTrading ? "PAPER" : "LIVE";
        _state.MinProfitPercent = _options.MinProfitPercent;
        _state.QuoteSize = _options.QuoteSize;
        _state.DynamicSymbols = _options.DynamicSymbols;
        _state.StrategyNote = _options.IsFuturesCross
            ? "FuturesCross (realistic paper): LONG cheap perp + SHORT rich perp. Round-trip fees + funding. Majors BTC/ETH/BNB excluded — mid-alts only. Margin only, no transfers."
            : "Spot inventory arb: buy cheap / sell rich with pre-funded balances.";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _markets.SetExchanges(_options.NormalizedExchanges);
        _state.Exchanges = _markets.Exchanges;
        await RefreshSymbolsAsync(stoppingToken);
        _lastSymbolRefreshUtc = DateTime.UtcNow;

        _logger.LogInformation(
            "Start | Strategy={Strat} | Mode={Mode} | Symbols={Sym} | Ex={Ex}",
            _options.StrategyMode, _state.Mode,
            string.Join(",", _markets.Symbols), string.Join(",", _markets.Exchanges));

        if (_options.IsFuturesCross)
        {
            _futPaper.Initialize(_markets.Exchanges);
            await _futMarket.StartAsync(stoppingToken);
            _logger.LogInformation("Futures books: {S}",
                string.Join("; ", _futMarket.ConnectionStatus.Select(kv => $"{kv.Key}={kv.Value}")));
        }
        else
        {
            _spotPaper.Initialize(_markets.Exchanges, _markets.Symbols);
            await _spotBooks.StartAsync(stoppingToken);
        }

        await Task.Delay(2500, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Periodic symbol universe refresh (config DynamicRefreshMinutes)
                var refreshMin = _options.DynamicRefreshMinutes > 0 ? _options.DynamicRefreshMinutes : 0;
                if (_options.DynamicSymbols && refreshMin > 0 &&
                    (DateTime.UtcNow - _lastSymbolRefreshUtc).TotalMinutes >= refreshMin)
                {
                    await TryRefreshUniverseAsync(stoppingToken);
                }

                if (_state.IsPaused)
                {
                    PushSnapshotExtras();
                    await _hub.Clients.All.SendAsync("Snapshot", _state.GetSnapshot(), stoppingToken);
                    await Task.Delay(_options.ScanIntervalMs, stoppingToken);
                    continue;
                }

                if (_options.IsFuturesCross)
                    await RunFuturesCycleAsync(stoppingToken);
                else
                    await RunSpotCycleAsync(stoppingToken);

                await _hub.Clients.All.SendAsync("Snapshot", _state.GetSnapshot(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cycle error");
                _state.SetError(ex.Message);
                await _hub.Clients.All.SendAsync("Snapshot", _state.GetSnapshot(), stoppingToken);
            }

            await Task.Delay(_options.ScanIntervalMs, stoppingToken);
        }

        if (_options.IsFuturesCross) await _futMarket.StopAsync(stoppingToken);
        else await _spotBooks.StopAsync(stoppingToken);
    }

    private async Task RunFuturesCycleAsync(CancellationToken ct)
    {
        var opps = await _futMarket.ScanAsync(ct);

        var tickersBySymbol = new Dictionary<string, Dictionary<string, BookTicker>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _markets.Symbols)
        {
            var t = _futMarket.GetBookTickers(s);
            if (t.Count > 0) tickersBySymbol[s] = t;
        }

        // Map futures opps into existing opportunity list shape for UI reuse
        var mapped = opps.Select(o => new Models.ArbitrageOpportunity
        {
            Symbol = o.Symbol,
            BuyExchange = o.LongExchange,
            SellExchange = o.ShortExchange,
            BuyPriceTop = o.LongAskTop,
            SellPriceTop = o.ShortBidTop,
            BuyPriceVwap = o.LongAskVwap,
            SellPriceVwap = o.ShortBidVwap,
            QuoteSize = o.NotionalUsd,
            FillBaseQty = o.BaseQty,
            FullyFilled = o.FullyFilled,
            GrossSpreadTopPercent = o.GrossSpreadPercent,
            GrossSpreadVwapPercent = o.GrossSpreadPercent,
            BuyFeePercent = o.LongFeePercent,
            SellFeePercent = o.ShortFeePercent,
            NetProfitPercent = o.NetAfterFundingPercent,
            NetProfitQuote = o.EstNetPnlUsd,
            BuySlippagePercent = o.SlippagePercent / 2,
            SellSlippagePercent = o.SlippagePercent / 2
        }).ToList();

        _state.UpdateScan(mapped, tickersBySymbol);
        _state.SetConnectionStatus(_futMarket.ConnectionStatus);

        var depthMap = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var sym in _markets.Symbols.Take(10))
            depthMap[sym] = _futMarket.GetDepth(sym, 18);
        _state.OrderBookDepth = depthMap;

        // Close converged hedges first
        _futPaper.TryCloseConverged((symbol, longEx, shortEx) =>
        {
            var books = _futMarket.GetBookTickers(symbol);
            if (!books.TryGetValue(longEx, out var l) || !books.TryGetValue(shortEx, out var s))
                return null;
            // close long at bid, cover short at ask
            return (l.BestBid, s.BestAsk);
        }, _options.FuturesCloseBelowNetPercent);

        decimal? bestOpen = opps.Count > 0 ? opps.Max(x => x.NetSpreadPercent) : null;
        decimal? bestRt = opps.Count > 0 ? opps.Max(x => x.NetRoundTripPercent) : null;
        _analytics.RecordScan(opps.Count, bestOpen, bestRt, _options.MinProfitPercent);

        if (opps.Count > 0)
        {
            _logger.LogInformation("Futures scan: {N} candidate(s), best RT={Best:F3}% open={Open:F3}%",
                opps.Count, bestRt, bestOpen);
        }
        else
        {
            // No candidates above threshold — record diagnostic skip for visibility
            _analytics.RecordSkip(null, "no-candidates-above-min",
                $"minOpen={_options.MinProfitPercent:F3}% requireRT={_options.FuturesRequireRoundTripEdge}");
        }

        if (opps.Count > 0 && _options.PaperTrading && _options.PaperAutoExecute)
        {
            foreach (var o in opps.OrderByDescending(x => x.NetSpreadPercent))
            {
                var trade = _futPaper.TryOpen(o);
                if (trade is null) continue;
                if (trade.Status == "Open")
                {
                    _logger.LogInformation("Opened futures paper hedge: {T}", o.ToString());
                    break;
                }
                _logger.LogInformation("Paper skip {Sym}: {Msg} open={Open:F3}% RT={Rt:F3}%",
                    o.Symbol, trade.Message, o.NetSpreadPercent, o.NetRoundTripPercent);
            }
        }

        // Expose analytics on state for UI
        _state.PaperAnalytics = _analytics.GetLiveSummary();
        _state.PaperRecentSkips = _analytics.GetRecentSkips(25);

        PushFuturesPaper();
    }

    private async Task RunSpotCycleAsync(CancellationToken ct)
    {
        var opportunities = await _spotMarket.ScanOpportunitiesAsync(ct);
        var tickersBySymbol = new Dictionary<string, Dictionary<string, BookTicker>>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in _markets.Symbols)
        {
            var books = await _spotMarket.GetBookTickersAsync(symbol, ct);
            if (books.Count > 0) tickersBySymbol[symbol] = books;
        }
        _state.UpdateScan(opportunities, tickersBySymbol);
        _state.SetConnectionStatus(_spotBooks.ConnectionStatus);

        if (opportunities.Count > 0 && _options.PaperTrading && _options.PaperAutoExecute)
        {
            foreach (var opp in opportunities)
            {
                if (_options.PaperRequireFullFill && !opp.FullyFilled) continue;
                var trade = _spotPaper.TryExecute(opp);
                if (trade.Success) break;
            }
        }

        _state.UpdatePaper(
            _spotPaper.RealizedPnlQuote,
            _spotPaper.TradeCount,
            _spotPaper.SuccessCount,
            _spotPaper.GetRecentTrades(40),
            _spotPaper.GetBalances());
    }

    private void PushFuturesPaper()
    {
        _futPaper.UpdateMarkToMarket((symbol, longEx, shortEx) =>
        {
            var books = _futMarket.GetBookTickers(symbol);
            if (!books.TryGetValue(longEx, out var lb) || !books.TryGetValue(shortEx, out var sb))
                return null;
            return (lb.BestBid, sb.BestAsk);
        });

        var trades = _futPaper.GetTrades(40);
        var positions = _futPaper.GetOpenPositions();
        var margin = _futPaper.GetMarginBalances();
        _state.FuturesPaper = new
        {
            realizedPnl = _futPaper.RealizedPnlUsd,
            unrealizedPnl = _futPaper.UnrealizedHintUsd,
            openCount = _futPaper.OpenCount,
            tradeAttempts = _futPaper.TradeAttempts,
            leverage = _options.FuturesPaperLeverage,
            stopLossUsd = _options.FuturesStopLossUsd,
            dailyLossLimitUsd = _options.FuturesDailyLossLimitUsd,
            maxMarginUsagePercent = _options.FuturesMaxMarginUsagePercent,
            maxNotionalUsd = _options.FuturesMaxNotionalUsd,
            dailyRealizedPnlUsd = _futPaper.DailyRealizedPnlUsd,
            margin,
            maxHoldMinutes = _options.FuturesMaxHoldMinutes,
            closeBelowNetPercent = _options.FuturesCloseBelowNetPercent,
            positions = positions.Select(p => new
            {
                p.Symbol,
                p.LongExchange,
                p.ShortExchange,
                p.BaseQty,
                p.LongEntry,
                p.ShortEntry,
                unrealizedPnl = p.UnrealizedPnlUsd,
                currentWidthPercent = p.CurrentWidthPercent,
                entryWidthPercent = p.EntryWidthPercent,
                openedAt = p.OpenedAt,
                holdSeconds = (int)(DateTime.UtcNow - p.OpenedAt).TotalSeconds
            }).ToList(),
            trades = trades.Select(t => new
            {
                t.Id,
                t.Symbol,
                t.LongExchange,
                t.ShortExchange,
                t.BaseQty,
                t.LongEntry,
                t.ShortEntry,
                t.LongExit,
                t.ShortExit,
                t.OpenFeesUsd,
                t.CloseFeesUsd,
                t.RealizedPnlUsd,
                t.IsOpen,
                t.Status,
                t.Message,
                t.OpenedAt,
                t.ClosedAt
            }).ToList()
        };

        // Also mirror into paper panel fields for simpler UI
        _state.UpdatePaper(
            _futPaper.RealizedPnlUsd,
            _futPaper.TradeAttempts,
            trades.Count(t => t.Status.StartsWith("Closed") || t.Status == "Open"),
            [],
            margin.ToDictionary(
                kv => kv.Key,
                kv => new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["USDT"] = kv.Value },
                StringComparer.OrdinalIgnoreCase));
    }

    private void PushSnapshotExtras()
    {
        if (_options.IsFuturesCross) PushFuturesPaper();
    }

    private async Task RefreshSymbolsAsync(CancellationToken ct)
    {
        IReadOnlyList<DiscoveredSymbol> discovered;
        if (_options.DynamicSymbols)
        {
            var discResult = await _discovery.DiscoverAsync(_markets.Exchanges, ct);
            discovered = discResult.Symbols;
            _state.DiscoverySource = discResult.Source;
            _state.DiscoveryMessage = discResult.Message;
        }
        else
        {
            _state.DiscoverySource = "fixed";
            _state.DiscoveryMessage = "DynamicSymbols=false — список из конфига";
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
            symbols = ["DOGEUSDT", "ADAUSDT", "SUIUSDT", "NEARUSDT"];

        // Futures: prefer fewer highly liquid names
        if (_options.IsFuturesCross && symbols.Count > _options.DynamicTopN)
            symbols = symbols.Take(_options.DynamicTopN).ToList();

        _markets.SetSymbols(symbols, discovered);
        _state.Symbols = _markets.Symbols;
        _state.DiscoveredSymbols = discovered.Select(d => (object)new
        {
            d.Symbol,
            d.BaseAsset,
            d.QuoteAsset,
            medianQuoteVolume = d.MedianQuoteVolume,
            d.ExchangeCount
        }).ToList();
    }

    private async Task TryRefreshUniverseAsync(CancellationToken ct)
    {
        try
        {
            var before = _markets.Symbols.ToList();
            await RefreshSymbolsAsync(ct);
            _lastSymbolRefreshUtc = DateTime.UtcNow;
            var after = _markets.Symbols.ToList();

            var same = before.Count == after.Count &&
                       before.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                             .SequenceEqual(after.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

            _logger.LogInformation(
                "Symbol refresh ({Src}): [{Before}] → [{After}] | changed={Changed}",
                _state.DiscoverySource,
                string.Join(",", before),
                string.Join(",", after),
                !same);

            if (same)
                return;

            if (_options.IsFuturesCross)
            {
                _logger.LogInformation("Restarting futures books for new symbol set…");
                await _futMarket.StopAsync(ct);
                await _futMarket.StartAsync(ct);
            }
            else
            {
                _logger.LogInformation("Restarting spot books for new symbol set…");
                await _spotBooks.StopAsync(ct);
                await _spotBooks.StartAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Periodic symbol refresh failed — keeping previous set");
            _lastSymbolRefreshUtc = DateTime.UtcNow;
            _state.DiscoveryMessage = "refresh error: " + ex.Message;
        }
    }

}
