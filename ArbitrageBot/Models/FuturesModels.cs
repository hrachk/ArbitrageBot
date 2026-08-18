namespace ArbitrageBot.Models;

public record FuturesOpportunity
{
    public required string Symbol { get; init; }
    public required string LongExchange { get; init; }   // buy cheap
    public required string ShortExchange { get; init; }  // sell rich
    public decimal LongAskVwap { get; init; }
    public decimal ShortBidVwap { get; init; }
    public decimal LongAskTop { get; init; }
    public decimal ShortBidTop { get; init; }
    public decimal NotionalUsd { get; init; }
    public decimal BaseQty { get; init; }
    public bool FullyFilled { get; init; }
    public decimal GrossSpreadPercent { get; init; }
    public decimal NetSpreadPercent { get; init; }
    public decimal EstNetPnlUsd { get; init; }
    public decimal LongFeePercent { get; init; }
    public decimal ShortFeePercent { get; init; }
    public decimal SlippagePercent { get; init; }
    public DateTime DetectedAt { get; init; } = DateTime.UtcNow;

    public override string ToString() =>
        $"{Symbol}: LONG {LongExchange}@{LongAskVwap:F2} / SHORT {ShortExchange}@{ShortBidVwap:F2} | net {NetSpreadPercent:F3}% (~{EstNetPnlUsd:F2} USD)";
}

public record FuturesPaperTrade
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTime OpenedAt { get; init; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; init; }
    public required string Symbol { get; init; }
    public required string LongExchange { get; init; }
    public required string ShortExchange { get; init; }
    public decimal BaseQty { get; init; }
    public decimal LongEntry { get; init; }
    public decimal ShortEntry { get; init; }
    public decimal? LongExit { get; init; }
    public decimal? ShortExit { get; init; }
    public decimal OpenFeesUsd { get; init; }
    public decimal? CloseFeesUsd { get; init; }
    public decimal? RealizedPnlUsd { get; init; }
    public bool IsOpen { get; init; } = true;
    public string Status { get; init; } = "Open";
    public string? Message { get; init; }
}

public class FuturesPaperPosition
{
    public required string Symbol { get; set; }
    public required string LongExchange { get; set; }
    public required string ShortExchange { get; set; }
    public decimal BaseQty { get; set; }
    public decimal LongEntry { get; set; }
    public decimal ShortEntry { get; set; }
    public DateTime OpenedAt { get; set; }
    public Guid TradeId { get; set; }
}
