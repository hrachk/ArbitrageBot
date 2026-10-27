namespace ArbitrageBot.Services;

/// <summary>
/// One price-edge model for scan, paper and live.
/// A cross-exchange hedge is long the cheap ask and short the rich bid.
/// Profit exists only after four taker legs (open + close). Funding is carry:
/// it can veto a trade, it never pays for a thin price edge.
/// </summary>
public static class ArbEconomics
{
    /// <summary>Taker fee in percent points (0.05 = 0.05%).</summary>
    public static decimal TakerPercent(IReadOnlyDictionary<string, decimal>? table, string exchange)
    {
        if (table != null)
        {
            if (table.TryGetValue(exchange, out var f) && f > 0) return f;
            foreach (var kv in table)
            {
                if (kv.Key.Equals(exchange, StringComparison.OrdinalIgnoreCase) && kv.Value > 0)
                    return kv.Value;
            }
        }
        return exchange.ToUpperInvariant() switch
        {
            "BITGET" or "KUCOIN" => 0.06m,
            "BYBIT" => 0.055m,
            _ => 0.05m
        };
    }

    /// <summary>Four taker touches, in percent points.</summary>
    public static decimal RoundTripFeePercent(decimal longFeePct, decimal shortFeePct)
        => 2m * (longFeePct + shortFeePct);

    /// <summary>
    /// Dollars left if the cross spread later converges to zero.
    /// Independent of the meeting price: (shortBid - longAsk) * qty minus both open and both close fees.
    /// </summary>
    public static decimal ExpectedConvergeUsd(
        decimal longAsk, decimal shortBid, decimal qty,
        decimal longFeePct, decimal shortFeePct)
    {
        if (qty <= 0 || longAsk <= 0 || shortBid <= longAsk) return 0m;
        var gross = (shortBid - longAsk) * qty;
        var open = longAsk * qty * longFeePct / 100m + shortBid * qty * shortFeePct / 100m;
        var close = open; // same notionals; close prices converge near these
        return gross - open - close;
    }

    /// <summary>
    /// Mark-to-market if we flatten now: sell the long at bid, buy the short at ask, minus open and close fees.
    /// </summary>
    public static decimal FlattenNetUsd(
        decimal longEntry, decimal shortEntry, decimal qty,
        decimal longBid, decimal shortAsk,
        decimal longFeePct, decimal shortFeePct)
    {
        var legs = (shortEntry - shortAsk) * qty + (longBid - longEntry) * qty;
        var open = longEntry * qty * longFeePct / 100m + shortEntry * qty * shortFeePct / 100m;
        var close = longBid * qty * longFeePct / 100m + shortAsk * qty * shortFeePct / 100m;
        return legs - open - close;
    }

    /// <summary>
    /// Stop must sit outside the fee bill. A -$3 stop on a trade whose round trip costs $8
    /// closes the hedge on the entry cross itself and books a guaranteed loss.
    /// </summary>
    public static decimal EffectiveStopUsd(decimal configuredStop, decimal roundTripFeesUsd)
    {
        var noise = -(Math.Abs(roundTripFeesUsd) * 2m);
        if (noise > -1m) noise = -1m;
        if (configuredStop >= 0) return noise;
        return Math.Min(configuredStop, noise);
    }
}
