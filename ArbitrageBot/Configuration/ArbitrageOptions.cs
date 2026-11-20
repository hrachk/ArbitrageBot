namespace ArbitrageBot.Configuration;

public class ArbitrageOptions
{
    public const string SectionName = "Arbitrage";

    /// <summary>SpotInventory | FuturesCross</summary>
    public string StrategyMode { get; set; } = "FuturesCross";

    public List<string> Symbols { get; set; } = [];
    public List<string> Exchanges { get; set; } = [];

    public decimal MinProfitPercent { get; set; } = 0.02m;
    public int ScanIntervalMs { get; set; } = 200;
    public bool PaperTrading { get; set; } = true;

    public Dictionary<string, decimal> EstimatedTakerFees { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Binance"] = 0.05m,
        ["Bybit"] = 0.055m,
        ["OKX"] = 0.05m,
        ["Bitget"] = 0.06m,
        ["GateIo"] = 0.05m, 
        ["Kucoin"] = 0.06m
    };

    public decimal QuoteSize { get; set; } = 100m;
    public int MaxDepthLevels { get; set; } = 20;

    public bool PaperAutoExecute { get; set; } = true;
    public decimal PaperStartingQuote { get; set; } = 100m;
    public decimal PaperStartingBaseDefault { get; set; } = 0.05m;
    public Dictionary<string, decimal> PaperStartingBaseUnits { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BTC"] = 0.05m,
        ["ETH"] = 0.5m,
        ["SOL"] = 5m
    };
    public int PaperCooldownMs { get; set; } = 12000;
    public bool PaperRequireFullFill { get; set; } = true;

    public bool DynamicSymbols { get; set; } = true;
    public int DynamicTopN { get; set; } = 16;
    public decimal DynamicMinQuoteVolumeUsd { get; set; } = 8_000_000m;
    public string DynamicQuoteAsset { get; set; } = "USDT";
    public int DynamicRefreshMinutes { get; set; } = 15;
    /// <summary>Skip these base assets (majors with near-zero cross-exchange edge).</summary>
    public List<string> ExcludeMajorBases { get; set; } = ["BTC", "ETH", "BNB"];
    /// <summary>Meme / equity-perp / thin names that destroy spatial EV.</summary>
    public List<string> ExcludeToxicBases { get; set; } =
    [
        "TRUMP", "FARTCOIN", "PEPE", "BONK", "MEME", "WIF", "FLOKI", "BOME", "NEIRO",
        "SOXL", "SOXS", "QQQ", "SPCX", "XAG", "SKHYNIX", "SKHY", "SAMSUNG", "SNXX", "KORU", "TSLA", "AAPL", "NVDA", "MSTR",
        "COIN", "HOOD", "MARA", "RIOT", "CL", "ZS", "CRCL",
        "SPX", "WLFI", "MU", "SNDK", "CHIP", "MRVL", "INTC", "BEAT", "ONG", "O", "TUT", "DOS", "HOME", "1000PEPE", "1000BONK"
    ];
    /// <summary>Min Binance depthScore (depthUsd/QuoteSize) to enter universe.</summary>
    public decimal MinDepthScoreForUniverse { get; set; } = 0.8m;
    /// <summary>Extra buffer on top of MinProfitPercent for open (bps as percent points).</summary>
    public decimal OpenEdgeBufferPercent { get; set; } = 0.03m;
    /// <summary>Close on converge only if projected PnL ≥ this (else wait timeout/stop).</summary>
    public decimal MinTakeProfitUsd { get; set; } = 0.15m;
    /// <summary>Minimum gross width % at entry (must exceed ~4 taker fees + buffer).</summary>
    public decimal MinGrossSpreadPercent { get; set; } = 0.16m;
    /// <summary>True: short-hold spatial scalp (seconds), fast TP, net-open entry.</summary>
    public bool SpatialScalpMode { get; set; } = false;
    /// <summary>Max hold in seconds when SpatialScalpMode (soft clock — red positions are NOT flattened).</summary>
    public int FuturesMaxHoldSeconds { get; set; } = 0; // 0 = no soft timer
    /// <summary>
    /// After this many minutes, flatten even if red (inventory recycle). 0 = never force-red.
    /// Soft timeout never closes a losing hedge; only SL or this hard cap does.
    /// </summary>
    public int FuturesHardMaxHoldMinutes { get; set; } = 0; // 0 = never force-flat
    /// <summary>Close leg fees as fraction of taker (0.5 ≈ limit/maker exit).</summary>
    public decimal PaperCloseFeeFactor { get; set; } = 1.0m;
    /// <summary>Open only if gross is rising vs ~1s ago (avoid dying spreads).</summary>
    public bool RequireSpreadingEdge { get; set; } = true;
    /// <summary>Optional upper volume cap to skip ultra-majors when ranking.</summary>
    public decimal DynamicMaxQuoteVolumeUsd { get; set; } = 800_000_000m;

    // Futures paper
    public decimal FuturesPaperLeverage { get; set; } = 2m;
    public int FuturesMaxOpenPositions { get; set; } = 2;
    public int FuturesMaxHoldMinutes { get; set; } = 0;
    /// <summary>Close hedge when current width (shortAsk-longBid)/longBid % falls to this or below.</summary>
    public decimal FuturesCloseBelowNetPercent { get; set; } = 0.05m;

    /// <summary>Max share of free margin on one exchange that a single new hedge may lock (0.25 = 25%).</summary>
    public decimal FuturesMaxMarginUsagePercent { get; set; } = 0.50m;
    /// <summary>Force-close if unrealized PnL on a hedge drops below this (USD, negative).</summary>
    public decimal FuturesStopLossUsd { get; set; } = -2m;
    /// <summary>Stop opening new hedges if day realized PnL is below this.</summary>
    public decimal FuturesDailyLossLimitUsd { get; set; } = -6m;
    /// <summary>Refuse open if notional would exceed this (USD) even with leverage.</summary>
    public decimal FuturesMaxNotionalUsd { get; set; } = 100m;

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
    public int LiveMaxOpenPositions { get; set; } = 2;
    /// <summary>Max notional USD per leg on live (hard ceiling).</summary>
    public decimal LiveMaxNotionalUsd { get; set; } = 100m;
    /// <summary>
    /// Free USDT margin on EACH exchange. $100 deposit → notional = equity × leverage × usage, capped at LiveMaxNotionalUsd.
    /// </summary>
    public decimal LiveEquityPerExchangeUsd { get; set; } = 100m;
    /// <summary>Fraction of per-exchange equity locked as margin. 0.25 of $100 at 5x = $125, then capped by max notional.</summary>
    public decimal LiveMarginUsageFraction { get; set; } = 0.50m;
    /// <summary>Daily realized loss limit (USD, negative). Hits → kill switch.</summary>
    public decimal LiveDailyLossLimitUsd { get; set; } = -6m;
    /// <summary>Per-hedge stop (USD, negative).</summary>
    public decimal LiveStopLossUsd { get; set; } = -2m;
    /// <summary>Require explicit confirmation phrase to enable live via API.</summary>
    public string LiveEnableConfirmPhrase { get; set; } = "ENABLE LIVE TRADING";
    /// <summary>Reject live open if book status is not healthy (Synced/book-ticker).</summary>
    public bool LiveRequireHealthyBooks { get; set; } = true;
    /// <summary>Min ms between any live order attempts (global + per venue).</summary>
    public int LiveMinOrderIntervalMs { get; set; } = 3000;
    /// <summary>Optional webhook (Telegram bot or Discord) on kill/enable/errors.</summary>
    public string? LiveAlertWebhookUrl { get; set; }
    /// <summary>Exchanges allowed for live orders (empty = all configured).</summary>
    public List<string> LiveAllowedExchanges { get; set; } = ["Binance", "Bybit", "OKX", "Bitget", "GateIo", "Kucoin"];

    /// <summary>Opportunity must stay above min edge this many ms before open (anti-flash).</summary>
    public int MinSpreadPersistMs { get; set; } = 300;
    /// <summary>Ignore book quotes older than this (ms). 0 = disabled.</summary>
    public int MaxBookAgeMs { get; set; } = 1500;
    /// <summary>Max open hedge legs touching the same venue (long or short side).</summary>
    public int MaxLegsPerVenue { get; set; } = 2;
    /// <summary>Skip open if current width already expanded vs entry estimate by this % (abs points).</summary>
    public decimal MaxWidthExpansionPercent { get; set; } = 0.10m;
    /// <summary>Require FullyFilled on both legs (also mirrored by PaperRequireFullFill).</summary>
    public bool RequireDepthFullFill { get; set; } = true;

    // ---- Single-leg scalper (isolated from the hedge) ----
    /// <summary>Isolated scalp budget (USDT). Never shared with the hedge.</summary>
    public decimal ScalpDepositUsd { get; set; } = 50m;
    /// <summary>Scalp leverage, 1-5.</summary>
    public decimal ScalpLeverage { get; set; } = 5m;
    /// <summary>Open scalp positions at a time.</summary>
    public int ScalpMaxOpen { get; set; } = 1;
    /// <summary>Scalp stops opening for the UTC day after this loss (positive number).</summary>
    public decimal ScalpDailyLossUsd { get; set; } = 3m;
    /// <summary>Venue must lag its fair offset by at least this much (percent).</summary>
    public decimal ScalpEntryPercent { get; set; } = 0.25m;
    /// <summary>Expected catch-up after width and two taker fees (percent).</summary>
    public decimal ScalpMinNetPercent { get; set; } = 0.05m;
    /// <summary>Adverse price move that closes the scalp (percent).</summary>
    public decimal ScalpStopPercent { get; set; } = 0.30m;
    /// <summary>Time stop.</summary>
    public int ScalpMaxHoldSeconds { get; set; } = 45;
    /// <summary>Pause between scalp entries.</summary>
    public int ScalpCooldownSeconds { get; set; } = 20;
    /// <summary>Scalp with real orders. Needs live orders enabled as well; off by default.</summary>
    public bool ScalpLive { get; set; } = false;
    /// <summary>Venues the scalper may trade, comma separated (e.g. "Binance"). Empty = every venue.
    /// The lag is still measured against all venues. Live scalping also skips venues without trade keys.</summary>
    public string ScalpVenues { get; set; } = "";
    /// <summary>Scalp strategy: "lag" (venue lead-lag) or "density" (stand in front of order-book walls). Density is paper-only.</summary>
    public string ScalpStrategy { get; set; } = "lag";
    /// <summary>Wall: minimum resting notional at one price (USDT).</summary>
    public decimal DensityMinWallUsd { get; set; } = 20000m;
    /// <summary>Wall: at least this many times the median level within the scan range.</summary>
    public decimal DensityMinMultiple { get; set; } = 5m;
    /// <summary>Look for walls within this distance of mid (%).</summary>
    public decimal DensityScanPercent { get; set; } = 1.0m;
    /// <summary>Place the entry when price is this close to the wall (%).</summary>
    public decimal DensityApproachPercent { get; set; } = 0.30m;
    /// <summary>Wall must stand this long (anti-spoof) before we trade it.</summary>
    public int DensityMinWallAgeSeconds { get; set; } = 20;
    /// <summary>Take profit away from the wall (%), resting maker order.</summary>
    public decimal DensityTakeProfitPercent { get; set; } = 0.35m;
    /// <summary>Stop if price goes this far beyond the wall (%), or the wall is eaten/pulled.</summary>
    public decimal DensityStopBeyondPercent { get; set; } = 0.15m;
    /// <summary>Close at market after this long.</summary>
    public int DensityMaxHoldSeconds { get; set; } = 180;
    /// <summary>Maker fee per side (%).</summary>
    public decimal DensityMakerFeePercent { get; set; } = 0.02m;
    /// <summary>Taker fee per side (%), for stop and time exits.</summary>
    public decimal DensityTakerFeePercent { get; set; } = 0.05m;
    /// <summary>Order book levels kept for Binance books (walls further than 30 levels are invisible otherwise).</summary>
    public int DensityBookLevels { get; set; } = 500;

    // ---- Execution realism (audit 2026-11) ----
    /// <summary>A traded leg's quote must be younger than this (ms). REST snapshots are never a traded leg.</summary>
    public int TradeMaxQuoteAgeMs { get; set; } = 1000;
    /// <summary>Max age difference between the two traded legs (ms). Larger skew = latency, not a spread.</summary>
    public int MaxLegSkewMs { get; set; } = 600;
    /// <summary>Basis must have at least this many samples (~1 per 0.7 s) before a route can trade.</summary>
    public int BasisMinSamples { get; set; } = 60;
    /// <summary>Basis must span at least this many seconds of wall-clock before a route can trade.</summary>
    public int BasisMinSpanSeconds { get; set; } = 90;
    /// <summary>Paper: the order "arrives" this many ms after the signal and fills against that book.</summary>
    public int PaperFillLatencyMs { get; set; } = 250;
    /// <summary>IOC limit allowance beyond the scanned touch, in basis points (paper and live use the same).</summary>
    public decimal IocSlipBps { get; set; } = 4m;
    /// <summary>Funding settlements inside this horizon are charged against the edge.</summary>
    public int FundingHorizonMinutes { get; set; } = 20;
    /// <summary>Live: submit both legs concurrently (true) instead of long-then-short.</summary>
    public bool LiveConcurrentLegs { get; set; } = true;

    /// <summary>Leverage used for live orders (separate from paper simulation).</summary>
    public decimal LivePaperLeverage { get; set; } = 2m;

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

    public static string BaseAsset(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return "";
        var s = symbol.Trim().ToUpperInvariant()
            .Replace("-USDT-SWAP", "", StringComparison.Ordinal)
            .Replace("-USDT", "", StringComparison.Ordinal)
            .Replace("USDT", "", StringComparison.Ordinal);
        if (s.StartsWith("1000", StringComparison.Ordinal)) s = "1000" + s[4..];
        return s;
    }

    public bool IsExcludedSymbol(string? symbol)
    {
        var b = BaseAsset(symbol);
        if (string.IsNullOrEmpty(b)) return false;
        if (ExcludeMajorBases?.Any(x => string.Equals(x, b, StringComparison.OrdinalIgnoreCase)) == true)
            return true;
        if (ExcludeToxicBases?.Any(x => string.Equals(x, b, StringComparison.OrdinalIgnoreCase)) == true)
            return true;
        return b is "BTC" or "ETH" or "BNB";
    }
}
