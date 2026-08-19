namespace ArbitrageBot.Configuration;

public class ArbitrageOptions
{
    public const string SectionName = "Arbitrage";

    /// <summary>SpotInventory | FuturesCross</summary>
    public string StrategyMode { get; set; } = "FuturesCross";

    public List<string> Symbols { get; set; } = [];
    public List<string> Exchanges { get; set; } = [];

    public decimal MinProfitPercent { get; set; } = 0.08m;
    public int ScanIntervalMs { get; set; } = 1500;
    public bool PaperTrading { get; set; } = true;

    public Dictionary<string, decimal> EstimatedTakerFees { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Binance"] = 0.05m,
        ["Bybit"] = 0.055m,
        ["OKX"] = 0.05m,
        ["Bitget"] = 0.06m,
        ["GateIo"] = 0.05m
    };

    public decimal QuoteSize { get; set; } = 500m;
    public int MaxDepthLevels { get; set; } = 20;

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

    public bool DynamicSymbols { get; set; } = true;
    public int DynamicTopN { get; set; } = 6;
    public decimal DynamicMinQuoteVolumeUsd { get; set; } = 10_000_000m;
    public string DynamicQuoteAsset { get; set; } = "USDT";
    public int DynamicRefreshMinutes { get; set; } = 60;
    /// <summary>Skip these base assets (majors with near-zero cross-exchange edge).</summary>
    public List<string> ExcludeMajorBases { get; set; } = ["BTC", "ETH", "BNB"];
    /// <summary>Optional upper volume cap to skip ultra-majors when ranking.</summary>
    public decimal DynamicMaxQuoteVolumeUsd { get; set; } = 800_000_000m;

    // Futures paper
    public decimal FuturesPaperLeverage { get; set; } = 5m;
    public int FuturesMaxOpenPositions { get; set; } = 3;
    public int FuturesMaxHoldMinutes { get; set; } = 30;
    /// <summary>Close hedge when current width (shortAsk-longBid)/longBid % falls to this or below.</summary>
    public decimal FuturesCloseBelowNetPercent { get; set; } = 0.02m;

    /// <summary>Max share of free margin on one exchange that a single new hedge may lock (0.25 = 25%).</summary>
    public decimal FuturesMaxMarginUsagePercent { get; set; } = 0.25m;
    /// <summary>Force-close if unrealized PnL on a hedge drops below this (USD, negative).</summary>
    public decimal FuturesStopLossUsd { get; set; } = -40m;
    /// <summary>Stop opening new hedges if day realized PnL is below this.</summary>
    public decimal FuturesDailyLossLimitUsd { get; set; } = -150m;
    /// <summary>Refuse open if notional would exceed this (USD) even with leverage.</summary>
    public decimal FuturesMaxNotionalUsd { get; set; } = 2500m;

    /// <summary>Include expected funding over N funding intervals (usually 8h each) in net filter.</summary>
    public bool FuturesIncludeFunding { get; set; } = true;
    /// <summary>How many funding periods to assume while holding (default 1 ≈ one 8h window).</summary>
    public int FuturesFundingPeriods { get; set; } = 1;
    /// <summary>Use round-trip (open+close) fees for entry threshold.</summary>
    public bool FuturesRequireRoundTripEdge { get; set; } = true;

    public bool IsFuturesCross =>
        string.Equals(StrategyMode, "FuturesCross", StringComparison.OrdinalIgnoreCase);

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
