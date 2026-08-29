using System.Collections.Concurrent;
using ArbitrageBot.Configuration;
using ArbitrageBot.Models;
using CryptoClients.Net.Interfaces;
using CryptoExchange.Net.Interfaces;
using CryptoExchange.Net.Objects.Sockets;
using CryptoExchange.Net.SharedApis;
using Microsoft.Extensions.Options;

namespace ArbitrageBot.Services;

/// <summary>
/// Live USDT-M perpetual order books + cross-exchange spread scan (long cheap / short rich).
/// </summary>
public class FuturesMarketService : IFuturesMarketService, IAsyncDisposable
{
    private readonly IExchangeOrderBookFactory _factory;
    private readonly IExchangeSocketClient _socket;
    private readonly IExchangeRestClient _rest;
    private readonly ActiveMarketContext _markets;
    private readonly ArbitrageOptions _options;
    private readonly RuntimeRiskConfig _runtime;
    private readonly ILogger<FuturesMarketService> _logger;

    private readonly ConcurrentDictionary<string, ISymbolOrderBook> _books = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, BookTicker> _tickers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _status = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<UpdateSubscription> _subs = [];
    private readonly object _subLock = new();
    private bool _started;
    public decimal? LastScanBestGross { get; private set; }
    public decimal? LastScanBestNetOpen { get; private set; }
    public int LastScanBooksReady { get; private set; }
    public int LastScanPairsCompared { get; private set; }
    public int LastScanStaleSkipped { get; private set; }
    public int LastScanPersistPending { get; private set; }
    private readonly ConcurrentDictionary<string, (decimal gross, DateTime utc)> _grossHistory = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _edgeFirstSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (decimal rate, DateTime at)> _fundingCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan FundingCacheTtl = TimeSpan.FromMinutes(45);
    private DateTime _lastFundingRefreshUtc = DateTime.MinValue;
    private readonly SemaphoreSlim _fundingLock = new(1, 1);

    public IReadOnlyDictionary<string, string> ConnectionStatus => _status;
    public bool IsReady => _started && _tickers.Count > 0;

    public FuturesMarketService(
        IExchangeOrderBookFactory factory,
        IExchangeSocketClient socket,
        IExchangeRestClient rest,
        ActiveMarketContext markets,
        IOptions<ArbitrageOptions> options,
        ILogger<FuturesMarketService> logger,
        RuntimeRiskConfig runtime)
    {
        _factory = factory;
        _socket = socket;
        _rest = rest;
        _markets = markets;
        _options = options.Value;
        _logger = logger;
        _runtime = runtime;

        // Exchange-specific Shared API parameters for USDT-M perps
        ExchangeParameters.SetStaticParameter("Bitget", "ProductType", "UsdtFutures");
        ExchangeParameters.SetStaticParameter("BitGet", "ProductType", "UsdtFutures");
        ExchangeParameters.SetStaticParameter("GateIo", "SettleAsset", "usdt");
        ExchangeParameters.SetStaticParameter("GateIO", "SettleAsset", "usdt");
        ExchangeParameters.SetStaticParameter("Gate", "SettleAsset", "usdt");
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_started) return;
        _started = true;

        // Pre-register every exchange:symbol so UI always lists all configured venues
        foreach (var symbolStr in _markets.Symbols)
        foreach (var exchange in _markets.Exchanges)
            _status[$"{exchange}:{symbolStr}"] = "pending";

        foreach (var symbolStr in _markets.Symbols)
        {
            SharedSymbol symbol;
            try { symbol = ParsePerp(symbolStr); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skip symbol {S}", symbolStr);
                foreach (var exchange in _markets.Exchanges)
                    _status[$"{exchange}:{symbolStr}"] = "bad-symbol";
                continue;
            }

            // Only venues where discovery saw volume (avoids OKX 60018 on missing perps)
            var venues = _markets.ExchangesFor(symbolStr);
            if (venues.Count == 0) venues = _markets.Exchanges.ToList();

            foreach (var exchange in _markets.Exchanges)
            {
                var key = $"{exchange}:{symbolStr}";
                if (!venues.Any(v => v.Equals(exchange, StringComparison.OrdinalIgnoreCase)))
                {
                    _status[key] = "skip-not-listed";
                    continue;
                }

                try
                {
                    var depth = _options.MaxDepthLevels > 0 ? _options.MaxDepthLevels : 20;
                    ISymbolOrderBook? book = null;
                    foreach (var name in ExchangeNameVariants(exchange))
                    {
                        try
                        {
                            book = _factory.Create(name, symbol, depth);
                            if (book != null)
                            {
                                if (!string.Equals(name, exchange, StringComparison.OrdinalIgnoreCase))
                                    _logger.LogInformation("OrderBook factory matched alias {Alias} for {Ex}", name, exchange);
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Create book {Name}:{Sym} threw", name, symbolStr);
                        }
                    }

                    if (book == null)
                    {
                        _status[key] = "no-factory→ticker";
                        await SubscribeBookTickerAsync(exchange, symbol, symbolStr, ct);
                        continue;
                    }

                    book.OnStatusChange += (_, st) =>
                    {
                        var s = st.ToString();
                        _status[key] = s;
                        // Quiet missing-instrument spam after connect errors
                        if (IsMissingInstrument(s))
                            _status[key] = "skip-no-instrument";
                    };
                    book.OnOrderBookUpdate += _ => UpdateFromBook(exchange, symbolStr, book);
                    book.OnBestOffersChanged += _ => UpdateFromBook(exchange, symbolStr, book);

                    var start = await book.StartAsync(ct);
                    if (!start.Success)
                    {
                        var err = start.Error?.Message ?? "start failed";
                        if (IsMissingInstrument(err))
                        {
                            _status[key] = "skip-no-instrument";
                            _logger.LogDebug("Skip {Key}: instrument not on venue ({E})", key, err);
                            try { await book.StopAsync(); } catch { /* ignore */ }
                            continue; // do NOT ticker-subscribe — same 60018
                        }
                        _status[key] = "failed:" + err;
                        _logger.LogWarning("Futures book {Key} failed: {E}", key, err);
                        await SubscribeBookTickerAsync(exchange, symbol, symbolStr, ct);
                        continue;
                    }

                    _books[key] = book;
                    _status[key] = book.Status.ToString();
                    UpdateFromBook(exchange, symbolStr, book);
                    _logger.LogInformation("Futures book {Key} {Status}", key, book.Status);
                }
                catch (Exception ex)
                {
                    if (IsMissingInstrument(ex.Message))
                    {
                        _status[key] = "skip-no-instrument";
                        _logger.LogDebug(ex, "Skip {Key}: no instrument", key);
                        continue;
                    }
                    _status[key] = $"error:{ex.Message}";
                    _logger.LogWarning(ex, "Futures book {Key}", key);
                    try { await SubscribeBookTickerAsync(exchange, symbol, symbolStr, ct); } catch { /* ignore */ }
                }
            }
        }

        // Summary for logs / demo validation
        foreach (var exchange in _markets.Exchanges)
        {
            var keys = _status.Where(kv => kv.Key.StartsWith(exchange + ":", StringComparison.OrdinalIgnoreCase)).ToList();
            var live = keys.Count(kv =>
                kv.Value.Contains("Synced", StringComparison.OrdinalIgnoreCase) ||
                kv.Value.Contains("book-ticker", StringComparison.OrdinalIgnoreCase));
            _logger.LogInformation("Exchange {Ex}: {Live}/{Total} streams live", exchange, live, keys.Count);
        }
    }

    private static IEnumerable<string> ExchangeNameVariants(string exchange)
    {
        yield return exchange;
        if (exchange.Equals("OKX", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Okx";
            yield return "OKX";
        }
        if (exchange.Equals("GateIo", StringComparison.OrdinalIgnoreCase))
        {
            yield return "GateIO";
            yield return "Gate.io";
            yield return "Gate";
        }
        if (exchange.Equals("Bitget", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Bitget";
            yield return "BitGet";
        }
    }

    private async Task SubscribeBookTickerAsync(string exchange, SharedSymbol symbol, string symbolStr, CancellationToken ct)
    {
        var key = $"{exchange}:{symbolStr}";
        try
        {
            string? lastErr = null;
            UpdateSubscription? sub = null;
            foreach (var name in ExchangeNameVariants(exchange))
            {
                try
                {
                    var req = new SubscribeBookTickerRequest(symbol);
                    if (exchange.Equals("Bitget", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Bitget", StringComparison.OrdinalIgnoreCase))
                    {
                        req.ExchangeParameters = new ExchangeParameters(
                            new ExchangeParameter("Bitget", "ProductType", "UsdtFutures"));
                    }
                    else if (exchange.Equals("GateIo", StringComparison.OrdinalIgnoreCase) ||
                             name.Equals("GateIo", StringComparison.OrdinalIgnoreCase) ||
                             name.Equals("GateIO", StringComparison.OrdinalIgnoreCase))
                    {
                        req.ExchangeParameters = new ExchangeParameters(
                            new ExchangeParameter("GateIo", "SettleAsset", "usdt"));
                    }

                    var result = await _socket.SubscribeToBookTickerUpdatesAsync(
                        name,
                        req,
                        update =>
                        {
                            var d = update.Data;
                            _tickers[key] = new BookTicker
                            {
                                Exchange = exchange,
                                Symbol = symbolStr,
                                BestBid = d.BestBidPrice,
                                BestAsk = d.BestAskPrice,
                                BidQuantity = d.BestBidQuantity,
                                AskQuantity = d.BestAskQuantity,
                                Timestamp = DateTime.UtcNow
                            };
                            _status[key] = "book-ticker";
                        },
                        ct);
                    if (result.Success && result.Data != null)
                    {
                        sub = result.Data;
                        break;
                    }
                    lastErr = result.Error?.Message;
                }
                catch (Exception ex)
                {
                    lastErr = ex.Message;
                }
            }

            if (sub != null)
            {
                lock (_subLock) _subs.Add(sub);
                _status[key] = "book-ticker-sub";
            }
            else
                _status[key] = $"ticker-fail:{lastErr}";
        }
        catch (Exception ex)
        {
            _status[key] = $"ticker-error:{ex.Message}";
            _logger.LogError(ex, "Futures ticker {Key}", key);
        }
    }

    private void UpdateFromBook(string exchange, string symbolStr, ISymbolOrderBook book)
    {
        _tickers[$"{exchange}:{symbolStr}"] = new BookTicker
        {
            Exchange = exchange,
            Symbol = symbolStr,
            BestBid = book.BestBid?.Price ?? 0,
            BestAsk = book.BestAsk?.Price ?? 0,
            BidQuantity = book.BestBid?.Quantity ?? 0,
            AskQuantity = book.BestAsk?.Quantity ?? 0,
            Timestamp = DateTime.UtcNow
        };
    }

    public Dictionary<string, BookTicker> GetBookTickers(string symbol)
    {
        var result = new Dictionary<string, BookTicker>(StringComparer.OrdinalIgnoreCase);
        var maxAge = _runtime.Snapshot.MaxBookAgeMs;
        var now = DateTime.UtcNow;
        foreach (var ex in _markets.Exchanges)
        {
            var key = $"{ex}:{symbol}";
            if (!_tickers.TryGetValue(key, out var t)) continue;
            if (maxAge > 0 && t.Timestamp != default)
            {
                var ageMs = (now - t.Timestamp).TotalMilliseconds;
                if (ageMs > maxAge) continue; // stale
            }
            result[ex] = t;
        }
        return result;
    }

    public Dictionary<string, object> GetDepth(string symbol, int levels = 12)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        levels = Math.Clamp(levels, 1, 50);
        foreach (var ex in _markets.Exchanges)
        {
            var key = $"{ex}:{symbol}";
            if (!_books.TryGetValue(key, out var book))
            {
                if (_tickers.TryGetValue(key, out var t) && t.BestBid > 0)
                {
                    result[ex] = new
                    {
                        bids = new[] { new { price = t.BestBid, qty = t.BidQuantity } },
                        asks = new[] { new { price = t.BestAsk, qty = t.AskQuantity } },
                        source = "ticker"
                    };
                }
                continue;
            }
            var bids = book.Bids?.Take(levels).Select(x => new { price = x.Price, qty = x.Quantity }).ToList() ?? [];
            var asks = book.Asks?.Take(levels).Select(x => new { price = x.Price, qty = x.Quantity }).ToList() ?? [];
            result[ex] = new { bids, asks, source = "book" };
        }
        return result;
    }

    private FillEstimate Estimate(string symbol, string exchange, decimal quoteUsd, bool isBuy)
    {
        var key = $"{exchange}:{symbol}";
        if (_books.TryGetValue(key, out var book))
        {
            var levels = isBuy
                ? book.Asks?.Select(x => (x.Price, x.Quantity)).ToList()
                : book.Bids?.Select(x => (x.Price, x.Quantity)).ToList();
            if (levels is { Count: > 0 })
                return Walk(levels, quoteUsd, isBuy);
        }

        if (_tickers.TryGetValue(key, out var t))
        {
            var price = isBuy ? t.BestAsk : t.BestBid;
            var qty = isBuy ? t.AskQuantity : t.BidQuantity;
            if (price > 0 && qty > 0)
                return Walk([(price, qty)], quoteUsd, isBuy);
        }

        return FillEstimate.Fail("no data");
    }

    private static FillEstimate Walk(IReadOnlyList<(decimal Price, decimal Quantity)> levels, decimal quoteAmount, bool isBuy)
    {
        decimal remaining = quoteAmount, filledBase = 0, filledQuote = 0;
        var top = levels[0].Price;
        foreach (var (price, qty) in levels)
        {
            if (price <= 0 || qty <= 0 || remaining <= 0) continue;
            var levelQuote = price * qty;
            if (levelQuote <= remaining)
            {
                filledBase += qty;
                filledQuote += levelQuote;
                remaining -= levelQuote;
            }
            else
            {
                var take = remaining / price;
                filledBase += take;
                filledQuote += remaining;
                remaining = 0;
            }
        }
        if (filledBase <= 0 || filledQuote <= 0) return FillEstimate.Fail("thin");
        var vwap = filledQuote / filledBase;
        var slip = top > 0 ? (isBuy ? (vwap - top) / top * 100m : (top - vwap) / top * 100m) : 0m;
        return new FillEstimate
        {
            Success = true,
            VwapPrice = vwap,
            FilledBaseQty = filledBase,
            FilledQuoteQty = filledQuote,
            FullyFilled = remaining <= quoteAmount * 0.001m,
            TopOfBookPrice = top,
            SlippagePercent = slip < 0 ? 0 : slip
        };
    }

    public async Task<IReadOnlyList<FuturesOpportunity>> ScanAsync(CancellationToken ct = default)
    {
        if (_options.FuturesIncludeFunding)
            await RefreshFundingCacheAsync(ct);

        var list = new List<FuturesOpportunity>();
        var notional = _runtime.Snapshot.QuoteSize > 0 ? _runtime.Snapshot.QuoteSize : 500m;
        decimal? bestGross = null, bestNet = null;
        var booksReady = 0;
        var pairsCompared = 0;
        LastScanPersistPending = 0;
        LastScanStaleSkipped = 0;
        foreach (var s0 in _markets.Symbols)
            booksReady += GetBookTickers(s0).Count;

        foreach (var symbol in _markets.Symbols)
        {
            if (ct.IsCancellationRequested) break;
            if (_runtime.Snapshot.IsExcludedSymbol(symbol)) continue;
            var tickers = GetBookTickers(symbol);
            if (tickers.Count < 2) continue;

            var exchanges = tickers.Keys.ToList();
            for (var i = 0; i < exchanges.Count; i++)
            {
                for (var j = 0; j < exchanges.Count; j++)
                {
                    if (i == j) continue;
                    // Long on i (buy ask), Short on j (sell bid)
                    var longEx = exchanges[i];
                    var shortEx = exchanges[j];

                    var buyFill = Estimate(symbol, longEx, notional, isBuy: true);
                    var sellFill = Estimate(symbol, shortEx, notional, isBuy: false);
                    if (!buyFill.Success || !sellFill.Success) continue;

                    var qty = Math.Min(buyFill.FilledBaseQty, sellFill.FilledBaseQty);
                    if (qty <= 0) continue;

                    var longVwap = buyFill.VwapPrice;
                    var shortVwap = sellFill.VwapPrice;
                    if (shortVwap <= longVwap) continue;

                    pairsCompared++;
                    var longFee = _options.EstimatedTakerFees.GetValueOrDefault(longEx, 0.05m);
                    var shortFee = _options.EstimatedTakerFees.GetValueOrDefault(shortEx, 0.05m);
                    var gross = (shortVwap - longVwap) / longVwap * 100m;
                    var netOpen = gross - longFee - shortFee;
                    if (bestGross is null || gross > bestGross) bestGross = gross;
                    if (bestNet is null || netOpen > bestNet) bestNet = netOpen;
                    // Round-trip: open long+short + close long+short (4 taker touches)
                    var netRt = gross - 2m * (longFee + shortFee);

                    decimal? frLong = GetCachedFunding(longEx, symbol);
                    decimal? frShort = GetCachedFunding(shortEx, symbol);
                    // Funding: positive rate => longs pay shorts. Hedge funding PnL % ≈ (FR_short - FR_long) * periods * 100
                    var periods = _runtime.Snapshot.FuturesFundingPeriods > 0 ? _runtime.Snapshot.FuturesFundingPeriods : 1;
                    decimal fundPct = 0;
                    if (_options.FuturesIncludeFunding && frLong is not null && frShort is not null)
                        fundPct = (frShort.Value - frLong.Value) * periods * 100m;

                    var netAfterFund = netRt + fundPct;
                    var ro = _runtime.Snapshot;
                    var thresholdMetric = ro.FuturesRequireRoundTripEdge
                        ? (ro.FuturesIncludeFunding ? netAfterFund : netRt)
                        : netOpen;

                    var estPnl = (shortVwap - longVwap) * qty
                        - longVwap * qty * (longFee / 100m) * 2m
                        - shortVwap * qty * (shortFee / 100m) * 2m
                        + notional * fundPct / 100m;

                    // Rank ALL positive-gross routes for UI + bot (same list). Executable = passed gates.
                    if (gross <= 0) continue;

                    var snap = _runtime.Snapshot;
                    // QUALITY: always score on full round-trip (open+close fees).
                    // Net-open-only entries were the "kopeck then give back" loss factory.
                    var scalp = snap.SpatialScalpMode;
                    if (snap.FuturesRequireRoundTripEdge)
                        thresholdMetric = snap.FuturesIncludeFunding ? netAfterFund : netRt;
                    else if (scalp)
                        // Still require RT >= 0 so close fees are not ignored
                        thresholdMetric = Math.Min(netOpen, netRt);
                    else
                        thresholdMetric = netOpen;

                    var minEdge = snap.MinProfitPercent
                                  + (snap.OpenEdgeBufferPercent > 0 ? snap.OpenEdgeBufferPercent : 0m);
                    var minGross = snap.MinGrossSpreadPercent > 0 ? snap.MinGrossSpreadPercent : 0.25m;
                    // Hard floor: never EXEC if RT cannot cover close path
                    if (netRt < minEdge * 0.5m)
                        thresholdMetric = Math.Min(thresholdMetric, netRt);
                    var edgeKey = $"{symbol}|{longEx}|{shortEx}";

                    var spreadingOk = true;
                    if (snap.RequireSpreadingEdge)
                    {
                        var now = DateTime.UtcNow;
                        if (_grossHistory.TryGetValue(edgeKey, out var prev)
                            && (now - prev.utc).TotalMilliseconds is >= 400 and <= 3000
                            && gross + 0.015m < prev.gross)
                            spreadingOk = false;
                        _grossHistory[edgeKey] = (gross, now);
                    }

                    var persistMs = snap.MinSpreadPersistMs;
                    var first = _edgeFirstSeen.GetOrAdd(edgeKey, _ => DateTime.UtcNow);
                    var heldMs = (DateTime.UtcNow - first).TotalMilliseconds;
                    var persistOk = persistMs <= 0 || heldMs >= persistMs;
                    if (!persistOk)
                        LastScanPersistPending++;

                    var filled = buyFill.FullyFilled && sellFill.FullyFilled;
                    var needFill = snap.RequireDepthFullFill || snap.PaperRequireFullFill;
                    var fillOk = !needFill || filled;

                    // Executable only when gates pass (UI still shows ranked near-misses)
                    var executable = gross >= minGross
                                     && thresholdMetric >= minEdge
                                     && spreadingOk
                                     && persistOk
                                     && fillOk;

                    if (!executable && (gross < minGross * 0.5m || thresholdMetric < 0))
                    {
                        // too far from tradeable — keep ranking noise low
                        if (gross < 0.04m) continue;
                    }

                    list.Add(new FuturesOpportunity
                    {
                        Symbol = symbol,
                        LongExchange = longEx,
                        ShortExchange = shortEx,
                        LongAskVwap = longVwap,
                        ShortBidVwap = shortVwap,
                        LongAskTop = buyFill.TopOfBookPrice,
                        ShortBidTop = sellFill.TopOfBookPrice,
                        NotionalUsd = notional,
                        BaseQty = qty,
                        FullyFilled = filled,
                        IsExecutable = executable,
                        GrossSpreadPercent = gross,
                        NetSpreadPercent = netOpen,
                        NetRoundTripPercent = netRt,
                        NetAfterFundingPercent = netAfterFund,
                        EstNetPnlUsd = estPnl,
                        LongFeePercent = longFee,
                        ShortFeePercent = shortFee,
                        SlippagePercent = buyFill.SlippagePercent + sellFill.SlippagePercent,
                        LongFundingRate = frLong,
                        ShortFundingRate = frShort,
                        ExpectedFundingPercent = fundPct
                    });
                }
            }
        }

        var alive = new HashSet<string>(list.Select(o => $"{o.Symbol}|{o.LongExchange}|{o.ShortExchange}"), StringComparer.OrdinalIgnoreCase);
        foreach (var k in _edgeFirstSeen.Keys)
        {
            if (alive.Contains(k)) continue;
            if (_edgeFirstSeen.TryGetValue(k, out var t0) &&
                (DateTime.UtcNow - t0).TotalMilliseconds > Math.Max(5000, _runtime.Snapshot.MinSpreadPersistMs * 3))
                _edgeFirstSeen.TryRemove(k, out _);
        }

        LastScanBestGross = bestGross;
        LastScanBestNetOpen = bestNet;
        LastScanBooksReady = booksReady;
        LastScanPairsCompared = pairsCompared;

        // One ranked list for dashboard + execution (top by open-edge net)
        return list
            .OrderByDescending(x => x.IsExecutable)
            .ThenByDescending(x => x.NetSpreadPercent)
            .Take(20)
            .ToList();
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        foreach (var kv in _books)
        {
            try { await kv.Value.StopAsync(); } catch { /* ignore */ }
        }
        _books.Clear();
        _tickers.Clear();
        _status.Clear();
        List<UpdateSubscription> subs;
        lock (_subLock) { subs = _subs.ToList(); _subs.Clear(); }
        foreach (var s in subs)
            try { await s.CloseAsync(); } catch { /* ignore */ }
        try { await _socket.UnsubscribeAllAsync(); } catch { /* ignore */ }
        _started = false;
    }

    public async ValueTask DisposeAsync() => await StopAsync();


    private decimal? GetCachedFunding(string exchange, string symbol)
    {
        var key = $"{exchange}:{symbol}";
        if (_fundingCache.TryGetValue(key, out var e) && DateTime.UtcNow - e.at < FundingCacheTtl)
            return e.rate;
        return null;
    }

    private async Task RefreshFundingCacheAsync(CancellationToken ct)
    {
        // Global throttle: at most one full refresh per TTL window
        if (DateTime.UtcNow - _lastFundingRefreshUtc < FundingCacheTtl)
            return;

        if (!await _fundingLock.WaitAsync(0, ct))
            return; // another refresh in progress

        try
        {
            if (DateTime.UtcNow - _lastFundingRefreshUtc < FundingCacheTtl)
                return;

            // Prefer core venues for funding; Bitget/Gate often need extra params and burn rate limits
            var fundingExchanges = _markets.Exchanges
                .Where(e => e is not null &&
                    !e.Equals("Bitget", StringComparison.OrdinalIgnoreCase) &&
                    !e.Equals("GateIo", StringComparison.OrdinalIgnoreCase) &&
                    !e.Equals("GateIO", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (fundingExchanges.Count == 0)
                fundingExchanges = _markets.Exchanges.ToList();

            _logger.LogInformation("Funding cache refresh for {N} symbols × {E} exchanges (TTL {Ttl}m)",
                _markets.Symbols.Count, fundingExchanges.Count, FundingCacheTtl.TotalMinutes);

            foreach (var symbolStr in _markets.Symbols)
            {
                if (ct.IsCancellationRequested) break;
                SharedSymbol symbol;
                try { symbol = ParsePerp(symbolStr); }
                catch { continue; }

                try
                {
                    var results = await _rest.GetFundingRateHistoryAsync(
                        new GetFundingRateHistoryRequest(symbol),
                        fundingExchanges,
                        ct);

                    foreach (var r in results)
                    {
                        if (!r.Success || r.Data == null || r.Data.Length == 0)
                        {
                            if (!r.Success)
                                _logger.LogDebug("Funding {Ex}:{Sym} fail: {Err}", r.Exchange, symbolStr, r.Error?.Message);
                            continue;
                        }
                        var last = r.Data.OrderByDescending(x => x.Timestamp).FirstOrDefault();
                        if (last == null) continue;
                        _fundingCache[$"{r.Exchange}:{symbolStr}"] = (last.FundingRate, DateTime.UtcNow);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Funding refresh failed for {S}", symbolStr);
                }

                // Soft rate-limit guard between symbols (Binance /fapi/v1/fundingRate is tight)
                await Task.Delay(350, ct);
            }

            _lastFundingRefreshUtc = DateTime.UtcNow;
            _logger.LogInformation("Funding cache entries: {Count}", _fundingCache.Count);
        }
        finally
        {
            _fundingLock.Release();
        }
    }

    private static bool IsMissingInstrument(string? msg)
    {
        if (string.IsNullOrEmpty(msg)) return false;
        return msg.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("60018", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Invalid symbol", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Unknown symbol", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("-1121", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("instrument_id", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Wrong URL or channel", StringComparison.OrdinalIgnoreCase);
    }

    private static SharedSymbol ParsePerp(string symbol)
    {
        symbol = symbol.ToUpperInvariant();
        if (symbol.EndsWith("USDT"))
            return new SharedSymbol(TradingMode.PerpetualLinear, symbol[..^4], "USDT");
        if (symbol.EndsWith("USDC"))
            return new SharedSymbol(TradingMode.PerpetualLinear, symbol[..^4], "USDC");
        throw new ArgumentException($"Unsupported futures symbol {symbol}");
    }
}
