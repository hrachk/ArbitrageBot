namespace ArbitrageBot.Configuration;

public class ArbitrageOptions
{
    public const string SectionName = "Arbitrage";

    public List<string> Symbols { get; set; } = ["BTCUSDT", "ETHUSDT", "SOLUSDT"];

    /// <summary>Minimum net profit after fees + slippage (percent).</summary>
    public decimal MinProfitPercent { get; set; } = 0.15m;

    /// <summary>How often to recompute opportunities from live books (ms).</summary>
    public int ScanIntervalMs { get; set; } = 1500;

    public bool PaperTrading { get; set; } = true;

    public List<string> Exchanges { get; set; } = ["Binance", "Bybit", "OKX"];

    /// <summary>Estimated taker fee per exchange (percent).</summary>
    public Dictionary<string, decimal> EstimatedTakerFees { get; set; } = new()
    {
        ["Binance"] = 0.10m,
        ["Bybit"] = 0.10m,
        ["OKX"] = 0.10m,
        ["Bitget"] = 0.10m,
        ["GateIo"] = 0.10m
    };

    /// <summary>
    /// Notional size in quote currency (USDT) used for depth / slippage calculation.
    /// Opportunities are evaluated as if buying/selling this size.
    /// </summary>
    public decimal QuoteSize { get; set; } = 500m;

    /// <summary>Max levels to walk in the book when estimating fill.</summary>
    public int MaxDepthLevels { get; set; } = 20;
}
