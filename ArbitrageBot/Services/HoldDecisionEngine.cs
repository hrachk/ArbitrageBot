using ArbitrageBot.Configuration;
using ArbitrageBot.Models;
using Microsoft.Extensions.Options;

namespace ArbitrageBot.Services;

/// <summary>
/// Математический движок принятия решения ДЕРЖАТЬ / ЗАКРЫТЬ для открытых хеджей.
///
/// Логика:
///   NET_IF_HOLD = accumulated_funding + expected_next_funding
///   NET_IF_EXIT = accumulated_funding - exit_cost
///
///   ДЕРЖАТЬ если:
///     1. delta_now > breakeven_delta  (покрывает комиссии выхода)
///     2. trend == "expanding"         (EMA5 > EMA20 — дельта растёт)
///     3. NET_IF_HOLD > NET_IF_EXIT    (держать выгоднее чем закрыть)
///     4. position.UnrealizedPnl > stop_loss  (SL не сработал)
///
///   ЗАКРЫТЬ если любое из:
///     - funding_delta < breakeven (дельта не покрывает выход)
///     - delta перевернулась (мы начинаем платить)
///     - trend = "converging" AND accumulated >= exit_cost (зафиксировать)
///     - spatial spread открылся (можно выйти с ценовой прибылью)
///     - SL сработал
/// </summary>
public sealed class HoldDecisionEngine
{
    private readonly ILogger<HoldDecisionEngine> _logger;
    private readonly IOptions<ArbitrageOptions> _opts;
    private readonly FundingRateService _funding;

    // Safety multiplier: expected next must be > exit_cost × SafetyMult to justify hold
    private const decimal SafetyMult = 1.5m;

    public HoldDecisionEngine(
        ILogger<HoldDecisionEngine> logger,
        IOptions<ArbitrageOptions> opts,
        FundingRateService funding)
    {
        _logger = logger;
        _opts   = opts;
        _funding = funding;
    }

    /// <summary>
    /// Evaluate whether to hold or close a live hedge position.
    /// Call this on every funding cycle (every 8h) and on every scan for spatial exit.
    /// </summary>
    public HoldDecision Evaluate(
        LiveHedgePosition position,
        decimal? currentSpatialSpreadPct = null)   // if book data available
    {
        var opts = _opts.Value;

        // ── 1. Funding data ─────────────────────────────────────────────────
        var delta = _funding.GetDelta(
            position.Symbol,
            position.LongExchange,
            position.ShortExchange);

        // ── 2. Fee math ─────────────────────────────────────────────────────
        decimal takerFee = GetTakerFee(position.LongExchange, opts)
                         + GetTakerFee(position.ShortExchange, opts);  // sum both legs
        decimal notional   = position.NotionalUsd;
        decimal exitCostUsd = takerFee / 100m * notional * 2m;  // 2 legs × taker%

        // ── 3. Accumulated funding PnL (already in position) ─────────────────
        decimal accumulated = position.AccumulatedFundingPnlUsd;

        // ── 4. Expected next funding payment ────────────────────────────────
        decimal expectedNext = 0;
        if (delta != null)
        {
            // Expected = delta_rate × notional (per interval)
            expectedNext = delta.DeltaRate * notional;
        }

        // ── 5. Breakeven delta ───────────────────────────────────────────────
        // We need to earn at least exitCostUsd in one interval to justify holding
        // breakeven_delta_rate = exitCostUsd / notional
        decimal breakevenDelta = notional > 0 ? exitCostUsd / notional : 999m;

        // ── 6. NET calculations ──────────────────────────────────────────────
        decimal netIfHold = accumulated + expectedNext;
        decimal netIfExit = accumulated - exitCostUsd;

        // ── 7. Stop-loss check ───────────────────────────────────────────────
        decimal stopLoss = opts.LiveStopLossUsd;
        decimal unrealized = position.UnrealizedPricePnlUsd;
        bool hitStopLoss = unrealized < stopLoss;

        // ── 8. Spatial exit check ────────────────────────────────────────────
        // If spread opened up ≥ MinTakeProfitUsd worth, better to close now
        bool spatialExitAvailable = currentSpatialSpreadPct.HasValue
            && currentSpatialSpreadPct.Value >= opts.MinGrossSpreadPercent
            && netIfExit + (currentSpatialSpreadPct.Value / 100m * notional) > 0;

        // ── 9. Decision logic ────────────────────────────────────────────────
        string reason;
        bool shouldHold;

        if (hitStopLoss)
        {
            shouldHold = false;
            reason = $"STOP LOSS: unrealized {unrealized:+0.00;-0.00} USD < limit {stopLoss:0.00}";
        }
        else if (delta == null)
        {
            // No funding data — use time-based fallback (spatial scalp mode)
            var age = DateTime.UtcNow - position.OpenedAt;
            shouldHold = age.TotalSeconds < opts.FuturesMaxHoldSeconds;
            reason = shouldHold
                ? $"No funding data — spatial hold ({age.TotalSeconds:0}s / {opts.FuturesMaxHoldSeconds}s)"
                : "No funding data — spatial timeout, close";
        }
        else if (delta.DeltaRate < 0)
        {
            shouldHold = false;
            reason = $"Funding delta NEGATIVE ({delta.DeltaRate * 100:+0.0000;-0.0000}%) — we now PAY, close";
        }
        else if (delta.DeltaRate < breakevenDelta && accumulated >= exitCostUsd)
        {
            // Delta too small but we've covered entry costs — take profit
            shouldHold = false;
            reason = $"Delta {delta.DeltaRate * 100:0.0000}% < breakeven {breakevenDelta * 100:0.0000}% " +
                     $"AND accumulated {accumulated:+0.00} covers exit — take profit";
        }
        else if (delta.DeltaRate < breakevenDelta && accumulated < exitCostUsd)
        {
            // Delta too small and we haven't covered entry yet — still hold briefly
            // unless trend is clearly converging
            if (delta.Trend == "converging" && delta.Ema5 < breakevenDelta * 0.5m)
            {
                shouldHold = false;
                reason = $"Delta {delta.DeltaRate * 100:0.0000}% < breakeven, trend converging — cut loss";
            }
            else
            {
                shouldHold = true;
                reason = $"Delta {delta.DeltaRate * 100:0.0000}% < breakeven but not yet covered entry — hold briefly";
            }
        }
        else if (spatialExitAvailable)
        {
            shouldHold = false;
            reason = $"Spatial spread {currentSpatialSpreadPct:+0.000}% opened — better to close now";
        }
        else if (netIfHold > netIfExit * SafetyMult)
        {
            shouldHold = true;
            reason = $"HOLD: net_if_hold {netIfHold:+0.00} > net_if_exit {netIfExit:+0.00} × {SafetyMult} " +
                     $"| delta {delta.DeltaRate * 100:+0.0000}% | trend {delta.Trend} " +
                     $"| EMA5 {delta.Ema5 * 100:0.0000}% EMA20 {delta.Ema20 * 100:0.0000}%";
        }
        else
        {
            shouldHold = false;
            reason = $"CLOSE: net_if_hold {netIfHold:+0.00} ≤ net_if_exit×mult — marginal, exit";
        }

        var decision = new HoldDecision(
            ShouldHold:          shouldHold,
            AccumulatedFundingPnlUsd: accumulated,
            ExpectedNextFundingUsd:   expectedNext,
            ExitCostUsd:         exitCostUsd,
            NetIfHold:           netIfHold,
            NetIfExit:           netIfExit,
            BreakevenDeltaRate:  breakevenDelta,
            CurrentDeltaRate:    delta?.DeltaRate,
            DeltaTrend:          delta?.Trend,
            AnnualizedApr:       delta?.AnnualizedApr,
            NextFundingUtc:      delta?.NextFundingUtc,
            HitStopLoss:         hitStopLoss,
            SpatialExitAvailable: spatialExitAvailable,
            Reason:              reason,
            EvaluatedAt:         DateTime.UtcNow
        );

        _logger.LogDebug(
            "HoldDecision [{Sym} {Long}→{Short}]: {Action} | {Reason}",
            position.Symbol, position.LongExchange, position.ShortExchange,
            shouldHold ? "HOLD" : "CLOSE", reason);

        return decision;
    }

    /// <summary>
    /// Check if funding arb entry is justified (separate from spatial entry).
    /// Call before opening a new position.
    /// </summary>
    public FundingEntrySignal EvaluateEntry(
        string symbol,
        string longExchange,
        string shortExchange,
        decimal notionalUsd)
    {
        var opts  = _opts.Value;
        var delta = _funding.GetDelta(symbol, longExchange, shortExchange);

        if (delta == null)
            return new FundingEntrySignal(false, 0, 0, 0, "No funding data available");

        decimal takerFee    = GetTakerFee(longExchange, opts) + GetTakerFee(shortExchange, opts);
        decimal roundTripCost = takerFee / 100m * notionalUsd * 2m;   // entry + exit cost
        decimal breakevenDelta = notionalUsd > 0 ? roundTripCost / notionalUsd : 999m;

        // Need at least 2× breakeven to justify the position (recover entry+exit in ~2 periods)
        decimal minEntryDelta = breakevenDelta * 2m;

        // Require delta has been stable for trend check
        bool stableAndExpanding = delta.Trend == "expanding"
            && delta.Ema5 > breakevenDelta
            && delta.Ema20 > 0;

        bool justified = delta.DeltaRate >= minEntryDelta
            && delta.DeltaRate > 0
            && stableAndExpanding;

        string reason = justified
            ? $"ENTRY OK: delta {delta.DeltaRate * 100:+0.0000}% ≥ min {minEntryDelta * 100:0.0000}% | APR {delta.AnnualizedApr * 100:0.0}% | trend {delta.Trend}"
            : $"ENTRY SKIP: delta {delta.DeltaRate * 100:+0.0000}% < min {minEntryDelta * 100:0.0000}% OR trend {delta.Trend}";

        return new FundingEntrySignal(
            Justified:         justified,
            DeltaRate:         delta.DeltaRate,
            AnnualizedApr:     delta.AnnualizedApr,
            BreakevenDeltaRate: breakevenDelta,
            Reason:            reason
        );
    }

    private static decimal GetTakerFee(string exchange, ArbitrageOptions opts)
    {
        if (opts.EstimatedTakerFees.TryGetValue(exchange, out var fee)) return fee;
        return 0.06m;  // conservative default
    }
}

// ── Result records ────────────────────────────────────────────────────────────

public sealed record HoldDecision(
    bool     ShouldHold,
    decimal  AccumulatedFundingPnlUsd,
    decimal  ExpectedNextFundingUsd,
    decimal  ExitCostUsd,
    decimal  NetIfHold,
    decimal  NetIfExit,
    decimal  BreakevenDeltaRate,
    decimal? CurrentDeltaRate,
    string?  DeltaTrend,
    decimal? AnnualizedApr,
    DateTime? NextFundingUtc,
    bool     HitStopLoss,
    bool     SpatialExitAvailable,
    string   Reason,
    DateTime EvaluatedAt
);

public sealed record FundingEntrySignal(
    bool    Justified,
    decimal DeltaRate,
    decimal AnnualizedApr,
    decimal BreakevenDeltaRate,
    string  Reason
);
