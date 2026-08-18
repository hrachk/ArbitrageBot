using ArbitrageBot.Models;

namespace ArbitrageBot.Services;

public interface IMarketDataService
{
    /// <summary>
    /// Get best bid/ask for a symbol across configured exchanges
    /// </summary>
    Task<Dictionary<string, BookTicker>> GetBookTickersAsync(string symbol, CancellationToken ct = default);

    /// <summary>
    /// Scan all configured symbols for arbitrage opportunities
    /// </summary>
    Task<IReadOnlyList<ArbitrageOpportunity>> ScanOpportunitiesAsync(CancellationToken ct = default);
}

public record BookTicker
{
    public required string Exchange { get; init; }
    public required string Symbol { get; init; }
    public decimal BestBid { get; init; }
    public decimal BestAsk { get; init; }
    public decimal BidQuantity { get; init; }
    public decimal AskQuantity { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
