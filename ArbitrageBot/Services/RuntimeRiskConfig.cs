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
    // Snapshot is read several times per directed pair per scan. Cloning on every read
    // allocated thousands of option objects per second; cache it and rebuild on change.
    private ArbitrageOptions? _cached;

    public RuntimeRiskConfig(IOptions<ArbitrageOptions> options)
    {
        _opts = Clone(options.Value);
    }

    public ArbitrageOptions Snapshot
    {
        get { lock (_lock) return _cached ??= Clone(_opts); }
    }

    public void ApplyTrading(TradingUiSettings t)
    {
        lock (_lock)
        {
            _cached = null;
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

            if (t.MaxHoldMinutes >= 0) _opts.FuturesMaxHoldMinutes = t.MaxHoldMinutes;
            if (t.CloseBelowNetPercent >= 0) _opts.FuturesCloseBelowNetPercent = t.CloseBelowNetPercent;
            if (t.MaxMarginUsagePercent > 0)
                _opts.FuturesMaxMarginUsagePercent = Math.Clamp(t.MaxMarginUsagePercent, 0.05m, 0.9m);
            if (t.MaxNotionalUsd > 0) _opts.FuturesMaxNotionalUsd = t.MaxNotionalUsd;
            if (t.PaperCooldownMs >= 0) _opts.PaperCooldownMs = t.PaperCooldownMs;
            _opts.PaperRequireFullFill = t.PaperRequireFullFill;
            _opts.FuturesRequireRoundTripEdge = t.RequireRoundTripEdge;
            _opts.FuturesIncludeFunding = t.IncludeFunding;

            if (t.MinGrossSpreadPercent > 0) _opts.MinGrossSpreadPercent = t.MinGrossSpreadPercent;
            if (t.MinTakeProfitUsd > 0) _opts.MinTakeProfitUsd = t.MinTakeProfitUsd;
            if (t.MinSpreadPersistMs > 0) _opts.MinSpreadPersistMs = t.MinSpreadPersistMs;
            if (t.MaxBookAgeMs > 0) _opts.MaxBookAgeMs = t.MaxBookAgeMs;
            if (t.ScanIntervalMs >= 100) _opts.ScanIntervalMs = t.ScanIntervalMs;
            if (t.FuturesMaxHoldSeconds >= 0) _opts.FuturesMaxHoldSeconds = t.FuturesMaxHoldSeconds;
            _opts.SpatialScalpMode = t.SpatialScalpMode;
            _opts.RequireSpreadingEdge = t.RequireSpreadingEdge;
            if (t.PaperCloseFeeFactor > 0) _opts.PaperCloseFeeFactor = Math.Clamp(t.PaperCloseFeeFactor, 0.1m, 1m);
            if (t.OpenEdgeBufferPercent >= 0) _opts.OpenEdgeBufferPercent = t.OpenEdgeBufferPercent;
            _opts.RequireDepthFullFill = t.RequireDepthFullFill;
            if (t.MinDepthScoreForUniverse > 0) _opts.MinDepthScoreForUniverse = t.MinDepthScoreForUniverse;
            if (t.MaxLegsPerVenue > 0) _opts.MaxLegsPerVenue = t.MaxLegsPerVenue;
            if (t.MaxWidthExpansionPercent > 0) _opts.MaxWidthExpansionPercent = t.MaxWidthExpansionPercent;
            if (t.TradeMaxQuoteAgeMs > 0) _opts.TradeMaxQuoteAgeMs = Math.Clamp(t.TradeMaxQuoteAgeMs, 100, 10_000);
            if (t.MaxLegSkewMs > 0) _opts.MaxLegSkewMs = Math.Clamp(t.MaxLegSkewMs, 50, 10_000);
            if (t.BasisMinSamples > 0) _opts.BasisMinSamples = Math.Clamp(t.BasisMinSamples, 4, 5000);
            if (t.BasisMinSpanSeconds > 0) _opts.BasisMinSpanSeconds = Math.Clamp(t.BasisMinSpanSeconds, 1, 86_400);
            if (t.PaperFillLatencyMs >= 0) _opts.PaperFillLatencyMs = Math.Clamp(t.PaperFillLatencyMs, 0, 5000);
            if (t.IocSlipBps > 0) _opts.IocSlipBps = Math.Clamp(t.IocSlipBps, 0.5m, 50m);
            if (t.FundingHorizonMinutes > 0) _opts.FundingHorizonMinutes = Math.Clamp(t.FundingHorizonMinutes, 1, 1440);
            if (t.LiveConcurrentLegs is { } lcl) _opts.LiveConcurrentLegs = lcl;
            if (t.ScalpDepositUsd is { } sd && sd > 0) _opts.ScalpDepositUsd = Math.Clamp(sd, 5m, 100_000m);
            if (t.ScalpLeverage is { } sl && sl > 0) _opts.ScalpLeverage = Math.Clamp(sl, 1m, 5m);
            if (t.ScalpMaxOpen is { } smo && smo > 0) _opts.ScalpMaxOpen = Math.Clamp(smo, 1, 5);
            if (t.ScalpDailyLossUsd is { } sdl && sdl != 0) _opts.ScalpDailyLossUsd = Math.Abs(sdl);
            if (t.ScalpEntryPercent is { } se && se > 0) _opts.ScalpEntryPercent = Math.Clamp(se, 0.05m, 3m);
            if (t.ScalpMinNetPercent is { } smn && smn >= 0) _opts.ScalpMinNetPercent = Math.Clamp(smn, 0m, 2m);
            if (t.ScalpStopPercent is { } ss && ss > 0) _opts.ScalpStopPercent = Math.Clamp(ss, 0.05m, 5m);
            if (t.ScalpMaxHoldSeconds is { } sh && sh > 0) _opts.ScalpMaxHoldSeconds = Math.Clamp(sh, 5, 3600);
            if (t.ScalpCooldownSeconds is { } sc && sc >= 0) _opts.ScalpCooldownSeconds = Math.Clamp(sc, 0, 3600);
            if (t.ScalpLive is { } slv) _opts.ScalpLive = slv;
            if (t.ScalpStrategy != null) _opts.ScalpStrategy = t.ScalpStrategy.Trim().ToLowerInvariant() == "density" ? "density" : "lag";
            if (t.DensityMinWallUsd is { } dw && dw > 0) _opts.DensityMinWallUsd = Math.Clamp(dw, 500m, 50_000_000m);
            if (t.DensityMinMultiple is { } dm && dm > 0) _opts.DensityMinMultiple = Math.Clamp(dm, 2m, 200m);
            if (t.DensityScanPercent is { } ds && ds > 0) _opts.DensityScanPercent = Math.Clamp(ds, 0.1m, 5m);
            if (t.DensityApproachPercent is { } da && da > 0) _opts.DensityApproachPercent = Math.Clamp(da, 0.02m, 2m);
            if (t.DensityMinWallAgeSeconds is { } dg && dg >= 0) _opts.DensityMinWallAgeSeconds = Math.Clamp(dg, 0, 3600);
            if (t.DensityTakeProfitPercent is { } dt && dt > 0) _opts.DensityTakeProfitPercent = Math.Clamp(dt, 0.05m, 5m);
            if (t.DensityStopBeyondPercent is { } db && db > 0) _opts.DensityStopBeyondPercent = Math.Clamp(db, 0.02m, 3m);
            if (t.DensityMaxHoldSeconds is { } dh && dh > 0) _opts.DensityMaxHoldSeconds = Math.Clamp(dh, 10, 86_400);
            if (t.DensityMakerFeePercent is { } dmf && dmf >= 0) _opts.DensityMakerFeePercent = Math.Clamp(dmf, -0.05m, 0.2m);
            if (t.DensityTakerFeePercent is { } dtf && dtf >= 0) _opts.DensityTakerFeePercent = Math.Clamp(dtf, 0m, 0.3m);
            if (t.ScalpVenues != null) _opts.ScalpVenues = Venues.NormalizeCsv(t.ScalpVenues, out _);
            _opts.DynamicSymbols = t.DynamicSymbols;
            if (t.DynamicTopN > 0) _opts.DynamicTopN = t.DynamicTopN;
            if (t.DynamicMinQuoteVolumeUsd > 0) _opts.DynamicMinQuoteVolumeUsd = t.DynamicMinQuoteVolumeUsd;
            if (t.DynamicMaxQuoteVolumeUsd > 0) _opts.DynamicMaxQuoteVolumeUsd = t.DynamicMaxQuoteVolumeUsd;
            if (t.DynamicRefreshMinutes > 0) _opts.DynamicRefreshMinutes = t.DynamicRefreshMinutes;
            if (t.PaperStartingQuote > 0) _opts.PaperStartingQuote = t.PaperStartingQuote;

            // Unified profile: Live uses the same size/risk as Paper unless explicitly overridden.
            if (t.LiveEquityPerExchangeUsd > 0)
                _opts.LiveEquityPerExchangeUsd = t.LiveEquityPerExchangeUsd;
            if (t.LiveMarginUsageFraction > 0)
                _opts.LiveMarginUsageFraction = Math.Clamp(t.LiveMarginUsageFraction, 0.15m, 0.85m);
            else if (t.MaxMarginUsagePercent > 0)
                _opts.LiveMarginUsageFraction = Math.Clamp(t.MaxMarginUsagePercent, 0.15m, 0.85m);

            // One notional: QuoteSize / MaxNotional drives LiveMaxNotional
            var unifiedNotional = t.LiveMaxNotionalUsd > 0 ? t.LiveMaxNotionalUsd
                : (t.MaxNotionalUsd > 0 ? t.MaxNotionalUsd : t.QuoteSize);
            if (unifiedNotional > 0)
                _opts.LiveMaxNotionalUsd = unifiedNotional;
            if (t.QuoteSize > 0 && _opts.FuturesMaxNotionalUsd <= 0)
                _opts.FuturesMaxNotionalUsd = t.QuoteSize;
            if (t.QuoteSize > 0)
                _opts.FuturesMaxNotionalUsd = t.MaxNotionalUsd > 0 ? t.MaxNotionalUsd : t.QuoteSize;

            var unifiedOpen = t.LiveMaxOpenPositions > 0 ? t.LiveMaxOpenPositions
                : (t.FuturesMaxOpenPositions > 0 ? t.FuturesMaxOpenPositions : 2);
            _opts.LiveMaxOpenPositions = unifiedOpen;

            _opts.LiveStopLossUsd = t.LiveStopLossUsd != 0 ? t.LiveStopLossUsd : t.FuturesStopLossUsd;
            _opts.LiveDailyLossLimitUsd = t.LiveDailyLossLimitUsd != 0 ? t.LiveDailyLossLimitUsd : t.FuturesDailyLossLimitUsd;

            // MaxHoldMinutes 0 = no minute-based timer (seconds/scalp may still apply)
            if (t.MaxHoldMinutes == 0)
                _opts.FuturesMaxHoldMinutes = 0;
            // Only clear seconds when explicitly sent as 0 AND scalp off
            if (t.FuturesMaxHoldSeconds == 0 && !t.SpatialScalpMode && t.MaxHoldMinutes == 0)
            {
                // keep existing seconds unless caller set SpatialScalpMode false and seconds 0 intentionally
            }
        }
    }

    public void ApplyRisk(RiskUiSettings r)
    {
        lock (_lock)
        {
            _cached = null;
            if (r.MaxHoldMinutes > 0) _opts.FuturesMaxHoldMinutes = r.MaxHoldMinutes;
            if (r.CloseBelowNetPercent >= 0) _opts.FuturesCloseBelowNetPercent = r.CloseBelowNetPercent;
            if (r.MaxMarginUsagePercent > 0)
                _opts.FuturesMaxMarginUsagePercent = Math.Clamp(r.MaxMarginUsagePercent, 0.05m, 0.9m);
            if (r.MaxNotionalUsd > 0) _opts.FuturesMaxNotionalUsd = r.MaxNotionalUsd;
            if (r.PaperCooldownMs >= 0) _opts.PaperCooldownMs = r.PaperCooldownMs;
            _opts.PaperRequireFullFill = r.PaperRequireFullFill;
            _opts.FuturesRequireRoundTripEdge = r.RequireRoundTripEdge;
            _opts.FuturesIncludeFunding = r.IncludeFunding;
            if (r.MinGrossSpreadPercent > 0) _opts.MinGrossSpreadPercent = r.MinGrossSpreadPercent;
            if (r.MinTakeProfitUsd > 0) _opts.MinTakeProfitUsd = r.MinTakeProfitUsd;
            if (r.MinSpreadPersistMs > 0) _opts.MinSpreadPersistMs = r.MinSpreadPersistMs;
            if (r.MaxBookAgeMs > 0) _opts.MaxBookAgeMs = r.MaxBookAgeMs;
            if (r.ScanIntervalMs >= 100) _opts.ScanIntervalMs = r.ScanIntervalMs;
            if (r.FuturesMaxHoldSeconds >= 0) _opts.FuturesMaxHoldSeconds = r.FuturesMaxHoldSeconds;
            _opts.SpatialScalpMode = r.SpatialScalpMode;
            _opts.RequireSpreadingEdge = r.RequireSpreadingEdge;
            if (r.PaperCloseFeeFactor > 0) _opts.PaperCloseFeeFactor = Math.Clamp(r.PaperCloseFeeFactor, 0.1m, 1m);
            if (r.OpenEdgeBufferPercent >= 0) _opts.OpenEdgeBufferPercent = r.OpenEdgeBufferPercent;
            _opts.RequireDepthFullFill = r.RequireDepthFullFill;
            if (r.MinDepthScoreForUniverse > 0) _opts.MinDepthScoreForUniverse = r.MinDepthScoreForUniverse;
            if (r.MaxLegsPerVenue > 0) _opts.MaxLegsPerVenue = r.MaxLegsPerVenue;
            if (r.MaxWidthExpansionPercent > 0) _opts.MaxWidthExpansionPercent = r.MaxWidthExpansionPercent;
            _opts.DynamicSymbols = r.DynamicSymbols;
            if (r.DynamicTopN > 0) _opts.DynamicTopN = r.DynamicTopN;
            if (r.DynamicMinQuoteVolumeUsd > 0) _opts.DynamicMinQuoteVolumeUsd = r.DynamicMinQuoteVolumeUsd;
            if (r.DynamicMaxQuoteVolumeUsd > 0) _opts.DynamicMaxQuoteVolumeUsd = r.DynamicMaxQuoteVolumeUsd;
            if (r.DynamicRefreshMinutes > 0) _opts.DynamicRefreshMinutes = r.DynamicRefreshMinutes;
            if (r.PaperStartingQuote > 0) _opts.PaperStartingQuote = r.PaperStartingQuote;
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
        TradeMaxQuoteAgeMs = o.TradeMaxQuoteAgeMs,
        MaxLegSkewMs = o.MaxLegSkewMs,
        BasisMinSamples = o.BasisMinSamples,
        BasisMinSpanSeconds = o.BasisMinSpanSeconds,
        PaperFillLatencyMs = o.PaperFillLatencyMs,
        IocSlipBps = o.IocSlipBps,
        FundingHorizonMinutes = o.FundingHorizonMinutes,
        LiveConcurrentLegs = o.LiveConcurrentLegs,
        ScalpDepositUsd = o.ScalpDepositUsd,
        ScalpLeverage = o.ScalpLeverage,
        ScalpMaxOpen = o.ScalpMaxOpen,
        ScalpDailyLossUsd = o.ScalpDailyLossUsd,
        ScalpEntryPercent = o.ScalpEntryPercent,
        ScalpMinNetPercent = o.ScalpMinNetPercent,
        ScalpStopPercent = o.ScalpStopPercent,
        ScalpMaxHoldSeconds = o.ScalpMaxHoldSeconds,
        ScalpCooldownSeconds = o.ScalpCooldownSeconds,
        ScalpLive = o.ScalpLive,
        ScalpVenues = o.ScalpVenues,
        ScalpStrategy = o.ScalpStrategy,
        DensityMinWallUsd = o.DensityMinWallUsd,
        DensityMinMultiple = o.DensityMinMultiple,
        DensityScanPercent = o.DensityScanPercent,
        DensityApproachPercent = o.DensityApproachPercent,
        DensityMinWallAgeSeconds = o.DensityMinWallAgeSeconds,
        DensityTakeProfitPercent = o.DensityTakeProfitPercent,
        DensityStopBeyondPercent = o.DensityStopBeyondPercent,
        DensityMaxHoldSeconds = o.DensityMaxHoldSeconds,
        DensityMakerFeePercent = o.DensityMakerFeePercent,
        DensityTakerFeePercent = o.DensityTakerFeePercent,
        DensityBookLevels = o.DensityBookLevels,
        LivePaperLeverage = o.LivePaperLeverage,
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
    public decimal MinGrossSpreadPercent { get; set; }
    public decimal MinTakeProfitUsd { get; set; }
    public int MinSpreadPersistMs { get; set; }
    public int MaxBookAgeMs { get; set; }
    public int ScanIntervalMs { get; set; }
    public int FuturesMaxHoldSeconds { get; set; }
    public bool SpatialScalpMode { get; set; }
    public bool RequireSpreadingEdge { get; set; }
    public decimal PaperCloseFeeFactor { get; set; }
    public decimal OpenEdgeBufferPercent { get; set; }
    public bool RequireDepthFullFill { get; set; } = true;
    public decimal MinDepthScoreForUniverse { get; set; }
    public int MaxLegsPerVenue { get; set; }
    public decimal MaxWidthExpansionPercent { get; set; }
    public bool DynamicSymbols { get; set; } = true;
    public int DynamicTopN { get; set; }
    public decimal DynamicMinQuoteVolumeUsd { get; set; }
    public decimal DynamicMaxQuoteVolumeUsd { get; set; }
    public int DynamicRefreshMinutes { get; set; }
    public decimal PaperStartingQuote { get; set; }
}
