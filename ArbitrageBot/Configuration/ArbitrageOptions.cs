namespace ArbitrageBot.Configuration;

public class ArbitrageOptions
{
    public const string SectionName = "Arbitrage";

    /// <summary>
    /// Static fallback / seed list. Used when DynamicSymbols=false or discovery fails.
    /// </summary>
    public List<string> Symbols { get; set; } = [];
    public List<string> Exchanges { get; set; } = [];

    public decimal MinProfitPercent { get; set; } = 0.15m;
    public int ScanIntervalMs { get; set; } = 1500;
    public bool PaperTrading { get; set; } = true;

    public Dictionary<string, decimal> EstimatedTakerFees { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Binance"] = 0.10m,
        ["Bybit"] = 0.10m,
        ["OKX"] = 0.10m,
        ["Bitget"] = 0.10m,
        ["GateIo"] = 0.10m
    };

    public decimal QuoteSize { get; set; } = 500m;
    public int MaxDepthLevels { get; set; } = 20;

    // Paper
    public bool PaperAutoExecute { get; set; } = true;
    public decimal PaperStartingQuote { get; set; } = 10_000m;
    public decimal PaperStartingBaseDefault { get; set; } = 0.05m;
    public Dictionary<string, decimal> PaperStartingBaseUnits { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BTC"] = 0.05m,
        ["ETH"] = 0.5m,
        ["SOL"] = 5m
    };
    public int PaperCooldownMs { get; set; } = 8000;
    public bool PaperRequireFullFill { get; set; } = true;

    // Dynamic symbol discovery
    /// <summary>If true, bot picks top liquid USDT pairs present on ALL exchanges.</summary>
    public bool DynamicSymbols { get; set; } = true;

    /// <summary>How many pairs to trade after ranking by volume.</summary>
    public int DynamicTopN { get; set; } = 8;

    /// <summary>Min median 24h quote volume (USDT) across exchanges.</summary>
    public decimal DynamicMinQuoteVolumeUsd { get; set; } = 5_000_000m;

    public string DynamicQuoteAsset { get; set; } = "USDT";

    /// <summary>Re-run discovery every N minutes (0 = only at startup).</summary>
    public int DynamicRefreshMinutes { get; set; } = 60;

    public IReadOnlyList<string> NormalizedSymbols =>
        Symbols.Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToUpperInvariant())
            .Distinct()
            .ToList();

    public IReadOnlyList<string> NormalizedExchanges =>
        Exchanges.Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
