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

    /// <summary>
    /// Percent you still pay to flatten after the two venues meet:
    /// you sell the long at its bid and buy the short at its ask.
    /// A cross-exchange gap that is only these two book spreads is not a trade.
    /// </summary>
    public static decimal ExitCrossPercent(decimal longBid, decimal longAsk, decimal shortBid, decimal shortAsk)
    {
        var longSpread = BookSpreadPercent(longBid, longAsk);
        var shortSpread = BookSpreadPercent(shortBid, shortAsk);
        if (longSpread > 1.5m || shortSpread > 1.5m) return 99m;
        return longSpread + shortSpread;
    }

    public static decimal BookSpreadPercent(decimal bid, decimal ask)
    {
        if (bid <= 0 || ask <= 0 || ask < bid) return 99m;
        return (ask - bid) / bid * 100m;
    }

    /// <summary>Four taker touches, in percent points.</summary>
    public static decimal RoundTripFeePercent(decimal longFeePct, decimal shortFeePct)
        => 2m * (longFeePct + shortFeePct);

    /// <summary>
    /// Extra percent a market order pays beyond the book you already measured.
    /// A saved buffer of 0.02 is one tick and is not enough; the floor is 0.08.
    /// </summary>
    public static decimal ExecutionReservePercent(decimal configured)
        => Math.Max(configured > 0 ? configured : 0m, 0.08m);

    /// <summary>Net edge that must remain after fees, both book spreads, and the reserve.</summary>
    public const decimal MinNetAfterCostsPercent = 0.20m;

    public static decimal RequiredNetPercent(decimal minProfit)
        => Math.Max(minProfit, MinNetAfterCostsPercent);

    /// <summary>
    /// Dollar target. Never below 0.60% of notional and never below $1.
    /// A $0.40 target against a $6 stop is how the book bleeds.
    /// </summary>
    public static decimal RequiredTakeProfitUsd(decimal configured, decimal notional)
    {
        var byNotional = notional > 0 ? notional * 0.006m : 1.50m;
        var cfg = configured > 0 ? configured : 1.50m;
        return Math.Max(cfg, Math.Max(byNotional, 1.00m));
    }

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
    /// Stop sits outside the fee bill, and no wider than 1.6× the take-profit.
    /// Risking $6 to make $0.40 needs a 94% win rate. This book does not have that.
    /// </summary>
    public static decimal EffectiveStopUsd(decimal configuredStop, decimal roundTripFeesUsd, decimal minTakeProfitUsd = 1.5m)
    {
        var noise = -(Math.Abs(roundTripFeesUsd) * 2m);
        if (noise > -1.5m) noise = -1.5m;
        var reward = minTakeProfitUsd > 0 ? minTakeProfitUsd : 1.5m;
        var band = -(reward * 1.6m);
        var chosen = configuredStop < 0 ? Math.Max(configuredStop, band) : band;
        if (chosen > noise) chosen = noise;
        return chosen;
    }
}
