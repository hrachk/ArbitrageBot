using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
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
    private readonly ConcurrentDictionary<string, byte> _restSourced = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, decimal> _basePerContract = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private readonly CancellationTokenSource _restCts = new();
    private static readonly TimeSpan FundingCacheTtl = TimeSpan.FromMinutes(45);
    private DateTime _lastFundingRefreshUtc = DateTime.MinValue;
    private readonly SemaphoreSlim _fundingLock = new(1, 1);

    public IReadOnlyDictionary<string, string> ConnectionStatus => _status;
    public bool IsReady => _started && _tickers.Count > 0;

    private readonly FundingRateService? _fundingSvc;

    public FuturesMarketService(
        IExchangeOrderBookFactory factory,
        IExchangeSocketClient socket,
        IExchangeRestClient rest,
        ActiveMarketContext markets,
        IOptions<ArbitrageOptions> options,
        ILogger<FuturesMarketService> logger,
        RuntimeRiskConfig runtime,
        FundingRateService? fundingSvc = null)
    {
        _factory = factory;
        _socket = socket;
        _rest = rest;
        _markets = markets;
        _options = options.Value;
        _logger = logger;
        _runtime = runtime;
        _fundingSvc = fundingSvc;

        // Exchange-specific Shared API parameters for USDT-M perps
        ExchangeParameters.SetStaticParameter("Bitget", "ProductType", "UsdtFutures");
        ExchangeParameters.SetStaticParameter("BitGet", "ProductType", "UsdtFutures");
        ExchangeParameters.SetStaticParameter("Bitget", "MarginAsset", "USDT");
        ExchangeParameters.SetStaticParameter("Bitget", "marginCoin", "USDT");
        ExchangeParameters.SetStaticParameter("BitGet", "MarginAsset", "USDT");
        ExchangeParameters.SetStaticParameter("BitGet", "marginCoin", "USDT");
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

            // Try every configured venue. Missing instruments → skip-no-instrument (not discovery skip).
            // Discovery ExchangesFor is ranking-only; KuCoin often absent from HTTP vol maps.
            foreach (var exchange in _markets.Exchanges)
            {
                var key = $"{exchange}:{symbolStr}";
                try
                {
                    var depth = _options.MaxDepthLevels > 0 ? _options.MaxDepthLevels : 20;
                    ISymbolOrderBook? book = null;
                    foreach (var name in ExchangeNameVariants(exchange))
                    {
                        try
                        {
                            book = _factory.Create(name, symbol, depth, BookParams(name));
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

        // WS books on OKX/Bitget/Gate/KuCoin often never reach Synced (wrong product type,
        // unsupported socket name). REST depth still lets those venues enter the scan.
        _ = Task.Run(() => RestBooksLoop(_restCts.Token));
    }

    private static ExchangeParameters? BookParams(string exchange)
    {
        if (exchange.Equals("Bitget", StringComparison.OrdinalIgnoreCase)
            || exchange.Equals("BitGet", StringComparison.OrdinalIgnoreCase))
        {
            return new ExchangeParameters(
                new ExchangeParameter("Bitget", "ProductType", "UsdtFutures"),
                new ExchangeParameter("Bitget", "MarginAsset", "USDT"),
                new ExchangeParameter("Bitget", "marginCoin", "USDT"));
        }
        if (exchange.Equals("GateIo", StringComparison.OrdinalIgnoreCase)
            || exchange.Equals("GateIO", StringComparison.OrdinalIgnoreCase)
            || exchange.Equals("Gate", StringComparison.OrdinalIgnoreCase))
        {
            return new ExchangeParameters(new ExchangeParameter("GateIo", "SettleAsset", "usdt"));
        }
        return null;
    }

    private static string SocketName(string exchange)
    {
        if (exchange.Equals("OKX", StringComparison.OrdinalIgnoreCase)) return "OKX";
        if (exchange.Equals("Bitget", StringComparison.OrdinalIgnoreCase)) return "Bitget";
        if (exchange.Equals("GateIo", StringComparison.OrdinalIgnoreCase)
            || exchange.Equals("Gate", StringComparison.OrdinalIgnoreCase)) return "GateIo";
        if (exchange.Equals("Kucoin", StringComparison.OrdinalIgnoreCase)
            || exchange.Equals("KuCoin", StringComparison.OrdinalIgnoreCase)) return "Kucoin";
        if (exchange.Equals("Bybit", StringComparison.OrdinalIgnoreCase)) return "Bybit";
        return "Binance";
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
        if (exchange.Equals("Coinbase", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Coinbase";
            yield return "CoinbaseInternational";
            yield return "CoinbaseAdv";
        }
        if (exchange.Equals("Kucoin", StringComparison.OrdinalIgnoreCase)
            || exchange.Equals("KuCoin", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Kucoin";
            yield return "KuCoin";
            yield return "KucoinFutures";
        }
    }

    private async Task SubscribeBookTickerAsync(string exchange, SharedSymbol symbol, string symbolStr, CancellationToken ct)
    {
        var key = $"{exchange}:{symbolStr}";
        try
        {
            string? lastErr = null;
            UpdateSubscription? sub = null;
            foreach (var name in new[] { SocketName(exchange) })
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
        var key = $"{exchange}:{symbolStr}";
        _restSourced.TryRemove(key, out _);
        _tickers[key] = new BookTicker
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
                var limit = _restSourced.ContainsKey(key) ? Math.Max(maxAge, 8000) : maxAge;
                if (ageMs > limit) continue;
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
        if (_books.TryGetValue(key, out var book) && !_restSourced.ContainsKey(key))
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
        // Professional: one size for scan/paper/live. Cap by max notional so we never skip "180 > 100".
        var snapN = _runtime.Snapshot;
        var quote = snapN.QuoteSize > 0 ? snapN.QuoteSize : 100m;
        var maxN = snapN.FuturesMaxNotionalUsd > 0 ? snapN.FuturesMaxNotionalUsd : quote;
        var notional = Math.Min(quote, maxN);
        if (notional < 10m) notional = 10m;
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

                    var qtyRaw = Math.Min(buyFill.FilledBaseQty, sellFill.FilledBaseQty);
                    if (qtyRaw <= 0) continue;

                    var longVwap = buyFill.VwapPrice;
                    var shortVwap = sellFill.VwapPrice;
                    if (shortVwap <= longVwap) continue;

                    // Round qty to exchange-safe precision (prevents Binance -1111 on live; keeps paper realistic)
                    var qty = LiveOrderEngine.RoundBaseQty(qtyRaw, longVwap);
                    if (qty <= 0) continue;

                    pairsCompared++;
                    var snap = _runtime.Snapshot;
                    var longFee = ArbEconomics.TakerPercent(snap.EstimatedTakerFees, longEx);
                    var shortFee = ArbEconomics.TakerPercent(snap.EstimatedTakerFees, shortEx);
                    var gross = (shortVwap - longVwap) / longVwap * 100m;
                    var netOpen = gross - longFee - shortFee;
                    if (bestGross is null || gross > bestGross) bestGross = gross;
                    if (bestNet is null || netOpen > bestNet) bestNet = netOpen;
                    var feeRt = ArbEconomics.RoundTripFeePercent(longFee, shortFee);
                    var netRt = gross - feeRt;

                    decimal? frLong = GetCachedFunding(longEx, symbol);
                    decimal? frShort = GetCachedFunding(shortEx, symbol);
                    var periods = snap.FuturesFundingPeriods > 0 ? snap.FuturesFundingPeriods : 1;
                    decimal fundPct = 0;
                    if (snap.FuturesIncludeFunding && frLong is not null && frShort is not null)
                        fundPct = (frShort.Value - frLong.Value) * periods * 100m;

                    // Display only. Positive funding must not turn a losing price hedge into a buy.
                    var netAfterFund = netRt + fundPct;
                    var pricePnl = ArbEconomics.ExpectedConvergeUsd(longVwap, shortVwap, qty, longFee, shortFee);

                    if (gross <= 0) continue;

                    var buffer = snap.OpenEdgeBufferPercent > 0 ? snap.OpenEdgeBufferPercent : 0.04m;
                    var minEdge = snap.MinProfitPercent + buffer;
                    var feeFloor = feeRt + buffer;
                    var minGross = Math.Max(snap.MinGrossSpreadPercent > 0 ? snap.MinGrossSpreadPercent : feeFloor, feeFloor);
                    var minTp = snap.MinTakeProfitUsd > 0 ? snap.MinTakeProfitUsd : 0.50m;
                    // Next funding interval would cost more than the price edge can carry.
                    var fundingOk = fundPct >= -0.02m || fundPct >= -(netRt * 0.35m);
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

                    // Price round-trip only. Funding is not allowed to buy the signal.
                    var executable = gross >= minGross
                                     && netRt >= minEdge
                                     && pricePnl >= minTp
                                     && fundingOk
                                     && spreadingOk
                                     && persistOk
                                     && fillOk;

                    if (!executable && (gross < minGross * 0.5m || netRt < 0))
                    {
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
                        EstNetPnlUsd = pricePnl,
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
            .ThenByDescending(x => x.EstNetPnlUsd)
            .Take(20)
            .ToList();
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        try { _restCts.Cancel(); } catch { /* already disposed */ }
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
        // Prefer FundingRateService (5-min fresh, all exchanges)
        if (_fundingSvc != null)
        {
            var snap = _fundingSvc.GetLatest(symbol, exchange);
            if (snap != null) return snap.Rate;
        }
        // Fallback: own shared-API cache (45-min TTL)
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
            // Kucoin shared funding often 404000 (contract id) — skip REST funding poll noise
            var fundingExchanges = _markets.Exchanges
                .Where(e => e is not null &&
                    !e.Equals("Bitget", StringComparison.OrdinalIgnoreCase) &&
                    !e.Equals("GateIo", StringComparison.OrdinalIgnoreCase) &&
                    !e.Equals("GateIO", StringComparison.OrdinalIgnoreCase) &&
                    !e.Equals("Kucoin", StringComparison.OrdinalIgnoreCase) &&
                    !e.Equals("KuCoin", StringComparison.OrdinalIgnoreCase))
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

    private async Task RestBooksLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await PullMissingBooksAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogDebug(ex, "REST book fill"); }
            try { await Task.Delay(1200, ct); } catch { break; }
        }
    }

    private async Task PullMissingBooksAsync(CancellationToken ct)
    {
        var jobs = new List<Task>();
        using var gate = new SemaphoreSlim(8);
        foreach (var symbol in _markets.Symbols)
        {
            foreach (var exchange in _markets.Exchanges)
            {
                if (!NeedsRestBook(exchange, symbol)) continue;
                var sym = symbol;
                var ex = exchange;
                jobs.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync(ct);
                    try { await StoreRestBookAsync(ex, sym, ct); }
                    catch (OperationCanceledException) { }
                    catch (Exception exn) { _logger.LogDebug(exn, "REST book {Ex}:{Sym}", ex, sym); }
                    finally { gate.Release(); }
                }, ct));
            }
        }
        if (jobs.Count > 0)
            await Task.WhenAll(jobs);
    }

    private bool NeedsRestBook(string exchange, string symbol)
    {
        if (exchange.Equals("Binance", StringComparison.OrdinalIgnoreCase)
            || exchange.Equals("Bybit", StringComparison.OrdinalIgnoreCase))
            return false;
        var key = $"{exchange}:{symbol}";
        if (_status.TryGetValue(key, out var st)
            && st.Contains("Synced", StringComparison.OrdinalIgnoreCase)
            && !_restSourced.ContainsKey(key))
            return false;
        return true;
    }

    private async Task StoreRestBookAsync(string exchange, string symbol, CancellationToken ct)
    {
        var key = $"{exchange}:{symbol}";
        var book = await FetchRestBookAsync(exchange, symbol, ct);
        if (book == null) return;
        var (bid, ask) = book.Value;
        if (bid.Price <= 0 || ask.Price <= 0) return;
        _tickers[key] = new BookTicker
        {
            Exchange = exchange,
            Symbol = symbol,
            BestBid = bid.Price,
            BestAsk = ask.Price,
            BidQuantity = bid.Qty,
            AskQuantity = ask.Qty,
            Timestamp = DateTime.UtcNow
        };
        _restSourced[key] = 1;
        _status[key] = "rest-book";
    }

    private async Task<( (decimal Price, decimal Qty) Bid, (decimal Price, decimal Qty) Ask )?> FetchRestBookAsync(
        string exchange, string symbol, CancellationToken ct)
    {
        if (!symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)) return null;
        var baseAsset = symbol[..^4];
        string? url = null;
        if (exchange.Equals("OKX", StringComparison.OrdinalIgnoreCase))
            url = "https://www.okx.com/api/v5/market/books?instId=" + baseAsset + "-USDT-SWAP&sz=5";
        else if (exchange.Equals("Bitget", StringComparison.OrdinalIgnoreCase))
            url = "https://api.bitget.com/api/v2/mix/market/merge-depth?symbol=" + symbol + "&productType=USDT-FUTURES&limit=5";
        else if (exchange.Equals("GateIo", StringComparison.OrdinalIgnoreCase))
            url = "https://api.gateio.ws/api/v4/futures/usdt/order_book?contract=" + baseAsset + "_USDT&limit=5";
        else if (exchange.Equals("Kucoin", StringComparison.OrdinalIgnoreCase) || exchange.Equals("KuCoin", StringComparison.OrdinalIgnoreCase))
            url = "https://api-futures.kucoin.com/api/v1/level2/depth20?symbol=" + KucoinContract(baseAsset);
        else
            return null;

        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;
        var mult = await BasePerContractAsync(exchange, symbol, baseAsset, ct);

        if (exchange.Equals("OKX", StringComparison.OrdinalIgnoreCase))
        {
            if (!root.TryGetProperty("data", out var data) || data.GetArrayLength() == 0) return null;
            var row = data[0];
            return (Top(row, "bids", mult), Top(row, "asks", mult));
        }
        if (exchange.Equals("Bitget", StringComparison.OrdinalIgnoreCase))
        {
            if (!root.TryGetProperty("data", out var data)) return null;
            return (Top(data, "bids", 1m), Top(data, "asks", 1m));
        }
        if (exchange.Equals("GateIo", StringComparison.OrdinalIgnoreCase))
        {
            return (TopObj(root, "bids", mult), TopObj(root, "asks", mult));
        }
        if (!root.TryGetProperty("data", out var kdata)) return null;
        return (Top(kdata, "bids", mult), Top(kdata, "asks", mult));
    }

    private async Task<decimal> BasePerContractAsync(string exchange, string symbol, string baseAsset, CancellationToken ct)
    {
        if (exchange.Equals("Bitget", StringComparison.OrdinalIgnoreCase)) return 1m;
        var key = exchange + ":" + symbol;
        if (_basePerContract.TryGetValue(key, out var cached) && cached > 0) return cached;
        try
        {
            if (exchange.Equals("OKX", StringComparison.OrdinalIgnoreCase))
            {
                using var resp = await _http.GetAsync("https://www.okx.com/api/v5/public/instruments?instType=SWAP&instId=" + baseAsset + "-USDT-SWAP", ct);
                if (!resp.IsSuccessStatusCode) return 1m;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                var ctVal = ReadNum(doc.RootElement.GetProperty("data")[0].GetProperty("ctVal"));
                if (ctVal <= 0) ctVal = 1m;
                _basePerContract[key] = ctVal;
                return ctVal;
            }
            if (exchange.Equals("GateIo", StringComparison.OrdinalIgnoreCase))
            {
                using var resp = await _http.GetAsync("https://api.gateio.ws/api/v4/futures/usdt/contracts/" + baseAsset + "_USDT", ct);
                if (!resp.IsSuccessStatusCode) return 1m;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                var m = ReadNum(doc.RootElement.GetProperty("quanto_multiplier"));
                if (m <= 0) m = 1m;
                _basePerContract[key] = m;
                return m;
            }
            if (exchange.Equals("Kucoin", StringComparison.OrdinalIgnoreCase))
            {
                using var resp = await _http.GetAsync("https://api-futures.kucoin.com/api/v1/contracts/" + KucoinContract(baseAsset), ct);
                if (!resp.IsSuccessStatusCode) return 1m;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                var m = ReadNum(doc.RootElement.GetProperty("data").GetProperty("multiplier"));
                if (m <= 0) m = 1m;
                _basePerContract[key] = m;
                return m;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "contract size {Ex} {Sym}", exchange, symbol);
        }
        return 1m;
    }

    private static string KucoinContract(string baseAsset)
        => (baseAsset.Equals("BTC", StringComparison.OrdinalIgnoreCase) ? "XBT" : baseAsset) + "USDTM";

    private static (decimal Price, decimal Qty) Top(JsonElement parent, string side, decimal mult)
    {
        if (!parent.TryGetProperty(side, out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            return (0, 0);
        var row = arr[0];
        var px = ReadNum(row[0]);
        var qty = ReadNum(row[1]) * (mult > 0 ? mult : 1m);
        return (px, qty);
    }

    private static (decimal Price, decimal Qty) TopObj(JsonElement parent, string side, decimal mult)
    {
        if (!parent.TryGetProperty(side, out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            return (0, 0);
        var row = arr[0];
        var px = row.TryGetProperty("p", out var p) ? ReadNum(p) : 0m;
        var qty = (row.TryGetProperty("s", out var s) ? ReadNum(s) : 0m) * (mult > 0 ? mult : 1m);
        return (px, qty);
    }

    private static decimal ReadNum(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number) return el.GetDecimal();
        if (el.ValueKind == JsonValueKind.String &&
            decimal.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return d;
        return 0m;
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
