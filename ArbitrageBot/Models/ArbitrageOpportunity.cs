namespace ArbitrageBot.Models;

public record ArbitrageOpportunity
{
    public required string Symbol { get; init; }
    public required string BuyExchange { get; init; }
    public required string SellExchange { get; init; }

    public decimal BuyPriceTop { get; init; }
    public decimal SellPriceTop { get; init; }
    public decimal BuyPriceVwap { get; init; }
    public decimal SellPriceVwap { get; init; }
    public decimal QuoteSize { get; init; }
    public decimal FillBaseQty { get; init; }
    public bool FullyFilled { get; init; }
    public bool IsExecutable { get; init; }
    public decimal GrossSpreadTopPercent { get; init; }
    public decimal GrossSpreadVwapPercent { get; init; }
    public decimal BuyFeePercent { get; init; }
    public decimal SellFeePercent { get; init; }

    /// <summary>Net open: gross − open fees (one side each).</summary>
    public decimal NetProfitPercent { get; init; }

    /// <summary>Net round-trip: gross − open fees − close fees (2 taker touches per leg).</summary>
    public decimal NetRoundTripPercent { get; init; }

    /// <summary>Net after estimated funding benefit over FuturesFundingPeriods intervals.</summary>
    public decimal NetAfterFundingPercent { get; init; }

    public decimal NetProfitQuote { get; init; }
    public decimal BuySlippagePercent { get; init; }
    public decimal SellSlippagePercent { get; init; }

    // Funding fields (nullable = not yet fetched)
    public decimal? LongFundingRate { get; init; }
    public decimal? ShortFundingRate { get; init; }
    public decimal ExpectedFundingPercent { get; init; }

    public DateTime DetectedAt { get; init; } = DateTime.UtcNow;

    public override string ToString() =>
        $"{Symbol}: {BuyExchange}->{SellExchange} | " +
        $"gross {GrossSpreadVwapPercent:F3}% net {NetProfitPercent:F3}% RT {NetRoundTripPercent:F3}% | " +
        $"size {QuoteSize:F0} exec={IsExecutable}";
}
