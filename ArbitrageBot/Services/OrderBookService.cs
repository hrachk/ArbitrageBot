using System.Collections.Concurrent;
using ArbitrageBot.Configuration;
using CryptoClients.Net.Interfaces;
using CryptoExchange.Net.Interfaces;
using CryptoExchange.Net.Objects.Sockets;
using CryptoExchange.Net.SharedApis;
using Microsoft.Extensions.Options;


namespace ArbitrageBot.Services;

/// <summary>
/// Maintains live WebSocket-backed order books via CryptoClients.Net factory.
/// Falls back to book-ticker WebSocket subscriptions if order-book factory is unavailable.
/// </summary>
public class OrderBookService : IOrderBookService, IAsyncDisposable
{
    private readonly IExchangeOrderBookFactory _orderBookFactory;
    private readonly IExchangeSocketClient _socketClient;
    private readonly ArbitrageOptions _options;
    private readonly ActiveMarketContext _markets;
    private readonly ILogger<OrderBookService> _logger;

    private readonly ConcurrentDictionary<string, ISymbolOrderBook> _books = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, BookTicker> _bookTickers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _status = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<UpdateSubscription> _subscriptions = [];
    private readonly object _subLock = new();

    private const int DepthLevels = 20;
    private bool _started;

    public bool IsReady => _started && (_books.Count > 0 || _bookTickers.Count > 0);
    public IReadOnlyDictionary<string, string> ConnectionStatus => _status;

    public OrderBookService(
        IExchangeOrderBookFactory orderBookFactory,
        IExchangeSocketClient socketClient,
        IOptions<ArbitrageOptions> options,
        ActiveMarketContext markets,
        ILogger<OrderBookService> logger)
    {
        _orderBookFactory = orderBookFactory;
        _socketClient = socketClient;
        _options = options.Value;
        _markets = markets;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_started) return;
        _started = true;

        foreach (var symbolStr in _markets.Symbols)
        {
            SharedSymbol symbol;
            try { symbol = ParseSymbol(symbolStr); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skip invalid symbol {Symbol}", symbolStr);
                continue;
            }

            foreach (var exchange in _markets.Exchanges)
            {
                var key = $"{exchange}:{symbolStr}";
                try
                {
                    var book = _orderBookFactory.Create(exchange, symbol, DepthLevels);
                    if (book == null)
                    {
                        _status[key] = "no-factory";
                        _logger.LogWarning("No order book factory for {Key}, trying book ticker WS", key);
                        await SubscribeBookTickerAsync(exchange, symbol, symbolStr, ct);
                        continue;
                    }

                    book.OnStatusChange += (_, newStatus) =>
                    {
                        _status[key] = newStatus.ToString();
                        _logger.LogDebug("OrderBook {Key} status -> {New}", key, newStatus);
                    };

                    book.OnOrderBookUpdate += _ => UpdateTickerFromBook(exchange, symbolStr, book);
                    book.OnBestOffersChanged += _ => UpdateTickerFromBook(exchange, symbolStr, book);

                    var start = await book.StartAsync(ct);
                    if (!start.Success)
                    {
                        _status[key] = $"failed: {start.Error?.Message}";
                        _logger.LogWarning("Failed to start order book {Key}: {Error}", key, start.Error?.Message);
                        
                        await SubscribeBookTickerAsync(exchange, symbol, symbolStr, ct);
                        continue;
                    }

                    _books[key] = book;
                    _status[key] = book.Status.ToString();
                    UpdateTickerFromBook(exchange, symbolStr, book);
                    _logger.LogInformation("Order book started: {Key} status={Status}", key, book.Status);
                }
                catch (Exception ex)
                {
                    _status[key] = $"error: {ex.Message}";
                    _logger.LogError(ex, "Error starting order book {Key}", key);
                    try { await SubscribeBookTickerAsync(exchange, symbol, symbolStr, ct); }
                    catch (Exception ex2) { _logger.LogError(ex2, "Book ticker fallback failed for {Key}", key); }
                }
            }
        }
    }

    private async Task SubscribeBookTickerAsync(string exchange, SharedSymbol symbol, string symbolStr, CancellationToken ct)
    {
        var key = $"{exchange}:{symbolStr}";
        try
        {
            var result = await _socketClient.SubscribeToBookTickerUpdatesAsync(
                exchange,
                new SubscribeBookTickerRequest(symbol),
                update =>
                {
                    var d = update.Data;
                    _bookTickers[key] = new BookTicker
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
                lock (_subLock) _subscriptions.Add(result.Data);
                _status[key] = "book-ticker-sub";
                _logger.LogInformation("Book ticker WS subscribed: {Key}", key);
            }
            else
            {
                _status[key] = $"ticker-fail: {result.Error?.Message}";
                _logger.LogWarning("Book ticker subscribe failed {Key}: {Error}", key, result.Error?.Message);
            }
        }
        catch (Exception ex)
        {
            _status[key] = $"ticker-error: {ex.Message}";
            _logger.LogError(ex, "Subscribe book ticker {Key}", key);
        }
    }

    private void UpdateTickerFromBook(string exchange, string symbolStr, ISymbolOrderBook book)
    {
        var bestBid = book.BestBid;
        var bestAsk = book.BestAsk;
        if (bestBid == null && bestAsk == null) return;

        var key = $"{exchange}:{symbolStr}";
        _bookTickers[key] = new BookTicker
        {
            Exchange = exchange,
            Symbol = symbolStr,
            BestBid = bestBid?.Price ?? 0,
            BestAsk = bestAsk?.Price ?? 0,
            BidQuantity = bestBid?.Quantity ?? 0,
            AskQuantity = bestAsk?.Quantity ?? 0,
            Timestamp = DateTime.UtcNow
        };
    }

    public Dictionary<string, BookTicker> GetBookTickers(string symbol)
    {
        var result = new Dictionary<string, BookTicker>(StringComparer.OrdinalIgnoreCase);
        foreach (var exchange in _markets.Exchanges)
        {
            var key = $"{exchange}:{symbol}";
            if (_bookTickers.TryGetValue(key, out var t))
                result[exchange] = t;
        }
        return result;
    }

    public IReadOnlyList<OrderBookLevelSnapshot> GetDepth(string symbol, string exchange, int levels = 10)
    {
        var key = $"{exchange}:{symbol}";
        if (!_books.TryGetValue(key, out var book))
            return [];

        return book.Bids?.Take(levels).Select(x => new OrderBookLevelSnapshot(x.Price, x.Quantity)).ToList()
               ?? [];
    }


    public FillEstimate EstimateFill(string symbol, string exchange, decimal quoteAmount, bool isBuy)
    {
        if (quoteAmount <= 0)
            return FillEstimate.Fail("quoteAmount must be > 0");

        var key = $"{exchange}:{symbol}";

        // Prefer full depth from local order book
        if (_books.TryGetValue(key, out var book))
        {
            var levels = isBuy
                ? book.Asks?.Select(x => (x.Price, x.Quantity)).ToList()
                : book.Bids?.Select(x => (x.Price, x.Quantity)).ToList();

            if (levels == null || levels.Count == 0)
                return FillEstimate.Fail("empty book");

            return WalkLevels(levels, quoteAmount, isBuy);
        }

        // Fallback: only top of book from book ticker
        if (_bookTickers.TryGetValue(key, out var t))
        {
            var price = isBuy ? t.BestAsk : t.BestBid;
            var qty = isBuy ? t.AskQuantity : t.BidQuantity;
            if (price <= 0 || qty <= 0)
                return FillEstimate.Fail("no top-of-book");

            return WalkLevels([(price, qty)], quoteAmount, isBuy);
        }

        return FillEstimate.Fail("no data");
    }

    private static FillEstimate WalkLevels(IReadOnlyList<(decimal Price, decimal Quantity)> levels, decimal quoteAmount, bool isBuy)
    {
        decimal remainingQuote = quoteAmount;
        decimal filledBase = 0;
        decimal filledQuote = 0;
        decimal top = levels[0].Price;

        foreach (var (price, qty) in levels)
        {
            if (price <= 0 || qty <= 0) continue;
            if (remainingQuote <= 0) break;

            var levelQuote = price * qty;
            if (levelQuote <= remainingQuote)
            {
                filledBase += qty;
                filledQuote += levelQuote;
                remainingQuote -= levelQuote;
            }
            else
            {
                var takeBase = remainingQuote / price;
                filledBase += takeBase;
                filledQuote += remainingQuote;
                remainingQuote = 0;
            }
        }

        if (filledBase <= 0 || filledQuote <= 0)
            return FillEstimate.Fail("insufficient liquidity");

        var vwap = filledQuote / filledBase;
        var fully = remainingQuote <= quoteAmount * 0.001m; // 0.1% tolerance
        var slip = top > 0
            ? (isBuy ? (vwap - top) / top * 100m : (top - vwap) / top * 100m)
            : 0m;

        return new FillEstimate
        {
            Success = true,
            VwapPrice = vwap,
            FilledBaseQty = filledBase,
            FilledQuoteQty = filledQuote,
            FullyFilled = fully,
            TopOfBookPrice = top,
            SlippagePercent = slip < 0 ? 0 : slip
        };
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        foreach (var kv in _books)
        {
            try
            {
                await kv.Value.StopAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stop book {Key}", kv.Key);
            }
        }
        _books.Clear();

        List<UpdateSubscription> subs;
        lock (_subLock)
        {
            subs = _subscriptions.ToList();
            _subscriptions.Clear();
        }
        foreach (var s in subs)
        {
            try { await s.CloseAsync(); } catch { /* ignore */ }
        }

        try { await _socketClient.UnsubscribeAllAsync(); } catch { /* ignore */ }
        _started = false;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private static SharedSymbol ParseSymbol(string symbol)
    {
        symbol = symbol.ToUpperInvariant();
        if (symbol.EndsWith("USDT")) return new SharedSymbol(TradingMode.Spot, symbol[..^4], "USDT");
        if (symbol.EndsWith("USDC")) return new SharedSymbol(TradingMode.Spot, symbol[..^4], "USDC");
        if (symbol.EndsWith("BTC")) return new SharedSymbol(TradingMode.Spot, symbol[..^3], "BTC");
        throw new ArgumentException($"Unsupported symbol: {symbol}");
    }
}
