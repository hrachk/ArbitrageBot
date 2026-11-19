using ArbitrageBot.Models;

namespace ArbitrageBot.Services;

public interface IFuturesMarketService
{
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task<IReadOnlyList<FuturesOpportunity>> ScanAsync(CancellationToken ct = default);
    Dictionary<string, BookTicker> GetBookTickers(string symbol);
    /// <summary>Top N bids/asks per exchange for a symbol (price, qty).</summary>
    Dictionary<string, object> GetDepth(string symbol, int levels = 12);
    IReadOnlyDictionary<string, string> ConnectionStatus { get; }
    bool IsReady { get; }
    /// <summary>True when the venue's quote comes from a live feed (not REST, not silent).</summary>
    bool QuoteLive(string exchange, string symbol);
    /// <summary>Fresh IOC entry prices for a fixed base quantity on one route.</summary>
    (decimal? LongAskVwap, decimal? ShortBidVwap, bool Fresh) QuoteEntry(string symbol, string longEx, string shortEx, decimal baseQty);
    /// <summary>Depth-walked exit marks (long sold at bids, short bought at asks) for a base quantity.</summary>
    (decimal longBid, decimal shortAsk)? ExitMarks(string symbol, string longEx, string shortEx, decimal baseQty);
}

public interface IFuturesPaperService
{
    void Initialize(IEnumerable<string> exchanges);
    FuturesPaperTrade? TryOpen(FuturesOpportunity opp);
    /// <summary>Book the cost of unwinding a single filled leg after the other IOC leg missed.</summary>
    void RecordLegMiss(FuturesOpportunity opp, string filledExchange, decimal costUsd);
    int TryCloseConverged(Func<string, string, string, (decimal longBid, decimal shortAsk)?> getMarks, decimal closeMinNetPercent);
    IReadOnlyList<FuturesPaperTrade> GetTrades(int take = 40);
    IReadOnlyList<FuturesPaperPosition> GetOpenPositions();
    IReadOnlyDictionary<string, decimal> GetMarginBalances();
    decimal RealizedPnlUsd { get; }
    decimal UnrealizedHintUsd { get; set; }
    decimal DailyRealizedPnlUsd { get; }
    void UpdateMarkToMarket(Func<string, string, string, (decimal longBid, decimal shortAsk)?> getMarks);
    int OpenCount { get; }
    int TradeAttempts { get; }
    void Reset(IEnumerable<string> exchanges);
    /// <summary>If no open positions, set each venue margin to current PaperStartingQuote (from Settings).</summary>
    bool ReseedBalancesIfIdle(IEnumerable<string>? exchanges = null);
    FuturesPaperTrade? ForceClose(Guid tradeId, Func<string, string, string, (decimal longBid, decimal shortAsk)?> getMarks, string reason = "manual");
    int PruneOrphanPositions(IReadOnlyCollection<string> activeSymbols);
    int ForceCloseAll();
    void PersistNow();
}
