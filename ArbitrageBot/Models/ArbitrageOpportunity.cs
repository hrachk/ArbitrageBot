namespace ArbitrageBot.Models;

public record ArbitrageOpportunity
{
    public required string Symbol { get; init; }
    public required string BuyExchange { get; init; }
    public required string SellExchange { get; init; }

    /// <summary>Top-of-book ask (buy side) before walking the book.</summary>
    public decimal BuyPriceTop { get; init; }

    /// <summary>Top-of-book bid (sell side) before walking the book.</summary>
    public decimal SellPriceTop { get; init; }

    /// <summary>VWAP buy price after walking asks for target size.</summary>
    public decimal BuyPriceVwap { get; init; }

    /// <summary>VWAP sell price after walking bids for target size.</summary>
    public decimal SellPriceVwap { get; init; }

    /// <summary>Configured notional in quote currency (e.g. USDT).</summary>
    public decimal QuoteSize { get; init; }

    /// <summary>Base quantity that can actually be filled on the thinner side.</summary>
    public decimal FillBaseQty { get; init; }

    /// <summary>True when full quote size was available on both sides.</summary>
    public bool FullyFilled { get; init; }

    /// <summary>Passed open gates — bot may trade this route.</summary>
    public bool IsExecutable { get; init; }

    public decimal GrossSpreadTopPercent { get; init; }
    public decimal GrossSpreadVwapPercent { get; init; }

    public decimal BuyFeePercent { get; init; }
    public decimal SellFeePercent { get; init; }

    /// <summary>Net profit % after fees using VWAP (realistic).</summary>
    public decimal NetProfitPercent { get; init; }

    /// <summary>Estimated net PnL in quote currency for the fill size.</summary>
    public decimal NetProfitQuote { get; init; }

    public decimal BuySlippagePercent { get; init; }
    public decimal SellSlippagePercent { get; init; }

    public DateTime DetectedAt { get; init; } = DateTime.UtcNow;

    public override string ToString()
    {
        return $"{Symbol}: {BuyExchange}->{SellExchange} | " +
               $"VWAP buy {BuyPriceVwap:F4} / sell {SellPriceVwap:F4} | " +
               $"Net {NetProfitPercent:F3}% ({NetProfitQuote:F2} quote) | " +
               $"size {QuoteSize:F0} fully={FullyFilled}";
    }
}
