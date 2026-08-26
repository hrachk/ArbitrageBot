namespace ArbitrageBot.Configuration;

public class ArbitrageOptions
{
    public const string SectionName = "Arbitrage";

    /// <summary>SpotInventory | FuturesCross</summary>
    public string StrategyMode { get; set; } = "FuturesCross";

    public List<string> Symbols { get; set; } = [];
    public List<string> Exchanges { get; set; } = [];

    public decimal MinProfitPercent { get; set; } = 0.12m;
    public int ScanIntervalMs { get; set; } = 400;
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
    /// <summary>Meme / equity-perp / thin names that destroy spatial EV.</summary>
    public List<string> ExcludeToxicBases { get; set; } =
    [
        "TRUMP", "FARTCOIN", "PEPE", "BONK", "MEME", "WIF", "FLOKI", "BOME", "NEIRO",
        "SOXL", "SKHYNIX", "SAMSUNG", "SNXX", "KORU", "TSLA", "AAPL", "NVDA", "MSTR",
        "COIN", "HOOD", "MARA", "RIOT", "CL", "ZS", "CRCL"
    ];
    /// <summary>Min Binance depthScore (depthUsd/QuoteSize) to enter universe.</summary>
    public decimal MinDepthScoreForUniverse { get; set; } = 1.5m;
    /// <summary>Extra buffer on top of MinProfitPercent for open (bps as percent points).</summary>
    public decimal OpenEdgeBufferPercent { get; set; } = 0.02m;
    /// <summary>Optional upper volume cap to skip ultra-majors when ranking.</summary>
    public decimal DynamicMaxQuoteVolumeUsd { get; set; } = 800_000_000m;

    // Futures paper
    public decimal FuturesPaperLeverage { get; set; } = 5m;
    public int FuturesMaxOpenPositions { get; set; } = 3;
    public int FuturesMaxHoldMinutes { get; set; } = 5;
    /// <summary>Close hedge when current width (shortAsk-longBid)/longBid % falls to this or below.</summary>
    public decimal FuturesCloseBelowNetPercent { get; set; } = 0.02m;

    /// <summary>Max share of free margin on one exchange that a single new hedge may lock (0.25 = 25%).</summary>
    public decimal FuturesMaxMarginUsagePercent { get; set; } = 0.15m;
    /// <summary>Force-close if unrealized PnL on a hedge drops below this (USD, negative).</summary>
    public decimal FuturesStopLossUsd { get; set; } = -40m;
    /// <summary>Stop opening new hedges if day realized PnL is below this.</summary>
    public decimal FuturesDailyLossLimitUsd { get; set; } = -150m;
    /// <summary>Refuse open if notional would exceed this (USD) even with leverage.</summary>
    public decimal FuturesMaxNotionalUsd { get; set; } = 1500m;

    /// <summary>Include expected funding over N funding intervals (usually 8h each) in net filter.</summary>
    public bool FuturesIncludeFunding { get; set; } = true;
    /// <summary>How many funding periods to assume while holding (default 1 ≈ one 8h window).</summary>
    public int FuturesFundingPeriods { get; set; } = 1;
    /// <summary>Use round-trip (open+close) fees for entry threshold.</summary>
    public bool FuturesRequireRoundTripEdge { get; set; } = true;


    // ─── Live trading (OFF by default — paper remains default path) ───
    /// <summary>Master switch. Must stay false until paper equity results are validated.</summary>
    public bool LiveTradingEnabled { get; set; } = false;
    /// <summary>If true, only verify balances/positions via API — never place orders.</summary>
    public bool LiveReadOnlyMode { get; set; } = true;
    /// <summary>Hard ceiling: max concurrent live hedges.</summary>
    public int LiveMaxOpenPositions { get; set; } = 1;
    /// <summary>Max notional USD per leg on live.</summary>
    public decimal LiveMaxNotionalUsd { get; set; } = 200m;
    /// <summary>Daily realized loss limit (USD, negative). Hits → kill switch.</summary>
    public decimal LiveDailyLossLimitUsd { get; set; } = -50m;
    /// <summary>Per-hedge stop (USD, negative).</summary>
    public decimal LiveStopLossUsd { get; set; } = -25m;
    /// <summary>Require explicit confirmation phrase to enable live via API.</summary>
    public string LiveEnableConfirmPhrase { get; set; } = "ENABLE LIVE TRADING";
    /// <summary>Reject live open if book status is not healthy (Synced/book-ticker).</summary>
    public bool LiveRequireHealthyBooks { get; set; } = true;
    /// <summary>Min ms between any live order attempts (global + per venue).</summary>
    public int LiveMinOrderIntervalMs { get; set; } = 3000;
    /// <summary>Optional webhook (Telegram bot or Discord) on kill/enable/errors.</summary>
    public string? LiveAlertWebhookUrl { get; set; }
    /// <summary>Exchanges allowed for live orders (empty = all configured).</summary>
    public List<string> LiveAllowedExchanges { get; set; } = ["Binance", "Bybit", "OKX", "Bitget"];

    /// <summary>Opportunity must stay above min edge this many ms before open (anti-flash).</summary>
    public int MinSpreadPersistMs { get; set; } = 600;
    /// <summary>Ignore book quotes older than this (ms). 0 = disabled.</summary>
    public int MaxBookAgeMs { get; set; } = 400;
    /// <summary>Max open hedge legs touching the same venue (long or short side).</summary>
    public int MaxLegsPerVenue { get; set; } = 3;
    /// <summary>Skip open if current width already expanded vs entry estimate by this % (abs points).</summary>
    public decimal MaxWidthExpansionPercent { get; set; } = 0.25m;
    /// <summary>Require FullyFilled on both legs (also mirrored by PaperRequireFullFill).</summary>
    public bool RequireDepthFullFill { get; set; } = true;

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
