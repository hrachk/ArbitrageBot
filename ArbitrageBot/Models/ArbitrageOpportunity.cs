namespace ArbitrageBot.Models;

public record ArbitrageOpportunity
{
    public required string Symbol { get; init; }
    public required string BuyExchange { get; init; }
    public required string SellExchange { get; init; }
    public decimal BuyPrice { get; init; }      // Ask on buy exchange
    public decimal SellPrice { get; init; }     // Bid on sell exchange
    public decimal GrossSpreadPercent { get; init; }
    public decimal NetProfitPercent { get; init; }  // after estimated fees
    public decimal BuyFeePercent { get; init; }
    public decimal SellFeePercent { get; init; }
    public DateTime DetectedAt { get; init; } = DateTime.UtcNow;

    public override string ToString()
    {
        return $"{Symbol}: Buy {BuyExchange} @ {BuyPrice:F4} → Sell {SellExchange} @ {SellPrice:F4} | " +
               $"Gross {GrossSpreadPercent:F3}% | Net {NetProfitPercent:F3}%";
    }
}
