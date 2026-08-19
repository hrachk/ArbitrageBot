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
    private readonly ILogger<FuturesMarketService> _logger;

    private readonly ConcurrentDictionary<string, ISymbolOrderBook> _books = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, BookTicker> _tickers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _status = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<UpdateSubscription> _subs = [];
    private readonly object _subLock = new();
    private bool _started;
    private readonly ConcurrentDictionary<string, (decimal rate, DateTime at)> _fundingCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan FundingCacheTtl = TimeSpan.FromMinutes(10);

    public IReadOnlyDictionary<string, string> ConnectionStatus => _status;
    public bool IsReady => _started && _tickers.Count > 0;

    public FuturesMarketService(
        IExchangeOrderBookFactory factory,
        IExchangeSocketClient socket,
        IExchangeRestClient rest,
        ActiveMarketContext markets,
        IOptions<ArbitrageOptions> options,
        ILogger<FuturesMarketService> logger)
    {
        _factory = factory;
        _socket = socket;
        _rest = rest;
        _markets = markets;
        _options = options.Value;
        _logger = logger;
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

            foreach (var exchange in _markets.Exchanges)
            {
                var key = $"{exchange}:{symbolStr}";
                try
                {
                    var depth = _options.MaxDepthLevels > 0 ? _options.MaxDepthLevels : 20;
                    // Try canonical name + common aliases (OKX vs Okx)
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
                        _logger.LogWarning("No futures order-book factory for {Key}, using book-ticker", key);
                        await SubscribeBookTickerAsync(exchange, symbol, symbolStr, ct);
                        continue;
                    }

                    book.OnStatusChange += (_, st) => _status[key] = st.ToString();
                    book.OnOrderBookUpdate += _ => UpdateFromBook(exchange, symbolStr, book);
                    book.OnBestOffersChanged += _ => UpdateFromBook(exchange, symbolStr, book);

                    var start = await book.StartAsync(ct);
                    if (!start.Success)
                    {
                        _status[key] = $"failed:{start.Error?.Message}";
                        _logger.LogWarning("Futures book {Key} failed: {E}", key, start.Error?.Message);
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
                    _status[key] = $"error:{ex.Message}";
                    _logger.LogError(ex, "Futures book {Key}", key);
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
                    var result = await _socket.SubscribeToBookTickerUpdatesAsync(
                        name,
                        new SubscribeBookTickerRequest(symbol),
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
        foreach (var ex in _markets.Exchanges)
        {
            var key = $"{ex}:{symbol}";
            if (_tickers.TryGetValue(key, out var t))
                result[ex] = t;
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
        var notional = _options.QuoteSize > 0 ? _options.QuoteSize : 500m;

        foreach (var symbol in _markets.Symbols)
        {
            if (ct.IsCancellationRequested) break;
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

                    var longFee = _options.EstimatedTakerFees.GetValueOrDefault(longEx, 0.05m);
                    var shortFee = _options.EstimatedTakerFees.GetValueOrDefault(shortEx, 0.05m);
                    var gross = (shortVwap - longVwap) / longVwap * 100m;
                    var netOpen = gross - longFee - shortFee;
                    // Round-trip: open long+short + close long+short (4 taker touches)
                    var netRt = gross - 2m * (longFee + shortFee);

                    decimal? frLong = GetCachedFunding(longEx, symbol);
                    decimal? frShort = GetCachedFunding(shortEx, symbol);
                    // Funding: positive rate => longs pay shorts. Hedge funding PnL % ≈ (FR_short - FR_long) * periods * 100
                    var periods = _options.FuturesFundingPeriods > 0 ? _options.FuturesFundingPeriods : 1;
                    decimal fundPct = 0;
                    if (_options.FuturesIncludeFunding && frLong is not null && frShort is not null)
                        fundPct = (frShort.Value - frLong.Value) * periods * 100m;

                    var netAfterFund = netRt + fundPct;
                    var thresholdMetric = _options.FuturesRequireRoundTripEdge
                        ? (_options.FuturesIncludeFunding ? netAfterFund : netRt)
                        : netOpen;

                    var estPnl = (shortVwap - longVwap) * qty
                        - longVwap * qty * (longFee / 100m) * 2m
                        - shortVwap * qty * (shortFee / 100m) * 2m
                        + notional * fundPct / 100m;

                    if (thresholdMetric < _options.MinProfitPercent) continue;

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
                        FullyFilled = buyFill.FullyFilled && sellFill.FullyFilled,
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

        return list.OrderByDescending(x => x.NetAfterFundingPercent).ToList();
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        foreach (var kv in _books)
        {
            try { await kv.Value.StopAsync(); } catch { /* ignore */ }
        }
        _books.Clear();
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
        var need = false;
        foreach (var symbol in _markets.Symbols)
        foreach (var ex in _markets.Exchanges)
        {
            var key = $"{ex}:{symbol}";
            if (!_fundingCache.TryGetValue(key, out var e) || DateTime.UtcNow - e.at >= FundingCacheTtl)
            { need = true; break; }
        }
        if (!need) return;

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
                    _markets.Exchanges,
                    ct);

                foreach (var r in results)
                {
                    if (!r.Success || r.Data == null || r.Data.Length == 0)
                        continue;
                    var last = r.Data.OrderByDescending(x => x.Timestamp).FirstOrDefault();
                    if (last == null) continue;
                    _fundingCache[$"{r.Exchange}:{symbolStr}"] = (last.FundingRate, DateTime.UtcNow);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Funding refresh failed for {S}", symbolStr);
            }
        }
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
