using ArbitrageBot.Models;

namespace ArbitrageBot.Services;

public interface IFuturesMarketService
{
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task<IReadOnlyList<FuturesOpportunity>> ScanAsync(CancellationToken ct = default);
    Dictionary<string, BookTicker> GetBookTickers(string symbol);
    IReadOnlyDictionary<string, string> ConnectionStatus { get; }
    bool IsReady { get; }
}

public interface IFuturesPaperService
{
    void Initialize(IEnumerable<string> exchanges);
    FuturesPaperTrade? TryOpen(FuturesOpportunity opp);
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
}
