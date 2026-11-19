using ArbitrageBot.Services;

namespace ArbitrageBot.Tests;

internal static class Program
{
    private static int _pass, _fail;
    private static readonly List<string> Failures = [];

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) { _pass++; return; }
        _fail++;
        Failures.Add(detail is null ? name : $"{name} :: {detail}");
    }

    private static void Near(string name, decimal actual, decimal expected, decimal tol = 0.0000001m)
        => Check(name, Math.Abs(actual - expected) <= tol, $"expected {expected}, got {actual}");

    private static int Main()
    {
        Economics();
        EconomicIdentity();
        Basis();
        Freshness();
        Funding();
        Fills();
        Depth();

        Console.WriteLine($"passed {_pass}, failed {_fail}");
        foreach (var f in Failures) Console.WriteLine("  FAIL " + f);
        return _fail == 0 ? 0 : 1;
    }

    private static void Economics()
    {
        // Regression: operator thresholds were silently lowered (0.12 -> 0.04, $0.50 -> $0.10, gross 0.40 -> 0.22).
        Near("net: configured 0.12 is respected", ArbEconomics.RequiredNetPercent(0.12m), 0.12m);
        Near("net: zero falls back to floor", ArbEconomics.RequiredNetPercent(0m), 0.02m);
        Near("net: below floor is raised", ArbEconomics.RequiredNetPercent(0.001m), 0.02m);
        Near("tp: configured $0.50 on $100 is respected", ArbEconomics.RequiredTakeProfitUsd(0.5m, 100m), 0.5m);
        Near("tp: unset uses $0.10 floor", ArbEconomics.RequiredTakeProfitUsd(0m, 100m), 0.10m);
        Near("tp: percent floor on large notional", ArbEconomics.RequiredTakeProfitUsd(0.05m, 1000m), 0.20m);
        Near("gross: configured 0.40 respected", ArbEconomics.RequiredGrossPercent(0.40m, 0.11m), 0.40m);
        Near("gross: unset = open fees + 0.04", ArbEconomics.RequiredGrossPercent(0m, 0.11m), 0.15m);
        Near("gross: never below open fees + 0.02", ArbEconomics.RequiredGrossPercent(0.05m, 0.11m), 0.13m);
        Check("persist: 800 respected", ArbEconomics.PersistMs(800) == 800);
        Check("persist: floor 200", ArbEconomics.PersistMs(50) == 200);
        Check("persist: cap 5000", ArbEconomics.PersistMs(99_999) == 5000);

        Near("rt fees = 4 touches", ArbEconomics.RoundTripFeePercent(0.05m, 0.055m), 0.21m);
        Near("revert usd", ArbEconomics.ExpectedRevertUsd(0.5m, 100m, 0.05m, 0.05m, 0.04m, 0.03m), 0.23m);
        Check("revert usd rejects broken exit cross", ArbEconomics.ExpectedRevertUsd(0.5m, 100m, 0.05m, 0.05m, 99m, 0.03m) < -100m);
        Near("taker table fallback kucoin", ArbEconomics.TakerPercent(null, "Kucoin"), 0.06m);
        Near("taker table override", ArbEconomics.TakerPercent(new Dictionary<string, decimal> { ["binance"] = 0.04m }, "Binance"), 0.04m);

        Check("books tradable: tight", ArbEconomics.BooksTradable(100m, 100.05m, 100.1m, 100.15m));
        Check("books tradable: one wide", !ArbEconomics.BooksTradable(100m, 100.5m, 100.1m, 100.15m));
        // median 100.00: long ask 99.90 is cheap, short bid 100.10 is rich -> two-sided
        Check("two-sided: both venues dislocated", ArbEconomics.TwoSidedDislocation(100m, 99.90m, 100.10m));
        // one venue printing cheap, the other at the median -> broken feed, not an arb
        Check("two-sided: one-sided print rejected", !ArbEconomics.TwoSidedDislocation(100m, 99.40m, 100.005m));
    }

    /// <summary>
    /// The scan's expected-revert formula must equal the paper ledger's realized PnL when the
    /// cross really reverts to its fair basis and widths are unchanged. If these disagree,
    /// paper results are not measuring what the entry gate predicted.
    /// </summary>
    private static void EconomicIdentity()
    {
        decimal fee = 0.05m;
        var rnd = new Random(7);
        var maxErr = 0m;
        for (var i = 0; i < 500; i++)
        {
            var mid = 1m + (decimal)rnd.NextDouble() * 99m;
            var wL = mid * (0.0002m + (decimal)rnd.NextDouble() * 0.0008m); // full book width
            var wS = mid * (0.0002m + (decimal)rnd.NextDouble() * 0.0008m);
            var fairMidGap = mid * ((decimal)rnd.NextDouble() * 0.004m - 0.002m); // fair cross of mids
            var shock = mid * (0.002m + (decimal)rnd.NextDouble() * 0.006m);       // dislocation beyond fair

            // Entry: long venue mid = mid, short venue mid = mid + fair + shock.
            var longAsk = mid + wL / 2m;
            var shortBid = mid + fairMidGap + shock - wS / 2m;
            // Fair "gross" (ask->bid) that the basis would have learned at the same widths:
            var fairLongAsk = mid + wL / 2m;
            var fairShortBid = mid + fairMidGap - wS / 2m;
            var fairGross = (fairShortBid - fairLongAsk) / fairLongAsk * 100m;
            var gross = (shortBid - longAsk) / longAsk * 100m;
            var excess = gross - fairGross;

            // Exit after reversion to fair (both mids move by the same common drift).
            var drift = mid * ((decimal)rnd.NextDouble() * 0.01m - 0.005m);
            var exitLongBid = mid + drift - wL / 2m;
            var exitShortAsk = mid + fairMidGap + drift + wS / 2m;

            var notional = 100m;
            var qty = notional / longAsk;
            var exitCrossPct = wL / (mid - wL / 2m) * 100m + wS / (mid + fairMidGap - wS / 2m) * 100m;
            var predicted = ArbEconomics.ExpectedRevertUsd(excess, notional, fee, fee, exitCrossPct, 0m);
            var realized = ArbEconomics.FlattenNetUsd(longAsk, shortBid, qty, exitLongBid, exitShortAsk, fee, fee);
            var err = Math.Abs(predicted - realized);
            if (err > maxErr) maxErr = err;
        }
        // Fees are charged on slightly different bases (entry vs exit prices); allow 2 cents per $100.
        Check("identity: predicted revert PnL == realized flatten PnL", maxErr < 0.02m, $"max error ${maxErr:F5}");
    }

    private static void Basis()
    {
        var b = new BasisEstimator();
        long t = 1_000_000;
        var rnd = new Random(1);
        for (var i = 0; i < 30; i++) { b.Observe(0.10m + (decimal)(rnd.NextDouble() - 0.5) * 0.02m, t); t += 1000; }
        Check("basis: not ready after 30 s", !b.Read(60, 90_000).Ready);
        for (var i = 0; i < 70; i++) { b.Observe(0.10m + (decimal)(rnd.NextDouble() - 0.5) * 0.02m, t); t += 1000; }
        var (fair, dev, ready) = b.Read(60, 90_000);
        Check("basis: ready after 100 s / 100 samples", ready);
        Near("basis: fair near 0.10", fair, 0.10m, 0.01m);
        Check("basis: dev small", dev < 0.02m, $"dev {dev}");

        var before = b.Read(60, 90_000).Fair;
        b.Observe(0.70m, t); t += 1000;
        Near("basis: single spike rejected", b.Read(60, 90_000).Fair, before, 0.0005m);

        Check("basis: samples throttled inside 700 ms", !b.Observe(0.10m, t - 900));

        var old = new BasisEstimator(); // the pre-audit behaviour: ready at 12 s, 4 samples
        long t2 = 0; old.Observe(0.50m, t2);
        for (var i = 0; i < 12; i++) { t2 += 1000; old.Observe(0.10m, t2); }
        Check("basis: 12 s of data is NOT enough (old rule said ready)", !old.Read(60, 90_000).Ready);

        // Regression: a route whose spread moved to a new level never finished warmup.
        var shift = new BasisEstimator(); long ts = 0;
        for (var i = 0; i < 15; i++) { shift.Observe(0.10m, ts); ts += 1000; }   // warm enough to reject outliers
        for (var i = 0; i < 120; i++) { shift.Observe(0.70m, ts); ts += 1000; }  // spread jumps and stays
        Check("basis: warmup completes after a level shift", shift.Read(60, 90_000).Ready, $"seen {shift.Seen} accepted {shift.Samples}");

        // A persistent regime change is still learned eventually.
        var r = new BasisEstimator(); long t3 = 0;
        for (var i = 0; i < 120; i++) { r.Observe(0.10m, t3); t3 += 1000; }
        for (var i = 0; i < 600; i++) { r.Observe(0.40m, t3); t3 += 1000; }
        Check("basis: regime shift learned", r.Read(60, 90_000).Fair > 0.30m, $"fair {r.Read(60, 90_000).Fair}");
    }

    private static void Freshness()
    {
        // (longAge, shortAge, longRest, shortRest, longSynced, shortSynced, maxAge, maxSkew, syncedMaxAge)
        Check("fresh: both socket legs young", QuoteGate.LegsFresh(120, 300, false, false, false, false, 1000, 600, 5000, out _));
        Check("fresh: REST leg never traded", !QuoteGate.LegsFresh(10, 10, true, false, true, true, 1000, 600, 5000, out var r1) && r1 == "rest-leg");
        Check("fresh: stale ticker leg rejected", !QuoteGate.LegsFresh(1200, 10, false, false, false, false, 1000, 600, 5000, out var r2) && r2 == "stale");
        Check("fresh: skew between ticker legs rejected", !QuoteGate.LegsFresh(900, 50, false, false, false, false, 1000, 600, 5000, out var r3) && r3 == "skew");
        // Regression: a quiet but synced book was treated as stale, flickering the gate.
        Check("fresh: quiet synced books are live", QuoteGate.LegsFresh(2500, 40, false, false, true, true, 1000, 600, 5000, out _));
        Check("fresh: synced book silent too long is stale", !QuoteGate.LegsFresh(6000, 40, false, false, true, true, 1000, 600, 5000, out _));
        Check("fresh: no skew rule when a leg is synced", QuoteGate.LegsFresh(900, 50, false, false, true, false, 1000, 600, 5000, out _));
    }

    private static void Funding()
    {
        var now = new DateTime(2026, 11, 18, 7, 50, 0, DateTimeKind.Utc);
        var h = TimeSpan.FromMinutes(20);
        var farFuture = now.AddHours(3);
        Near("funding: no settlement in horizon costs nothing",
            FundingModel.CostInHorizonPercent(0.001m, farFuture, -0.001m, farFuture, now, h)!.Value, 0m);
        Near("funding: long pays its positive rate",
            FundingModel.CostInHorizonPercent(0.0005m, now.AddMinutes(10), 0m, farFuture, now, h)!.Value, 0.05m);
        Near("funding: short receives its positive rate",
            FundingModel.CostInHorizonPercent(0m, farFuture, 0.0005m, now.AddMinutes(10), now, h)!.Value, -0.05m);
        Check("funding: unknown rate settling in horizon is unknown",
            FundingModel.CostInHorizonPercent(null, now.AddMinutes(5), 0m, farFuture, now, h) is null);
        Check("funding allows: unknown -> veto", !FundingModel.Allows(null, 0.1m));
        Check("funding allows: receiving -> ok", FundingModel.Allows(-0.03m, 0.01m));
        Check("funding allows: small cost vs edge", FundingModel.Allows(0.02m, 0.10m));
        Check("funding allows: cost > 35% of edge -> veto", !FundingModel.Allows(0.04m, 0.10m));
        Check("funding allows: above hard cap -> veto", !FundingModel.Allows(0.06m, 1.00m));
    }

    private static void Fills()
    {
        Check("fill: both legs inside limits",
            FillSimulator.Evaluate(100m, 100.5m, 100.02m, 100.48m, 4m) == LegFillOutcome.Both);
        Check("fill: long ran away -> only short fills",
            FillSimulator.Evaluate(100m, 100.5m, 100.20m, 100.48m, 4m) == LegFillOutcome.ShortOnly);
        Check("fill: short collapsed -> only long fills",
            FillSimulator.Evaluate(100m, 100.5m, 100.00m, 100.30m, 4m) == LegFillOutcome.LongOnly);
        Check("fill: no book -> none",
            FillSimulator.Evaluate(100m, 100.5m, null, null, 4m) == LegFillOutcome.None);
        Near("unwind cost = 2 fees + width", FillSimulator.OneLegUnwindCostUsd(100m, 0.05m, 0.04m), 0.14m);
    }

    private static void Depth()
    {
        var lv = new List<(decimal, decimal)> { (10m, 1m), (11m, 1m) };
        Near("depth: vwap across two levels", DepthMath.VwapForBaseQty(lv, 1.5m)!.Value, 15.5m / 1.5m);
        Check("depth: insufficient book -> null", DepthMath.VwapForBaseQty(lv, 3m) is null);
        Near("depth: exact top level", DepthMath.VwapForBaseQty(lv, 1m)!.Value, 10m);
    }
}
