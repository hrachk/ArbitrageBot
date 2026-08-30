using ArbitrageBot.Configuration;
using Microsoft.Extensions.Options;

namespace ArbitrageBot.Services;

/// <summary>
/// Mutable risk/trading params applied at runtime (Settings UI) without full process restart.
/// Seeded from appsettings; paper engine and worker read from here.
/// </summary>
public sealed class RuntimeRiskConfig
{
    private readonly object _lock = new();
    private ArbitrageOptions _opts;

    public RuntimeRiskConfig(IOptions<ArbitrageOptions> options)
    {
        _opts = Clone(options.Value);
    }

    public ArbitrageOptions Snapshot
    {
        get { lock (_lock) return Clone(_opts); }
    }

    public void ApplyTrading(TradingUiSettings t)
    {
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(t.StrategyMode))
                _opts.StrategyMode = t.StrategyMode;
            _opts.PaperTrading = t.PaperTrading;
            _opts.PaperAutoExecute = t.PaperAutoExecute;
            if (t.MinProfitPercent > 0) _opts.MinProfitPercent = t.MinProfitPercent;
            if (t.QuoteSize > 0) _opts.QuoteSize = t.QuoteSize;
            if (t.FuturesPaperLeverage > 0)
                _opts.FuturesPaperLeverage = Math.Clamp(t.FuturesPaperLeverage, 1, 10);
            if (t.FuturesMaxOpenPositions > 0)
                _opts.FuturesMaxOpenPositions = t.FuturesMaxOpenPositions;
            _opts.FuturesStopLossUsd = t.FuturesStopLossUsd;
            _opts.FuturesDailyLossLimitUsd = t.FuturesDailyLossLimitUsd;

            if (t.MaxHoldMinutes > 0) _opts.FuturesMaxHoldMinutes = t.MaxHoldMinutes;
            if (t.CloseBelowNetPercent >= 0) _opts.FuturesCloseBelowNetPercent = t.CloseBelowNetPercent;
            if (t.MaxMarginUsagePercent > 0)
                _opts.FuturesMaxMarginUsagePercent = Math.Clamp(t.MaxMarginUsagePercent, 0.05m, 0.9m);
            if (t.MaxNotionalUsd > 0) _opts.FuturesMaxNotionalUsd = t.MaxNotionalUsd;
            if (t.PaperCooldownMs >= 0) _opts.PaperCooldownMs = t.PaperCooldownMs;
            _opts.PaperRequireFullFill = t.PaperRequireFullFill;
            _opts.FuturesRequireRoundTripEdge = t.RequireRoundTripEdge;
            _opts.FuturesIncludeFunding = t.IncludeFunding;

            // Live micro-account — NEVER overwrite LiveMaxNotional with QuoteSize
            if (t.LiveEquityPerExchangeUsd > 0)
                _opts.LiveEquityPerExchangeUsd = t.LiveEquityPerExchangeUsd;
            if (t.LiveMarginUsageFraction > 0)
                _opts.LiveMarginUsageFraction = Math.Clamp(t.LiveMarginUsageFraction, 0.2m, 0.85m);
            if (t.LiveMaxNotionalUsd > 0)
                _opts.LiveMaxNotionalUsd = t.LiveMaxNotionalUsd;
            if (t.LiveMaxOpenPositions > 0)
                _opts.LiveMaxOpenPositions = t.LiveMaxOpenPositions;
            if (t.LiveStopLossUsd != 0)
                _opts.LiveStopLossUsd = t.LiveStopLossUsd;
            if (t.LiveDailyLossLimitUsd != 0)
                _opts.LiveDailyLossLimitUsd = t.LiveDailyLossLimitUsd;
        }
    }

    public void ApplyRisk(RiskUiSettings r)
    {
        lock (_lock)
        {
            if (r.MaxHoldMinutes > 0) _opts.FuturesMaxHoldMinutes = r.MaxHoldMinutes;
            if (r.CloseBelowNetPercent >= 0) _opts.FuturesCloseBelowNetPercent = r.CloseBelowNetPercent;
            if (r.MaxMarginUsagePercent > 0)
                _opts.FuturesMaxMarginUsagePercent = Math.Clamp(r.MaxMarginUsagePercent, 0.05m, 0.9m);
            if (r.MaxNotionalUsd > 0) _opts.FuturesMaxNotionalUsd = r.MaxNotionalUsd;
            if (r.PaperCooldownMs >= 0) _opts.PaperCooldownMs = r.PaperCooldownMs;
            _opts.PaperRequireFullFill = r.PaperRequireFullFill;
            _opts.FuturesRequireRoundTripEdge = r.RequireRoundTripEdge;
            _opts.FuturesIncludeFunding = r.IncludeFunding;
            if (r.StopLossUsd != 0) _opts.FuturesStopLossUsd = r.StopLossUsd;
            if (r.DailyLossLimitUsd != 0) _opts.FuturesDailyLossLimitUsd = r.DailyLossLimitUsd;
            if (r.MinProfitPercent > 0) _opts.MinProfitPercent = r.MinProfitPercent;
            if (r.QuoteSize > 0) _opts.QuoteSize = r.QuoteSize;
            if (r.Leverage > 0) _opts.FuturesPaperLeverage = Math.Clamp(r.Leverage, 1, 10);
            if (r.MaxOpenPositions > 0) _opts.FuturesMaxOpenPositions = r.MaxOpenPositions;
            if (r.LiveEquityPerExchangeUsd > 0) _opts.LiveEquityPerExchangeUsd = r.LiveEquityPerExchangeUsd;
            if (r.LiveMarginUsageFraction > 0)
                _opts.LiveMarginUsageFraction = Math.Clamp(r.LiveMarginUsageFraction, 0.2m, 0.85m);
            if (r.LiveMaxNotionalUsd > 0) _opts.LiveMaxNotionalUsd = r.LiveMaxNotionalUsd;
            if (r.LiveMaxOpenPositions > 0) _opts.LiveMaxOpenPositions = r.LiveMaxOpenPositions;
            if (r.LiveStopLossUsd != 0) _opts.LiveStopLossUsd = r.LiveStopLossUsd;
        }
    }

    private static ArbitrageOptions Clone(ArbitrageOptions o) => new()
    {
        StrategyMode = o.StrategyMode,
        PaperTrading = o.PaperTrading,
        PaperAutoExecute = o.PaperAutoExecute,
        Symbols = o.Symbols?.ToList() ?? [],
        Exchanges = o.Exchanges?.ToList() ?? [],
        MinProfitPercent = o.MinProfitPercent,
        QuoteSize = o.QuoteSize,
        ScanIntervalMs = o.ScanIntervalMs,
        MaxDepthLevels = o.MaxDepthLevels,
        PaperRequireFullFill = o.PaperRequireFullFill,
        PaperCooldownMs = o.PaperCooldownMs,
        PaperStartingQuote = o.PaperStartingQuote,
        EstimatedTakerFees = new Dictionary<string, decimal>(o.EstimatedTakerFees, StringComparer.OrdinalIgnoreCase),
        DynamicSymbols = o.DynamicSymbols,
        DynamicTopN = o.DynamicTopN,
        DynamicMinQuoteVolumeUsd = o.DynamicMinQuoteVolumeUsd,
        DynamicMaxQuoteVolumeUsd = o.DynamicMaxQuoteVolumeUsd,
        DynamicQuoteAsset = o.DynamicQuoteAsset,
        DynamicRefreshMinutes = o.DynamicRefreshMinutes,
        ExcludeMajorBases = o.ExcludeMajorBases?.ToList() ?? [],
        FuturesPaperLeverage = o.FuturesPaperLeverage,
        FuturesMaxOpenPositions = o.FuturesMaxOpenPositions,
        FuturesMaxHoldMinutes = o.FuturesMaxHoldMinutes,
        FuturesCloseBelowNetPercent = o.FuturesCloseBelowNetPercent,
        FuturesIncludeFunding = o.FuturesIncludeFunding,
        FuturesFundingPeriods = o.FuturesFundingPeriods,
        FuturesRequireRoundTripEdge = o.FuturesRequireRoundTripEdge,
        FuturesMaxMarginUsagePercent = o.FuturesMaxMarginUsagePercent,
        FuturesStopLossUsd = o.FuturesStopLossUsd,
        FuturesDailyLossLimitUsd = o.FuturesDailyLossLimitUsd,
        FuturesMaxNotionalUsd = o.FuturesMaxNotionalUsd,
        MinSpreadPersistMs = o.MinSpreadPersistMs,
        MaxBookAgeMs = o.MaxBookAgeMs,
        MaxLegsPerVenue = o.MaxLegsPerVenue,
        MaxWidthExpansionPercent = o.MaxWidthExpansionPercent,
        RequireDepthFullFill = o.RequireDepthFullFill,
        MinDepthScoreForUniverse = o.MinDepthScoreForUniverse,
        OpenEdgeBufferPercent = o.OpenEdgeBufferPercent,
        MinTakeProfitUsd = o.MinTakeProfitUsd,
        MinGrossSpreadPercent = o.MinGrossSpreadPercent,
        SpatialScalpMode = o.SpatialScalpMode,
        FuturesMaxHoldSeconds = o.FuturesMaxHoldSeconds,
        FuturesHardMaxHoldMinutes = o.FuturesHardMaxHoldMinutes,
        PaperCloseFeeFactor = o.PaperCloseFeeFactor,
        RequireSpreadingEdge = o.RequireSpreadingEdge,
        ExcludeToxicBases = o.ExcludeToxicBases?.ToList() ?? [],
        LiveTradingEnabled = o.LiveTradingEnabled,
        LiveReadOnlyMode = o.LiveReadOnlyMode,
        LiveMaxOpenPositions = o.LiveMaxOpenPositions,
        LiveMaxNotionalUsd = o.LiveMaxNotionalUsd,
        LiveEquityPerExchangeUsd = o.LiveEquityPerExchangeUsd,
        LiveMarginUsageFraction = o.LiveMarginUsageFraction,
        LiveDailyLossLimitUsd = o.LiveDailyLossLimitUsd,
        LiveStopLossUsd = o.LiveStopLossUsd,
        LiveEnableConfirmPhrase = o.LiveEnableConfirmPhrase,
        LiveRequireHealthyBooks = o.LiveRequireHealthyBooks,
        LiveMinOrderIntervalMs = o.LiveMinOrderIntervalMs,
        LiveAlertWebhookUrl = o.LiveAlertWebhookUrl,
        LiveAllowedExchanges = o.LiveAllowedExchanges?.ToList() ?? []
    };
}

public class RiskUiSettings
{
    public decimal MinProfitPercent { get; set; }
    public decimal QuoteSize { get; set; }
    public decimal Leverage { get; set; }
    public int MaxOpenPositions { get; set; }
    public int MaxHoldMinutes { get; set; }
    public decimal CloseBelowNetPercent { get; set; }
    public decimal MaxMarginUsagePercent { get; set; }
    public decimal MaxNotionalUsd { get; set; }
    public decimal StopLossUsd { get; set; }
    public decimal DailyLossLimitUsd { get; set; }
    public int PaperCooldownMs { get; set; }
    public bool PaperRequireFullFill { get; set; }
    public bool RequireRoundTripEdge { get; set; }
    public bool IncludeFunding { get; set; }
    public decimal LiveEquityPerExchangeUsd { get; set; }
    public decimal LiveMarginUsageFraction { get; set; }
    public decimal LiveMaxNotionalUsd { get; set; }
    public int LiveMaxOpenPositions { get; set; }
    public decimal LiveStopLossUsd { get; set; }
}
