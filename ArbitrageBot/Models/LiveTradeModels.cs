namespace ArbitrageBot.Models;

public sealed class LiveHedgePosition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Symbol { get; set; } = "";
    public string LongExchange { get; set; } = "";
    public string ShortExchange { get; set; } = "";
    public decimal BaseQty { get; set; }
    public decimal NotionalUsd { get; set; }
    public decimal LongEntry { get; set; }
    public decimal ShortEntry { get; set; }
    public string? LongOrderId { get; set; }
    public string? ShortOrderId { get; set; }
    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
    public bool IsOpen { get; set; } = true;
    public string Status { get; set; } = "Open";
    public string? Message { get; set; }
    public decimal? RealizedPnlUsd { get; set; }
    public DateTime? ClosedAt { get; set; }

    // ── Funding tracking ──────────────────────────────────────────────────
    /// <summary>Sum of all funding payments received (positive = we received, negative = we paid).</summary>
    public decimal AccumulatedFundingPnlUsd { get; set; }

    /// <summary>Funding delta at time of entry (ShortRate - LongRate).</summary>
    public decimal EntryFundingDeltaRate { get; set; }

    /// <summary>Number of funding periods settled since open.</summary>
    public int FundingPeriodsSettled { get; set; }

    /// <summary>Last time funding was credited to this position.</summary>
    public DateTime? LastFundingSettlementUtc { get; set; }

    /// <summary>Latest hold/close decision from HoldDecisionEngine.</summary>
    public string? LastHoldDecisionReason { get; set; }

    public bool ShouldHold { get; set; } = true;

    /// <summary>Unrealized price PnL (mark - entry) in USD — updated from exchange data.</summary>
    public decimal UnrealizedPricePnlUsd { get; set; }

    /// <summary>Total PnL = accumulated funding + unrealized price.</summary>
    public decimal TotalUnrealizedPnlUsd => AccumulatedFundingPnlUsd + UnrealizedPricePnlUsd;

    /// <summary>Position type: Spatial (fast open/close) or FundingArb (hold for funding).</summary>
    public string PositionType { get; set; } = "Spatial";  // "Spatial" | "FundingArb" | "Hybrid"
}

public sealed class LiveLedgerFile
{
    public List<LiveHedgePosition> Positions { get; set; } = [];
    public List<LiveHedgePosition> Closed { get; set; } = [];
}
