namespace ArbitrageBot.Configuration;

public class ArbitrageOptions
{
    public const string SectionName = "Arbitrage";

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

    // --- Paper execution ---
    /// <summary>Auto-execute paper trades when opportunity passes filters.</summary>
    public bool PaperAutoExecute { get; set; } = true;

    /// <summary>Starting USDT balance on each exchange.</summary>
    public decimal PaperStartingQuote { get; set; } = 10_000m;

    /// <summary>Default starting base units per asset if not in PaperStartingBaseUnits (e.g. 0.05 BTC).</summary>
    public decimal PaperStartingBaseDefault { get; set; } = 0.05m;

    /// <summary>Per-asset starting base inventory, e.g. BTC: 0.1, ETH: 1.</summary>
    public Dictionary<string, decimal> PaperStartingBaseUnits { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BTC"] = 0.05m,
        ["ETH"] = 0.5m,
        ["SOL"] = 5m
    };

    /// <summary>Min ms between successful paper trades.</summary>
    public int PaperCooldownMs { get; set; } = 8000;

    /// <summary>Only execute full-fill opportunities in paper mode.</summary>
    public bool PaperRequireFullFill { get; set; } = true;

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
