using ArbitrageBot.Models;

namespace ArbitrageBot.Services;

public interface IOrderBookService
{
    /// <summary>
    /// Start WebSocket order books for all configured symbols/exchanges.
    /// </summary>
    Task StartAsync(CancellationToken ct = default);

    Task StopAsync(CancellationToken ct = default);

    /// <summary>
    /// Snapshot of best bid/ask from live books (or last update).
    /// </summary>
    Dictionary<string, BookTicker> GetBookTickers(string symbol);

    /// <summary>
    /// Top-of-book levels for UI / slippage calc.
    /// </summary>
    IReadOnlyList<OrderBookLevelSnapshot> GetDepth(string symbol, string exchange, int levels = 10);

    bool IsReady { get; }
    IReadOnlyDictionary<string, string> ConnectionStatus { get; } // key: "Exchange:Symbol"
}

public record OrderBookLevelSnapshot(decimal Price, decimal Quantity);

public record LiveOrderBookSnapshot
{
    public required string Exchange { get; init; }
    public required string Symbol { get; init; }
    public decimal BestBid { get; init; }
    public decimal BestAsk { get; init; }
    public decimal BidQuantity { get; init; }
    public decimal AskQuantity { get; init; }
    public IReadOnlyList<OrderBookLevelSnapshot> Bids { get; init; } = [];
    public IReadOnlyList<OrderBookLevelSnapshot> Asks { get; init; } = [];
    public DateTime UpdatedAt { get; init; }
}
