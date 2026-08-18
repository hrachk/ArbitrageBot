using System.Collections.Concurrent;
using ArbitrageBot.Models;

namespace ArbitrageBot.Services;

/// <summary>
/// In-memory shared state that the Worker updates and the Web UI / SignalR reads.
/// </summary>
public class ArbitrageState
{
    private readonly object _lock = new();

    public DateTime LastScanUtc { get; private set; }
    public string Mode { get; set; } = "PAPER";
    public IReadOnlyList<string> Symbols { get; set; } = [];
    public IReadOnlyList<string> Exchanges { get; set; } = [];
    public decimal MinProfitPercent { get; set; }

    // Latest opportunities (sorted by net profit desc)
    public IReadOnlyList<ArbitrageOpportunity> Opportunities { get; private set; } = [];

    // Latest book tickers: Symbol -> Exchange -> BookTicker
    public ConcurrentDictionary<string, ConcurrentDictionary<string, BookTicker>> BookTickers { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Simple stats
    public long ScanCount { get; private set; }
    public long OpportunitiesFoundTotal { get; private set; }
    public string? LastError { get; private set; }

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
                scanCount = ScanCount,
                opportunitiesFoundTotal = OpportunitiesFoundTotal,
                lastError = LastError,
                opportunities = Opportunities.Select(o => new
                {
                    o.Symbol,
                    o.BuyExchange,
                    o.SellExchange,
                    o.BuyPrice,
                    o.SellPrice,
                    o.GrossSpreadPercent,
                    o.NetProfitPercent,
                    o.BuyFeePercent,
                    o.SellFeePercent,
                    detectedAt = o.DetectedAt
                }).ToList(),
                bookTickers = books
            };
        }
    }
}
