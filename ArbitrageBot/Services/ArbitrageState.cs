using System.Collections.Concurrent;
using ArbitrageBot.Models;

namespace ArbitrageBot.Services;

public class ArbitrageState
{
    private readonly object _lock = new();

    public DateTime LastScanUtc { get; private set; }
    public string Mode { get; set; } = "PAPER";
    public IReadOnlyList<string> Symbols { get; set; } = [];
    public IReadOnlyList<string> Exchanges { get; set; } = [];
    public decimal MinProfitPercent { get; set; }
    public decimal QuoteSize { get; set; }
    public bool IsPaused { get; set; }
    public string StrategyNote { get; set; } = "Futures cross: LONG cheap perp + SHORT rich perp (margin only, no transfers).";
    public string StrategyMode { get; set; } = "FuturesCross";
    public object? FuturesPaper { get; set; }
    public bool DynamicSymbols { get; set; }
    public IReadOnlyList<object> DiscoveredSymbols { get; set; } = [];

    public IReadOnlyList<ArbitrageOpportunity> Opportunities { get; private set; } = [];
    public ConcurrentDictionary<string, ConcurrentDictionary<string, BookTicker>> BookTickers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public long ScanCount { get; private set; }
    public long OpportunitiesFoundTotal { get; private set; }
    public string? LastError { get; private set; }
    public IReadOnlyDictionary<string, string> ConnectionStatus { get; private set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // Paper
    public decimal PaperRealizedPnl { get; private set; }
    public int PaperTradeCount { get; private set; }
    public int PaperSuccessCount { get; private set; }
    public IReadOnlyList<PaperTrade> PaperTrades { get; private set; } = [];
    public IReadOnlyDictionary<string, Dictionary<string, decimal>> PaperBalances { get; private set; } =
        new Dictionary<string, Dictionary<string, decimal>>(StringComparer.OrdinalIgnoreCase);
    public PaperTrade? LastPaperTrade { get; private set; }

    public void UpdateScan(IReadOnlyList<ArbitrageOpportunity> opportunities, Dictionary<string, Dictionary<string, BookTicker>>? tickersBySymbol = null)
    {
        lock (_lock)
        {
            Opportunities = opportunities;
            LastScanUtc = DateTime.UtcNow;
            ScanCount++;
            OpportunitiesFoundTotal += opportunities.Count;
            LastError = null;

            if (tickersBySymbol != null)
            {
                foreach (var (symbol, byEx) in tickersBySymbol)
                {
                    var dict = BookTickers.GetOrAdd(symbol, _ => new ConcurrentDictionary<string, BookTicker>(StringComparer.OrdinalIgnoreCase));
                    foreach (var (ex, ticker) in byEx)
                        dict[ex] = ticker;
                }
            }
        }
    }

    public void UpdatePaper(
        decimal realizedPnl,
        int tradeCount,
        int successCount,
        IReadOnlyList<PaperTrade> trades,
        IReadOnlyDictionary<string, Dictionary<string, decimal>> balances,
        PaperTrade? lastTrade = null)
    {
        lock (_lock)
        {
            PaperRealizedPnl = realizedPnl;
            PaperTradeCount = tradeCount;
            PaperSuccessCount = successCount;
            PaperTrades = trades;
            PaperBalances = balances;
            if (lastTrade != null) LastPaperTrade = lastTrade;
        }
    }

    public void SetConnectionStatus(IReadOnlyDictionary<string, string> status)
    {
        lock (_lock)
            ConnectionStatus = new Dictionary<string, string>(status, StringComparer.OrdinalIgnoreCase);
    }

    public void SetError(string message)
    {
        lock (_lock)
        {
            LastError = message;
            LastScanUtc = DateTime.UtcNow;
        }
    }

    public object GetSnapshot()
    {
        lock (_lock)
        {
            var books = BookTickers.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.ToDictionary(x => x.Key, x => new
                {
                    x.Value.BestBid,
                    x.Value.BestAsk,
                    x.Value.BidQuantity,
                    x.Value.AskQuantity,
                    x.Value.Timestamp
                }, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

            return new
            {
                lastScanUtc = LastScanUtc,
                mode = Mode,
                symbols = Symbols,
                exchanges = Exchanges,
                minProfitPercent = MinProfitPercent,
                quoteSize = QuoteSize,
                isPaused = IsPaused,
                strategyNote = StrategyNote,
                strategyMode = StrategyMode,
                futuresPaper = FuturesPaper,
                dynamicSymbols = DynamicSymbols,
                discoveredSymbols = DiscoveredSymbols,
                scanCount = ScanCount,
                opportunitiesFoundTotal = OpportunitiesFoundTotal,
                lastError = LastError,
                connectionStatus = ConnectionStatus,
                dataSource = "websocket+depth+paper",
                opportunities = Opportunities.Select(o => new
                {
                    o.Symbol,
                    o.BuyExchange,
                    o.SellExchange,
                    buyPriceTop = o.BuyPriceTop,
                    sellPriceTop = o.SellPriceTop,
                    buyPriceVwap = o.BuyPriceVwap,
                    sellPriceVwap = o.SellPriceVwap,
                    o.QuoteSize,
                    o.FillBaseQty,
                    o.FullyFilled,
                    grossSpreadTopPercent = o.GrossSpreadTopPercent,
                    grossSpreadVwapPercent = o.GrossSpreadVwapPercent,
                    o.BuyFeePercent,
                    o.SellFeePercent,
                    netProfitPercent = o.NetProfitPercent,
                    netProfitQuote = o.NetProfitQuote,
                    buySlippagePercent = o.BuySlippagePercent,
                    sellSlippagePercent = o.SellSlippagePercent,
                    detectedAt = o.DetectedAt
                }).ToList(),
                bookTickers = books,
                paper = new
                {
                    realizedPnl = PaperRealizedPnl,
                    tradeCount = PaperTradeCount,
                    successCount = PaperSuccessCount,
                    balances = PaperBalances,
                    trades = PaperTrades.Select(t => new
                    {
                        id = t.Id,
                        executedAt = t.ExecutedAt,
                        t.Symbol,
                        t.BuyExchange,
                        t.SellExchange,
                        t.BaseQty,
                        t.BuyVwap,
                        t.SellVwap,
                        t.NetPnlQuote,
                        t.NetPnlPercent,
                        t.Success,
                        t.Message,
                        t.QuoteSpent,
                        t.QuoteReceived
                    }).ToList(),
                    lastTrade = LastPaperTrade == null ? null : new
                    {
                        LastPaperTrade.Symbol,
                        LastPaperTrade.BuyExchange,
                        LastPaperTrade.SellExchange,
                        LastPaperTrade.NetPnlQuote,
                        LastPaperTrade.Success,
                        LastPaperTrade.Message,
                        LastPaperTrade.ExecutedAt
                    }
                }
            };
        }
    }
}
