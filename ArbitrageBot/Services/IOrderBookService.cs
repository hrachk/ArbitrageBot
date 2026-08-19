using ArbitrageBot.Models;

namespace ArbitrageBot.Services;

public interface IOrderBookService
{
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);

    Dictionary<string, BookTicker> GetBookTickers(string symbol);

    IReadOnlyList<OrderBookLevelSnapshot> GetDepth(string symbol, string exchange, int levels = 10);

    /// <summary>
    /// Walk the ask side (buy) or bid side (sell) to fill approximately <paramref name="quoteAmount"/> quote.
    /// Returns VWAP, filled base qty, and whether the full size was available.
    /// </summary>
    FillEstimate EstimateFill(string symbol, string exchange, decimal quoteAmount, bool isBuy);

    bool IsReady { get; }
    IReadOnlyDictionary<string, string> ConnectionStatus { get; }
}

public record OrderBookLevelSnapshot(decimal Price, decimal Quantity);

public record FillEstimate
{
    public bool Success { get; init; }
    public decimal VwapPrice { get; init; }
    public decimal FilledBaseQty { get; init; }
    public decimal FilledQuoteQty { get; init; }
    public bool FullyFilled { get; init; }
    public decimal TopOfBookPrice { get; init; }
    public decimal SlippagePercent { get; init; }
    public string? Error { get; init; }

    public static FillEstimate Fail(string error) => new() { Success = false, Error = error };
}

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
