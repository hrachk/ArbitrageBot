namespace ArbitrageBot.Configuration;

/// <summary>
/// Per-exchange API credentials. Secrets should live in User Secrets / env, not in git.
/// Section: "ExchangeCredentials"
/// </summary>
public class ExchangeCredentialsOptions
{
    public const string SectionName = "ExchangeCredentials";

    public Dictionary<string, ExchangeCredential> Exchanges { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class ExchangeCredential
{
    public bool Enabled { get; set; }
    public string? ApiKey { get; set; }
    public string? ApiSecret { get; set; }
    /// <summary>OKX / Bitget passphrase when required.</summary>
    public string? Passphrase { get; set; }
    /// <summary>read-only | trade (paper ignores trade).</summary>
    public string Permission { get; set; } = "read-only";
}

/// <summary>
/// Unified trading + live settings persisted to data/local-settings.json.
/// Survives rebuild/restart when that file is kept.
/// </summary>
public class TradingUiSettings
{
    public const string SectionName = "TradingUi";

    public string StrategyMode { get; set; } = "FuturesCross";
    public bool PaperTrading { get; set; } = true;
    public bool PaperAutoExecute { get; set; } = true;
    /// <summary>Min net edge % after fees, both books and slip. Same gate for paper and live.</summary>
    public decimal MinProfitPercent { get; set; } = 0.20m;
    /// <summary>Notional per leg (USDT). Paper + Live share this (LiveMaxNotional synced).</summary>
    public decimal QuoteSize { get; set; } = 250m;
    public decimal FuturesPaperLeverage { get; set; } = 3m;
    public int FuturesMaxOpenPositions { get; set; } = 2;
    public decimal FuturesStopLossUsd { get; set; } = -3m;
    public decimal FuturesDailyLossLimitUsd { get; set; } = -9m;
    /// <summary>0 = no soft hold timer (professional: exit on converge/TP/SL only).</summary>
    public int MaxHoldMinutes { get; set; } = 0;
    public decimal CloseBelowNetPercent { get; set; } = 0.05m;
    public decimal MaxMarginUsagePercent { get; set; } = 0.20m;
    public decimal MaxNotionalUsd { get; set; } = 250m;
    public int PaperCooldownMs { get; set; } = 20000;
    public bool PaperRequireFullFill { get; set; } = true;
    public bool RequireRoundTripEdge { get; set; } = true;
    public bool IncludeFunding { get; set; } = true;

    // ——— Live mirrors paper (one professional profile) ———
    public decimal LiveEquityPerExchangeUsd { get; set; } = 1000m;
    public decimal LiveMarginUsageFraction { get; set; } = 0.20m;
    public decimal LiveMaxNotionalUsd { get; set; } = 250m;
    public int LiveMaxOpenPositions { get; set; } = 2;
    public decimal LiveStopLossUsd { get; set; } = -3m;
    public decimal LiveDailyLossLimitUsd { get; set; } = -9m;

    public decimal MinGrossSpreadPercent { get; set; } = 0.55m;
    public decimal MinTakeProfitUsd { get; set; } = 1.50m;
    public int MinSpreadPersistMs { get; set; } = 1500;
    public int MaxBookAgeMs { get; set; } = 300;
    public int ScanIntervalMs { get; set; } = 250;
    public int FuturesMaxHoldSeconds { get; set; } = 0;
    public bool SpatialScalpMode { get; set; } = false;
    public bool RequireSpreadingEdge { get; set; } = true;
    public decimal PaperCloseFeeFactor { get; set; } = 1m;
    public decimal OpenEdgeBufferPercent { get; set; } = 0.08m;
    public bool RequireDepthFullFill { get; set; } = true;
    public decimal MinDepthScoreForUniverse { get; set; } = 1.0m;
    public int MaxLegsPerVenue { get; set; } = 2;
    public decimal MaxWidthExpansionPercent { get; set; } = 0.08m;
    public bool DynamicSymbols { get; set; } = true;
    public int DynamicTopN { get; set; } = 12;
    public decimal DynamicMinQuoteVolumeUsd { get; set; } = 15_000_000m;
    public decimal DynamicMaxQuoteVolumeUsd { get; set; } = 500_000_000m;
    public int DynamicRefreshMinutes { get; set; } = 15;
    public decimal PaperStartingQuote { get; set; } = 1000m;
}
