namespace ArbitrageBot.Configuration;

public class ArbitrageOptions
{
    public const string SectionName = "Arbitrage";

    // Empty defaults — values come from appsettings (avoids list double-bind with property initializers)
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

    /// <summary>Notional size in quote (USDT) for depth / slippage.</summary>
    public decimal QuoteSize { get; set; } = 500m;

    public int MaxDepthLevels { get; set; } = 20;

    /// <summary>Normalized unique symbols (uppercase).</summary>
    public IReadOnlyList<string> NormalizedSymbols =>
        Symbols.Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToUpperInvariant())
            .Distinct()
            .ToList();

    /// <summary>Normalized unique exchange names.</summary>
    public IReadOnlyList<string> NormalizedExchanges =>
        Exchanges.Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
