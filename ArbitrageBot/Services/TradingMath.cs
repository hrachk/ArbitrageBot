namespace ArbitrageBot.Services;

// Pure, dependency-free trading math. Everything in this file is unit-tested
// (ArbitrageBot.Tests) and must stay free of exchange SDK types.

/// <summary>
/// Fair basis of one directed cross (symbol | long venue | short venue).
///
/// Why this replaced the old BasisStat:
///  - the old one was "ready" after 12 s and 4 samples, and an EMA seeded with the first
///    print keeps ~80% weight on that one print after 12 samples. "Fair basis" was then
///    effectively a single random tick, so excess = gross − fair was mostly noise;
///  - every print, including the spike being evaluated, was folded into the EMA.
///
/// Here: a slow EMA (half-life ≈ 35 samples), mean absolute deviation, a minimum
/// sample count AND a minimum wall-clock span, and outlier rejection so the dislocation
/// we want to trade never teaches the baseline that it is normal.
/// </summary>
public sealed class BasisEstimator
{
    public const decimal Alpha = 0.02m;
    public const long MinSampleGapMs = 700;

    private readonly object _lock = new();
    public decimal Ema { get; private set; }
    public decimal Dev { get; private set; }
    /// <summary>Accepted (non-outlier) observations.</summary>
    public int Samples { get; private set; }
    /// <summary>All throttled observations, outliers included. Warmup counts these.</summary>
    public int Seen { get; private set; }
    /// <summary>Consecutive outliers.</summary>
    public int Rejected { get; private set; }
    public long FirstMs { get; private set; }
    public long LastMs { get; private set; }

    /// <summary>Feed one tight-book observation of the directed gross cross, in percent.</summary>
    /// <returns>true if the sample was used.</returns>
    public bool Observe(decimal grossPercent, long nowMs)
    {
        if (grossPercent > 25m || grossPercent < -25m) return false;
        lock (_lock)
        {
            if (Seen > 0 && nowMs - LastMs < MinSampleGapMs) return false;
            if (Seen == 0)
            {
                Ema = grossPercent;
                Dev = 0m;
                Samples = 1;
                Seen = 1;
                FirstMs = LastMs = nowMs;
                return true;
            }

            Seen++;
            LastMs = nowMs;
            var err = grossPercent - Ema;
            var abs = err < 0 ? -err : err;
            // Once warmed, a print far outside the band is the event, not the baseline.
            // It still counts towards warmup (Seen). The first version counted only accepted
            // samples, so a route whose spread moved to a new level could never finish warmup
            // and was stuck in "basis-warmup" for good (ZKP Binance->Bybit, 35+ minutes at
            // 0.55-0.78% excess). A move that persists is learned at a quarter weight.
            if (Samples >= 10 && abs > OutlierBand(Dev))
            {
                Rejected++;
                if (Rejected < 3) return false;
                var slow = Alpha / 4m;
                Ema += slow * err;
                Dev += slow * (abs - Dev);
                return false;
            }

            Rejected = 0;
            Samples++;
            Ema += Alpha * err;
            Dev += Alpha * (abs - Dev);
            return true;
        }
    }

    public static decimal OutlierBand(decimal dev) => Math.Max(0.06m, dev * 4m);

    public (decimal Fair, decimal Dev, bool Ready) Read(int minSamples, long minSpanMs)
    {
        lock (_lock)
        {
            var ready = Seen >= Math.Max(4, minSamples) && Samples >= 4 && LastMs - FirstMs >= Math.Max(1000, minSpanMs);
            return (Ema, Dev, ready);
        }
    }
}

/// <summary>
/// Quote freshness for the two legs we would actually trade.
/// Cross-venue arbitrage on stale quotes is the textbook source of phantom profit:
/// one venue's price is old, the "spread" is just latency, and it is gone by the time
/// an order arrives. REST snapshots (polled every few seconds) are fine for the venue
/// median but must never be a traded leg.
/// </summary>
public static class QuoteGate
{
    /// <summary>
    /// A leg is tradable when its feed is alive, not when its price changed recently.
    /// A synchronised websocket order book only sends when the book changes: a quiet but
    /// synced book is the current price. Measuring "age since last change" (the first
    /// version of this gate) flickered on every quiet book, reset the persistence timer
    /// and blocked almost every entry.
    ///  - REST snapshot leg: never traded (it is seconds old by construction).
    ///  - Synced book: tradable while its quote is younger than <paramref name="syncedMaxAgeMs"/>.
    ///  - Book-ticker stream (no synced book): must be younger than <paramref name="maxAgeMs"/>,
    ///    and two such legs must not be further apart in time than <paramref name="maxSkewMs"/>.
    /// </summary>
    public static bool LegsFresh(
        double longAgeMs, double shortAgeMs,
        bool longIsRest, bool shortIsRest,
        bool longSynced, bool shortSynced,
        int maxAgeMs, int maxSkewMs, int syncedMaxAgeMs,
        out string? reason)
    {
        reason = null;
        if (longIsRest || shortIsRest) { reason = "rest-leg"; return false; }
        var age = maxAgeMs > 0 ? maxAgeMs : 1000;
        var syncedAge = syncedMaxAgeMs > 0 ? Math.Max(syncedMaxAgeMs, age) : Math.Max(5000, age);
        if (longAgeMs > (longSynced ? syncedAge : age) || shortAgeMs > (shortSynced ? syncedAge : age))
        { reason = "stale"; return false; }
        if (!longSynced && !shortSynced && maxSkewMs > 0 && Math.Abs(longAgeMs - shortAgeMs) > maxSkewMs)
        { reason = "skew"; return false; }
        return true;
    }
}

/// <summary>
/// Funding is paid only at settlement timestamps. What matters for a hedge that lives
/// seconds-to-minutes is whether a settlement of either venue falls inside the expected
/// hold, and at what CURRENT (predicted) rate — not the last settled historical rate.
/// Venues settle on different intervals (1h/2h/4h/8h per symbol), so each leg is timed
/// separately.
/// </summary>
public static class FundingModel
{
    /// <summary>
    /// Percent of notional we expect to PAY (positive) or RECEIVE (negative) from settlements
    /// that occur within <paramref name="horizon"/>. Null when a leg settles inside the horizon
    /// but its rate is unknown (caller decides how conservative to be).
    /// Rates are per-interval fractions (0.0001 = 0.01%).
    /// Long pays when its rate is positive; short receives when its rate is positive.
    /// </summary>
    public static decimal? CostInHorizonPercent(
        decimal? longRate, DateTime? longNextUtc,
        decimal? shortRate, DateTime? shortNextUtc,
        DateTime nowUtc, TimeSpan horizon)
    {
        decimal cost = 0m;
        if (Within(longNextUtc, nowUtc, horizon))
        {
            if (longRate is null) return null;
            cost += longRate.Value * 100m;
        }
        if (Within(shortNextUtc, nowUtc, horizon))
        {
            if (shortRate is null) return null;
            cost -= shortRate.Value * 100m;
        }
        return cost;
    }

    private static bool Within(DateTime? next, DateTime now, TimeSpan horizon)
        => next is { } t && t != default && t >= now && t - now <= horizon;

    /// <summary>
    /// Veto rule: an adverse settlement may eat at most <paramref name="maxShareOfEdge"/> of the
    /// net price edge, and never more than <paramref name="hardCapPercent"/>.
    /// </summary>
    public static bool Allows(decimal? costPercent, decimal netEdgePercent,
        decimal maxShareOfEdge = 0.35m, decimal hardCapPercent = 0.05m)
    {
        if (costPercent is null) return false;
        if (costPercent.Value <= 0m) return true;
        if (costPercent.Value > hardCapPercent) return false;
        return costPercent.Value <= Math.Max(0m, netEdgePercent) * maxShareOfEdge;
    }
}

public enum LegFillOutcome { Both, LongOnly, ShortOnly, None }

/// <summary>
/// Paper fill model that mirrors the live engine: both legs are IOC limits placed at the
/// scanned touch with a small slip allowance, evaluated against the book that exists
/// <c>latency</c> ms later — not against the book that produced the signal.
/// A leg whose fresh VWAP is outside its limit does not fill. A single filled leg must be
/// unwound at a cost, exactly as live does.
/// </summary>
public static class FillSimulator
{
    public static LegFillOutcome Evaluate(
        decimal signalLongAsk, decimal signalShortBid,
        decimal? freshLongAskVwap, decimal? freshShortBidVwap,
        decimal slipBps)
    {
        var slip = Math.Max(0m, slipBps) / 10_000m;
        var longLimit = signalLongAsk * (1m + slip);
        var shortLimit = signalShortBid * (1m - slip);
        var longOk = freshLongAskVwap is > 0 && freshLongAskVwap.Value <= longLimit;
        var shortOk = freshShortBidVwap is > 0 && freshShortBidVwap.Value >= shortLimit;
        return (longOk, shortOk) switch
        {
            (true, true) => LegFillOutcome.Both,
            (true, false) => LegFillOutcome.LongOnly,
            (false, true) => LegFillOutcome.ShortOnly,
            _ => LegFillOutcome.None
        };
    }

    /// <summary>
    /// Dollar cost of opening one leg and flattening it immediately:
    /// two taker fees plus the venue's full book width.
    /// </summary>
    public static decimal OneLegUnwindCostUsd(decimal notional, decimal takerPercent, decimal bookWidthPercent)
        => notional * (2m * Math.Max(0m, takerPercent) + Math.Max(0m, bookWidthPercent)) / 100m;
}

/// <summary>Walk one side of a book for a fixed base quantity (exit sizing).</summary>
public static class DepthMath
{
    /// <summary>
    /// Average price to trade <paramref name="baseQty"/> through <paramref name="levels"/>
    /// (best first). Returns null when the book cannot absorb the quantity.
    /// </summary>
    public static decimal? VwapForBaseQty(IReadOnlyList<(decimal Price, decimal Quantity)> levels, decimal baseQty)
    {
        if (baseQty <= 0 || levels.Count == 0) return null;
        decimal remaining = baseQty, quote = 0m;
        foreach (var (p, q) in levels)
        {
            if (p <= 0 || q <= 0) continue;
            var take = Math.Min(q, remaining);
            quote += take * p;
            remaining -= take;
            if (remaining <= 0) break;
        }
        if (remaining > baseQty * 0.001m) return null;
        return quote / (baseQty - Math.Max(0m, remaining));
    }
}
