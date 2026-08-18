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

    public IReadOnlyList<ArbitrageOpportunity> Opportunities { get; private set; } = [];
    public ConcurrentDictionary<string, ConcurrentDictionary<string, BookTicker>> BookTickers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public long ScanCount { get; private set; }
    public long OpportunitiesFoundTotal { get; private set; }
    public string? LastError { get; private set; }
    public IReadOnlyDictionary<string, string> ConnectionStatus { get; private set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
                scanCount = ScanCount,
                opportunitiesFoundTotal = OpportunitiesFoundTotal,
                lastError = LastError,
                connectionStatus = ConnectionStatus,
                dataSource = "websocket+depth",
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
                bookTickers = books
            };
        }
    }
}
