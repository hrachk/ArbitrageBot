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

public class TradingUiSettings
{
    public const string SectionName = "TradingUi";

    public string StrategyMode { get; set; } = "FuturesCross";
    public bool PaperTrading { get; set; } = true;
    public bool PaperAutoExecute { get; set; } = true;
    public decimal MinProfitPercent { get; set; } = 0.12m;
    public decimal QuoteSize { get; set; } = 400m;
    public decimal FuturesPaperLeverage { get; set; } = 5m;
    public int FuturesMaxOpenPositions { get; set; } = 3;
    public decimal FuturesStopLossUsd { get; set; } = -30m;
    public decimal FuturesDailyLossLimitUsd { get; set; } = -100m;
}
