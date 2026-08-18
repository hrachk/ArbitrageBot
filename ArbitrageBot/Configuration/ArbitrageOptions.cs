namespace ArbitrageBot.Configuration;

public class ArbitrageOptions
{
    public const string SectionName = "Arbitrage";

    /// <summary>
    /// Trading symbols to monitor (e.g. BTCUSDT, ETHUSDT)
    /// </summary>
    public List<string> Symbols { get; set; } = ["BTCUSDT", "ETHUSDT", "SOLUSDT"];

    /// <summary>
    /// Minimum net profit after fees (in percent). Example: 0.15 means 0.15%
    /// </summary>
    public decimal MinProfitPercent { get; set; } = 0.15m;

    /// <summary>
    /// How often to scan for opportunities (milliseconds)
    /// </summary>
    public int ScanIntervalMs { get; set; } = 2000;

    /// <summary>
    /// Paper trading mode - no real orders
    /// </summary>
    public bool PaperTrading { get; set; } = true;

    /// <summary>
    /// Exchanges to use (must match CryptoClients.Net names)
    /// </summary>
    public List<string> Exchanges { get; set; } = ["Binance", "Bybit", "OKX"];

    /// <summary>
    /// Estimated taker fee per exchange (percent). Used for profit calculation.
    /// </summary>
    public Dictionary<string, decimal> EstimatedTakerFees { get; set; } = new()
    {
        ["Binance"] = 0.10m,
        ["Bybit"] = 0.10m,
        ["OKX"] = 0.10m,
        ["Bitget"] = 0.10m,
        ["GateIo"] = 0.10m
    };
}
