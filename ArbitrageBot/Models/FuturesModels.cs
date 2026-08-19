namespace ArbitrageBot.Models;

public record FuturesOpportunity
{
    public required string Symbol { get; init; }
    public required string LongExchange { get; init; }
    public required string ShortExchange { get; init; }
    public decimal LongAskVwap { get; init; }
    public decimal ShortBidVwap { get; init; }
    public decimal LongAskTop { get; init; }
    public decimal ShortBidTop { get; init; }
    public decimal NotionalUsd { get; init; }
    public decimal BaseQty { get; init; }
    public bool FullyFilled { get; init; }
    public decimal GrossSpreadPercent { get; init; }
    /// <summary>Open fees only (legacy view).</summary>
    public decimal NetSpreadPercent { get; init; }
    /// <summary>Open+close taker fees both legs.</summary>
    public decimal NetRoundTripPercent { get; init; }
    /// <summary>Net after round-trip fees and expected funding over hold horizon.</summary>
    public decimal NetAfterFundingPercent { get; init; }
    public decimal EstNetPnlUsd { get; init; }
    public decimal LongFeePercent { get; init; }
    public decimal ShortFeePercent { get; init; }
    public decimal SlippagePercent { get; init; }
    public decimal? LongFundingRate { get; init; }
    public decimal? ShortFundingRate { get; init; }
    /// <summary>Expected funding PnL % over configured hold periods (positive = we receive).</summary>
    public decimal ExpectedFundingPercent { get; init; }
    public DateTime DetectedAt { get; init; } = DateTime.UtcNow;

    public override string ToString() =>
        $"{Symbol}: L {LongExchange}@{LongAskVwap:F2} / S {ShortExchange}@{ShortBidVwap:F2} | " +
        $"RT {NetRoundTripPercent:F3}% fund {ExpectedFundingPercent:F3}% → {NetAfterFundingPercent:F3}%";
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
    public decimal UnrealizedPnlUsd { get; set; }
    public decimal CurrentWidthPercent { get; set; }
}
