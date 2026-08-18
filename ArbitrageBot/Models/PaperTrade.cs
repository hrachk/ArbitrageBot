namespace ArbitrageBot.Models;

public record PaperTrade
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTime ExecutedAt { get; init; } = DateTime.UtcNow;
    public required string Symbol { get; init; }
    public required string BuyExchange { get; init; }
    public required string SellExchange { get; init; }
    public decimal BaseQty { get; init; }
    public decimal BuyVwap { get; init; }
    public decimal SellVwap { get; init; }
    public decimal BuyFeeQuote { get; init; }
    public decimal SellFeeQuote { get; init; }
    public decimal QuoteSpent { get; init; }
    public decimal QuoteReceived { get; init; }
    public decimal NetPnlQuote { get; init; }
    public decimal NetPnlPercent { get; init; }
    public bool Success { get; init; }
    public string? Message { get; init; }
}

public class PaperBalancesSnapshot
{
    public required string Exchange { get; init; }
    public Dictionary<string, decimal> Assets { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
