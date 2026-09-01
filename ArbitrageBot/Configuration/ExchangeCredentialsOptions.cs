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
    /// <summary>Min net open edge % after fees — same for PAPER and LIVE.</summary>
    public decimal MinProfitPercent { get; set; } = 0.10m;
    /// <summary>Notional per leg (USDT). Paper + Live share this (LiveMaxNotional synced).</summary>
    public decimal QuoteSize { get; set; } = 100m;
    public decimal FuturesPaperLeverage { get; set; } = 5m;
    public int FuturesMaxOpenPositions { get; set; } = 2;
    public decimal FuturesStopLossUsd { get; set; } = -12m;
    public decimal FuturesDailyLossLimitUsd { get; set; } = -40m;
    /// <summary>0 = no soft hold timer (professional: exit on converge/TP/SL only).</summary>
    public int MaxHoldMinutes { get; set; } = 0;
    public decimal CloseBelowNetPercent { get; set; } = 0.02m;
    public decimal MaxMarginUsagePercent { get; set; } = 0.35m;
    public decimal MaxNotionalUsd { get; set; } = 100m;
    public int PaperCooldownMs { get; set; } = 15000;
    public bool PaperRequireFullFill { get; set; } = true;
    public bool RequireRoundTripEdge { get; set; } = true;
    public bool IncludeFunding { get; set; } = true;

    // ——— Live mirrors paper (one professional profile) ———
    public decimal LiveEquityPerExchangeUsd { get; set; } = 2500m;
    public decimal LiveMarginUsageFraction { get; set; } = 0.35m;
    public decimal LiveMaxNotionalUsd { get; set; } = 100m;
    public int LiveMaxOpenPositions { get; set; } = 2;
    public decimal LiveStopLossUsd { get; set; } = -12m;
    public decimal LiveDailyLossLimitUsd { get; set; } = -40m;
}
