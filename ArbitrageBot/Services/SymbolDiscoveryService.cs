using System.Net.Http.Json;
using System.Text.Json;
using ArbitrageBot.Configuration;
using CryptoClients.Net.Interfaces;
using CryptoExchange.Net.SharedApis;
using Microsoft.Extensions.Options;

namespace ArbitrageBot.Services;

/// <summary>
/// Picks USDT-M perps suited for cross-exchange spatial arb:
/// multi-venue presence, mid liquidity (not BTC-tight, not illiquid junk).
/// Primary path: public HTTP tickers (Binance/Bybit/OKX). Fallback: CryptoClients + curated rotate.
/// </summary>
public class SymbolDiscoveryService : ISymbolDiscoveryService
{
    private readonly IExchangeRestClient _rest;
    private readonly ArbitrageOptions _options;
    private readonly ILogger<SymbolDiscoveryService> _logger;
    private readonly HttpClient _http;

    private static readonly HashSet<string> HardExclude = new(StringComparer.OrdinalIgnoreCase)
    {
        "BTC", "ETH", "BNB", "USDC", "FDUSD", "TUSD", "DAI", "EUR", "BUSD"
    };

    /// <summary>Stable liquid names that usually list on 3+ venues — used only if HTTP fails.</summary>
    private static readonly string[] ArbFriendlyPool =
    [
        "SOLUSDT", "XRPUSDT", "DOGEUSDT", "ADAUSDT", "AVAXUSDT", "LINKUSDT", "NEARUSDT",
        "SUIUSDT", "ARBUSDT", "OPUSDT", "APTUSDT", "INJUSDT", "SEIUSDT", "TIAUSDT",
        "WIFUSDT", "PEPEUSDT", "FILUSDT", "ATOMUSDT", "LTCUSDT", "DOTUSDT", "TONUSDT",
        "RENDERUSDT", "FETUSDT", "AAVEUSDT", "UNIUSDT", "ENAUSDT", "JUPUSDT", "WLDUSDT",
        "STRKUSDT", "ORDIUSDT", "STXUSDT", "IMXUSDT", "GRTUSDT", "SANDUSDT", "MANAUSDT",
        "CRVUSDT", "MKRUSDT", "LDOUSDT", "RUNEUSDT", "CFXUSDT", "TRXUSDT", "BCHUSDT",
        "1000PEPEUSDT", "1000BONKUSDT", "ORDIUSDT", "PYTHUSDT", "JTOUSDT", "MEMEUSDT"
    ];

    public SymbolDiscoveryService(
        IExchangeRestClient rest,
        IOptions<ArbitrageOptions> options,
        ILogger<SymbolDiscoveryService> logger,
        IHttpClientFactory? httpFactory = null)
    {
        _rest = rest;
        _options = options.Value;
        _logger = logger;
        _http = httpFactory?.CreateClient("discovery") ?? new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "ArbitrageBot/1.0");
    }

    private HashSet<string> ExcludedBases
    {
        get
        {
            var set = new HashSet<string>(HardExclude, StringComparer.OrdinalIgnoreCase);
            foreach (var b in _options.ExcludeMajorBases ?? [])
                set.Add(b);
            return set;
        }
    }

    public async Task<DiscoveryResult> DiscoverAsync(
        IReadOnlyList<string> exchanges,
        CancellationToken ct = default)
    {
        ExchangeParameters.SetStaticParameter("Bitget", "ProductType", "UsdtFutures");
        ExchangeParameters.SetStaticParameter("GateIo", "SettleAsset", "usdt");
        ExchangeParameters.SetStaticParameter("GateIO", "SettleAsset", "usdt");

        var topN = _options.DynamicTopN > 0 ? _options.DynamicTopN : 10;
        var minVol = _options.DynamicMinQuoteVolumeUsd > 0 ? _options.DynamicMinQuoteVolumeUsd : 3_000_000m;
        var maxVol = _options.DynamicMaxQuoteVolumeUsd > 0 ? _options.DynamicMaxQuoteVolumeUsd : 600_000_000m;
        var excluded = ExcludedBases;

        _logger.LogInformation(
            "Discovery: public tickers Binance/Bybit/OKX (arb band {Min:0}–{Max:0} USDT vol, top {N})",
            minVol, maxVol, topN);

        // symbol -> exchange -> quote volume
        var volumes = new Dictionary<string, Dictionary<string, decimal>>(StringComparer.OrdinalIgnoreCase);

        await MergeHttpBinanceAsync(volumes, excluded, ct);
        await MergeHttpBybitAsync(volumes, excluded, ct);
        await MergeHttpOkxAsync(volumes, excluded, ct);

        // Optional supplemental via library for Bitget/Gate if configured
        try
        {
            await MergeLibraryFuturesAsync(exchanges, volumes, excluded, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Library futures tickers supplemental failed");
        }

        if (volumes.Count == 0)
        {
            _logger.LogWarning("Discovery HTTP empty — rotating curated arb pool");
            return RotatingFallback("HTTP tickers empty (geo/network) — rotating arb pool", topN);
        }

        var ranked = RankForArb(volumes, minVol, maxVol, topN);
        if (ranked.Count < Math.Min(4, topN))
        {
            // Relax band: accept wider volume, still require ≥2 venues
            ranked = RankForArb(volumes, minVol * 0.3m, maxVol * 2m, topN, minVenues: 2);
        }
        if (ranked.Count == 0)
            ranked = RankForArb(volumes, 500_000m, 2_000_000_000m, topN, minVenues: 2);

        if (ranked.Count == 0)
            return RotatingFallback("rank empty after filters — rotating arb pool", topN);

        _logger.LogInformation("Discovered {Count} arb pairs: {List}",
            ranked.Count,
            string.Join(", ", ranked.Select(r => $"{r.Symbol}@{r.ExchangeCount}ex/{Fmt(r.MedianQuoteVolume)}")));

        return new DiscoveryResult
        {
            Symbols = ranked,
            Source = "http-tickers-arb",
            Message = $"band {Fmt(minVol)}–{Fmt(maxVol)}; ≥2 venues; top-{ranked.Count} by arb score"
        };
    }

    /// <summary>
    /// Prefer: listed on 2–3 majors, volume in mid band (spread room), higher venue count.
    /// </summary>
    private List<DiscoveredSymbol> RankForArb(
        Dictionary<string, Dictionary<string, decimal>> volumes,
        decimal minVol,
        decimal maxVol,
        int topN,
        int minVenues = 2)
    {
        var mid = (double)((minVol + maxVol) / 2m);
        var scored = new List<(DiscoveredSymbol d, double score)>();

        foreach (var (symbol, byEx) in volumes)
        {
            if (byEx.Count < minVenues) continue;
            var vols = byEx.Values.Where(v => v > 0).OrderBy(v => v).ToList();
            if (vols.Count == 0) continue;
            var median = vols[vols.Count / 2];
            if (median < minVol || median > maxVol) continue;

            // Score: venue count strongly, proximity to mid-band volume, log volume
            var venueScore = byEx.Count * 30.0;
            var volDist = Math.Abs(Math.Log10((double)median + 1) - Math.Log10(mid + 1));
            var bandScore = Math.Max(0, 25.0 - volDist * 12.0);
            var logVol = Math.Log10((double)median + 1);
            var score = venueScore + bandScore + logVol;

            // Slight boost for known movers (meme/L2) — still must pass volume filters
            var baseAsset = BaseOf(symbol);
            if (IsMoverish(baseAsset)) score += 5;

            scored.Add((new DiscoveredSymbol
            {
                Symbol = symbol,
                BaseAsset = baseAsset,
                QuoteAsset = "USDT",
                MedianQuoteVolume = median,
                ExchangeCount = byEx.Count,
                Exchanges = byEx.Keys.OrderBy(x => x).ToList()
            }, score));
        }

        return scored
            .OrderByDescending(x => x.score)
            .Select(x => x.d)
            .Take(topN)
            .ToList();
    }

    private static bool IsMoverish(string baseAsset) =>
        baseAsset is "DOGE" or "WIF" or "PEPE" or "1000PEPE" or "1000BONK" or "WLD" or "ENA"
            or "ARB" or "OP" or "SUI" or "SEI" or "TIA" or "INJ" or "JUP" or "STRK";

    private async Task MergeHttpBinanceAsync(
        Dictionary<string, Dictionary<string, decimal>> volumes,
        HashSet<string> excluded,
        CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync("https://fapi.binance.com/fapi/v1/ticker/24hr", ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Binance fapi tickers HTTP {Code}", (int)resp.StatusCode);
                return;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var n = 0;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var sym = el.GetProperty("symbol").GetString() ?? "";
                if (!sym.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)) continue;
                if (sym.Contains('_') || sym.Contains('_')) continue; // skip dated deliveries if any
                var baseAsset = BaseOf(sym);
                if (excluded.Contains(baseAsset)) continue;
                if (!decimal.TryParse(el.GetProperty("quoteVolume").GetString(),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var qv))
                    continue;
                AddVol(volumes, sym, "Binance", qv);
                n++;
            }
            _logger.LogInformation("Binance fapi: {N} USDT perps with volume", n);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Binance public ticker fetch failed");
        }
    }

    private async Task MergeHttpBybitAsync(
        Dictionary<string, Dictionary<string, decimal>> volumes,
        HashSet<string> excluded,
        CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync("https://api.bybit.com/v5/market/tickers?category=linear", ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Bybit tickers HTTP {Code}", (int)resp.StatusCode);
                return;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("list", out var list))
                return;
            var n = 0;
            foreach (var el in list.EnumerateArray())
            {
                var sym = el.GetProperty("symbol").GetString() ?? "";
                if (!sym.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)) continue;
                var baseAsset = BaseOf(sym);
                if (excluded.Contains(baseAsset)) continue;
                // turnover24h is quote volume in USDT on Bybit
                if (!decimal.TryParse(el.GetProperty("turnover24h").GetString(),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var qv))
                    continue;
                AddVol(volumes, sym, "Bybit", qv);
                n++;
            }
            _logger.LogInformation("Bybit linear: {N} USDT perps with volume", n);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bybit public ticker fetch failed");
        }
    }

    private async Task MergeHttpOkxAsync(
        Dictionary<string, Dictionary<string, decimal>> volumes,
        HashSet<string> excluded,
        CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync("https://www.okx.com/api/v5/market/tickers?instType=SWAP", ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("OKX tickers HTTP {Code}", (int)resp.StatusCode);
                return;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return;
            var n = 0;
            foreach (var el in data.EnumerateArray())
            {
                var instId = el.GetProperty("instId").GetString() ?? ""; // e.g. BTC-USDT-SWAP
                if (!instId.EndsWith("-USDT-SWAP", StringComparison.OrdinalIgnoreCase)) continue;
                var baseAsset = instId.Split('-')[0];
                if (excluded.Contains(baseAsset)) continue;
                var sym = baseAsset + "USDT";
                // volCcy24h ≈ quote currency volume for swaps
                var volStr = el.TryGetProperty("volCcy24h", out var v) ? v.GetString() : null;
                volStr ??= el.TryGetProperty("vol24h", out var v2) ? v2.GetString() : null;
                if (!decimal.TryParse(volStr,
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var qv))
                    continue;
                AddVol(volumes, sym, "OKX", qv);
                n++;
            }
            _logger.LogInformation("OKX SWAP: {N} USDT perps with volume", n);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OKX public ticker fetch failed");
        }
    }

    private async Task MergeLibraryFuturesAsync(
        IReadOnlyList<string> exchanges,
        Dictionary<string, Dictionary<string, decimal>> volumes,
        HashSet<string> excluded,
        CancellationToken ct)
    {
        var core = exchanges.Where(e =>
            e.Equals("Binance", StringComparison.OrdinalIgnoreCase) ||
            e.Equals("Bybit", StringComparison.OrdinalIgnoreCase) ||
            e.Equals("OKX", StringComparison.OrdinalIgnoreCase)).ToList();
        if (core.Count == 0) return;

        var fut = await _rest.GetFuturesTickersAsync(
            new GetTickersRequest(TradingMode.PerpetualLinear), core, ct);
        foreach (var exResult in fut)
        {
            if (!exResult.Success || exResult.Data == null) continue;
            foreach (var ticker in exResult.Data)
            {
                var sym = ticker.SharedSymbol;
                if (sym == null) continue;
                var quote = sym.QuoteAsset ?? "USDT";
                if (!quote.Equals("USDT", StringComparison.OrdinalIgnoreCase)) continue;
                var baseAsset = sym.BaseAsset ?? "";
                if (excluded.Contains(baseAsset)) continue;
                var name = baseAsset + "USDT";
                // Shared API volume fields differ by version — HTTP path is primary
                decimal qv = 0;
                try
                {
                    var prop = ticker.GetType().GetProperty("QuoteVolume")
                               ?? ticker.GetType().GetProperty("Volume");
                    if (prop?.GetValue(ticker) is decimal d) qv = d;
                    else if (prop?.GetValue(ticker) is double dbl) qv = (decimal)dbl;
                }
                catch { /* ignore */ }
                if (qv <= 0) continue;
                AddVol(volumes, name, exResult.Exchange, qv);
            }
        }
    }

    private static void AddVol(
        Dictionary<string, Dictionary<string, decimal>> volumes,
        string symbol,
        string exchange,
        decimal quoteVolume)
    {
        if (quoteVolume <= 0) return;
        symbol = symbol.ToUpperInvariant();
        if (!volumes.TryGetValue(symbol, out var map))
        {
            map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            volumes[symbol] = map;
        }
        // keep max if duplicate source
        if (!map.TryGetValue(exchange, out var prev) || quoteVolume > prev)
            map[exchange] = quoteVolume;
    }

    private DiscoveryResult RotatingFallback(string reason, int topN)
    {
        // Rotate by UTC day-hour so list is not frozen forever when REST is blocked
        var seed = (int)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerHour);
        var rng = new Random(seed);
        var pool = ArbFriendlyPool.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Prefer config symbols first if present
        var preferred = new List<string>();
        foreach (var s in _options.NormalizedSymbols)
            if (!preferred.Contains(s, StringComparer.OrdinalIgnoreCase))
                preferred.Add(s);
        foreach (var s in pool.OrderBy(_ => rng.Next()))
            if (!preferred.Contains(s, StringComparer.OrdinalIgnoreCase))
                preferred.Add(s);

        var take = preferred.Take(topN).ToList();
        var list = take.Select(s => new DiscoveredSymbol
        {
            Symbol = s,
            BaseAsset = BaseOf(s),
            QuoteAsset = "USDT",
            MedianQuoteVolume = 0,
            ExchangeCount = 0,
            Exchanges = []
        }).ToList();

        _logger.LogWarning("Discovery fallback: {Reason} → {List}", reason, string.Join(",", take));
        return new DiscoveryResult
        {
            Symbols = list,
            Source = "rotated-curated",
            Message = reason + " | rotates hourly"
        };
    }

    private static string BaseOf(string symbol)
    {
        symbol = symbol.ToUpperInvariant();
        if (symbol.EndsWith("USDT")) return symbol[..^4];
        if (symbol.EndsWith("USDC")) return symbol[..^4];
        return symbol;
    }

    private static string Fmt(decimal v)
    {
        if (v >= 1_000_000_000) return $"{v / 1_000_000_000m:0.#}B";
        if (v >= 1_000_000) return $"{v / 1_000_000m:0.#}M";
        if (v >= 1_000) return $"{v / 1_000m:0.#}K";
        return v.ToString("0");
    }
}
