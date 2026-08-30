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
    public decimal MinProfitPercent { get; set; } = 0.08m;
    /// <summary>Scan / paper quote size (USDT). Live size uses equity formula, not this.</summary>
    public decimal QuoteSize { get; set; } = 15m;
    public decimal FuturesPaperLeverage { get; set; } = 5m;
    public int FuturesMaxOpenPositions { get; set; } = 2;
    public decimal FuturesStopLossUsd { get; set; } = -2.5m;
    public decimal FuturesDailyLossLimitUsd { get; set; } = -8m;
    public int MaxHoldMinutes { get; set; } = 12;
    public decimal CloseBelowNetPercent { get; set; } = 0.02m;
    public decimal MaxMarginUsagePercent { get; set; } = 0.6m;
    public decimal MaxNotionalUsd { get; set; } = 15m;
    public int PaperCooldownMs { get; set; } = 12000;
    public bool PaperRequireFullFill { get; set; } = true;
    public bool RequireRoundTripEdge { get; set; } = true;
    public bool IncludeFunding { get; set; } = true;

    // ——— Live micro-account (default: ~$5 per exchange) ———
    /// <summary>Free USDT assumed on EACH exchange for margin.</summary>
    public decimal LiveEquityPerExchangeUsd { get; set; } = 5m;
    /// <summary>Share of equity used as margin (0.6 → $3 of $5).</summary>
    public decimal LiveMarginUsageFraction { get; set; } = 0.6m;
    /// <summary>Hard cap on live leg notional USDT.</summary>
    public decimal LiveMaxNotionalUsd { get; set; } = 15m;
    public int LiveMaxOpenPositions { get; set; } = 1;
    public decimal LiveStopLossUsd { get; set; } = -2.5m;
    public decimal LiveDailyLossLimitUsd { get; set; } = -8m;
}
