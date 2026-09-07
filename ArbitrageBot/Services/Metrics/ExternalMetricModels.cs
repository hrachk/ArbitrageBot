namespace ArbitrageBot.Services.Metrics;

public sealed class FundingSnapshot
{
    public required string Exchange { get; init; }
    public required string Symbol { get; init; } // e.g. BTCUSDT
    public decimal Rate { get; init; }           // per funding interval (usually 8h)
    public decimal? PredictedRate { get; init; }
    public DateTime? NextFundingUtc { get; init; }
    public DateTime UpdatedUtc { get; init; } = DateTime.UtcNow;
}

public sealed class OpenInterestSnapshot
{
    public required string Exchange { get; init; }
    public required string Symbol { get; init; }
    public decimal OpenInterestUsd { get; init; }
    public decimal? OpenInterestChangePct24h { get; init; }
    public DateTime UpdatedUtc { get; init; } = DateTime.UtcNow;
}

public sealed class LiquidationSnapshot
{
    public required string Symbol { get; init; }
    public decimal LongLiqUsd24h { get; init; }
    public decimal ShortLiqUsd24h { get; init; }
    public DateTime UpdatedUtc { get; init; } = DateTime.UtcNow;
}

public sealed class LongShortSnapshot
{
    public required string Exchange { get; init; }
    public required string Symbol { get; init; }
    public decimal LongRatio { get; init; }
    public decimal ShortRatio { get; init; }
    public DateTime UpdatedUtc { get; init; } = DateTime.UtcNow;
}

public sealed class ExchangeFlowSnapshot
{
    public required string Asset { get; init; } // BTC
    public required string Exchange { get; init; }
    public decimal? NetFlowUsd { get; init; }
    public decimal? InflowUsd { get; init; }
    public decimal? OutflowUsd { get; init; }
    public string Source { get; init; } = "unknown";
    public DateTime UpdatedUtc { get; init; } = DateTime.UtcNow;
}

public sealed class MetricAlert
{
    public required string Type { get; init; }
    public required string Message { get; init; }
    public string? Symbol { get; init; }
    public DateTime Utc { get; init; } = DateTime.UtcNow;
}

/// <summary>Aggregated signal pack consumed by the trading bot / UI.</summary>
public sealed class AggregatedMetricsSnapshot
{
    public DateTime Utc { get; init; } = DateTime.UtcNow;
    public IReadOnlyList<FundingSnapshot> Funding { get; init; } = [];
    public IReadOnlyList<OpenInterestSnapshot> OpenInterest { get; init; } = [];
    public IReadOnlyList<LiquidationSnapshot> Liquidations { get; init; } = [];
    public IReadOnlyList<LongShortSnapshot> LongShort { get; init; } = [];
    public IReadOnlyList<ExchangeFlowSnapshot> ExchangeFlows { get; init; } = [];
    public IReadOnlyList<MetricAlert> Alerts { get; init; } = [];
    public IReadOnlyList<FundingSpreadRow> FundingSpreads { get; init; } = [];
    public string? Note { get; init; }
}

public sealed class FundingSpreadRow
{
    public required string Symbol { get; init; }
    public required string LongExchange { get; init; }  // pay less / receive more → prefer long here if rate low
    public required string ShortExchange { get; init; }
    public decimal LongRate { get; init; }
    public decimal ShortRate { get; init; }
    /// <summary>ShortRate - LongRate (positive = shorts receive relative to longs on the other venue).</summary>
    public decimal DeltaRate { get; init; }
    public decimal DeltaAprApprox { get; init; } // delta * 3 * 365
}
