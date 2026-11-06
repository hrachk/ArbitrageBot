using System.Collections.Concurrent;

namespace ArbitrageBot.Services;

/// <summary>
/// One price-edge model for scan, paper and live.
/// A cross-exchange hedge is long the cheap ask and short the rich bid.
/// Profit is only the part of the gap that is ABOVE that pair's own fair basis.
/// A spread that has sat at 1% for minutes is the basis, not a trade.
/// Funding is carry: it can veto a trade, it never pays for a thin price edge.
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

    /// <summary>A book wider than this is noise, not a hedgeable touch. Percent points.</summary>
    public const decimal MaxBookWidthPercent = 0.22m;

    /// <summary>Above this the print is a bad contract or a broken feed, not an arb.</summary>
    public const decimal MaxSaneGrossPercent = 2.2m;

    /// <summary>
    /// A directed cross that lives here after the basis warms up is structural.
    /// Mean-reversion will not pay the fees. Ban the route.
    /// </summary>
    public const decimal StructuralBasisPercent = 0.45m;

    /// <summary>Each leg must own at least this fraction of the gap versus the venue median.</summary>
    public const decimal MinLegShare = 0.18m;

    public static bool BooksTradable(decimal longBid, decimal longAsk, decimal shortBid, decimal shortAsk)
    {
        var longW = BookSpreadPercent(longBid, longAsk);
        var shortW = BookSpreadPercent(shortBid, shortAsk);
        return longW <= MaxBookWidthPercent && shortW <= MaxBookWidthPercent;
    }

    /// <summary>
    /// Both venues must be on the wrong side of the cross-venue median.
    /// A 1% gap that is only one exchange printing cheap is a bad feed.
    /// </summary>
    public static bool TwoSidedDislocation(decimal median, decimal longAsk, decimal shortBid)
    {
        if (median <= 0 || longAsk <= 0 || shortBid <= longAsk) return false;
        var cheap = (median - longAsk) / median * 100m;
        var rich = (shortBid - median) / median * 100m;
        if (cheap <= 0.008m || rich <= 0.008m) return false;
        var gross = (shortBid - longAsk) / longAsk * 100m;
        var floor = gross * MinLegShare;
        return cheap >= floor && rich >= floor;
    }

    /// <summary>Four taker touches, in percent points.</summary>
    public static decimal RoundTripFeePercent(decimal longFeePct, decimal shortFeePct)
        => 2m * (longFeePct + shortFeePct);

    /// <summary>
    /// Extra percent a market order pays beyond the book you already measured.
    /// The two book widths are already charged as the exit cross.
    /// A saved 0.08 on top of that is why a session can produce zero entries.
    /// </summary>
    public static decimal ExecutionReservePercent(decimal configured)
    {
        var v = configured > 0 ? configured : 0.03m;
        if (v < 0.02m) v = 0.02m;
        if (v > 0.04m) v = 0.04m;
        return v;
    }

    /// <summary>Net edge that must remain after fees and a realistic exit allowance.</summary>
    public const decimal MinNetAfterCostsPercent = 0.04m;

    public static decimal RequiredNetPercent(decimal minProfit)
    {
        var v = minProfit > 0 ? minProfit : MinNetAfterCostsPercent;
        if (v < 0.03m) v = 0.03m;
        // A saved 0.20 on top of four taker fees asked for a spike this market does not print.
        if (v > 0.06m) v = 0.06m;
        return v;
    }

    /// <summary>
    /// Dollar form of the net gate. Do not add another 0.60% of notional on top of fees:
    /// that stack is why a whole day produced zero entries.
    /// </summary>
    public static decimal RequiredTakeProfitUsd(decimal configured, decimal notional)
    {
        var fromEdge = notional > 0 ? notional * MinNetAfterCostsPercent / 100m : 0.10m;
        if (fromEdge < 0.10m) fromEdge = 0.10m;
        if (configured <= 0) return fromEdge;
        var cap = notional > 0 ? Math.Max(notional * 0.0008m, fromEdge) : configured;
        if (configured > cap) return cap;
        return Math.Max(configured, fromEdge);
    }

    /// <summary>
    /// Dollars left if the cross spread later converges to zero.
    /// Independent of the meeting price: (shortBid - longAsk) * qty minus both open and both close fees.
    /// Kept for diagnostics. Entry decisions use <see cref="ExpectedRevertUsd"/>.
    /// </summary>
    public static decimal ExpectedConvergeUsd(
        decimal longAsk, decimal shortBid, decimal qty,
        decimal longFeePct, decimal shortFeePct)
    {
        if (qty <= 0 || longAsk <= 0 || shortBid <= longAsk) return 0m;
        var gross = (shortBid - longAsk) * qty;
        var open = longAsk * qty * longFeePct / 100m + shortBid * qty * shortFeePct / 100m;
        var close = open;
        return gross - open - close;
    }

    /// <summary>
    /// Dollars left if the spread falls back to this pair's fair basis — not to zero.
    /// A 1.2% gap whose basis is already 1.1% has ~0.1% of capturable edge, then fees.
    /// </summary>
    public static decimal ExpectedRevertUsd(
        decimal excessPercent, decimal notional,
        decimal longFeePct, decimal shortFeePct,
        decimal exitCrossPercent, decimal reservePercent)
    {
        if (notional <= 0 || exitCrossPercent >= 50m) return -999m;
        var capturable = notional * excessPercent / 100m;
        var fees = notional * RoundTripFeePercent(longFeePct, shortFeePct) / 100m;
        var slip = notional * (Math.Max(exitCrossPercent, 0m) + Math.Max(reservePercent, 0m)) / 100m;
        return capturable - fees - slip;
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
        var chosen = configuredStop < 0 ? configuredStop : -3m;
        var reward = minTakeProfitUsd > 0 ? minTakeProfitUsd : 1.5m;
        var band = -(reward * 1.6m);
        // Never risk more than 1.6× the target, and never sit inside fee noise.
        if (chosen < band) chosen = band;
        if (chosen > noise) chosen = noise;
        return chosen;
    }
}

/// <summary>
/// Session ban after a stop-out or a dead book. Stops the same toxic name from reloading.
/// </summary>
public static class TradeBan
{
    private static readonly ConcurrentDictionary<string, long> UntilMs = new(StringComparer.OrdinalIgnoreCase);

    public static void Ban(string? symbol, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(symbol) || duration <= TimeSpan.Zero) return;
        var until = Environment.TickCount64 + (long)duration.TotalMilliseconds;
        UntilMs.AddOrUpdate(symbol.Trim(), until, (_, prev) => Math.Max(prev, until));
    }

    public static void BanRoute(string? symbol, string? longEx, string? shortEx, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(longEx) || string.IsNullOrWhiteSpace(shortEx))
            return;
        Ban(RouteKey(symbol, longEx, shortEx), duration);
    }

    public static bool IsBanned(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return false;
        if (!UntilMs.TryGetValue(symbol.Trim(), out var until)) return false;
        if (Environment.TickCount64 < until) return true;
        UntilMs.TryRemove(symbol.Trim(), out _);
        return false;
    }

    public static bool IsRouteBanned(string? symbol, string? longEx, string? shortEx)
    {
        if (IsBanned(symbol)) return true;
        return IsBanned(RouteKey(symbol, longEx, shortEx));
    }

    private static string RouteKey(string? symbol, string? longEx, string? shortEx)
        => $"route:{(symbol ?? "").Trim()}|{(longEx ?? "").Trim()}|{(shortEx ?? "").Trim()}";
}
