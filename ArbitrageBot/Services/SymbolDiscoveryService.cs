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
        await MergeHttpBitgetAsync(volumes, excluded, ct);
        await MergeHttpGateAsync(volumes, excluded, ct);

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

        var poolN = Math.Max(topN * 3, topN + 8);
        var ranked = RankForArb(volumes, minVol, maxVol, poolN);
        if (ranked.Count < Math.Min(4, topN))
        {
            ranked = RankForArb(volumes, minVol * 0.3m, maxVol * 2m, poolN, minVenues: 2);
        }
        if (ranked.Count == 0)
            ranked = RankForArb(volumes, 500_000m, 2_000_000_000m, poolN, minVenues: 2);

        if (ranked.Count == 0)
            return RotatingFallback("rank empty after filters — rotating arb pool", topN);

        // Depth score on trade size (Binance public book) — prefer pairs that actually fill
        var target = _options.QuoteSize > 0 ? _options.QuoteSize : 180m;
        ranked = await EnrichAndRankByDepthAsync(ranked, target, topN, ct);

        _logger.LogInformation("Discovered {Count} arb pairs (depth-scored): {List}",
            ranked.Count,
            string.Join(", ", ranked.Select(r =>
                $"{r.Symbol}@{r.ExchangeCount}ex/vol={Fmt(r.MedianQuoteVolume)}/d={r.DepthScore:0.0}x")));

        return new DiscoveryResult
        {
            Symbols = ranked,
            Source = "http-tickers+depth",
            Message = $"band {Fmt(minVol)}–{Fmt(maxVol)}; ≥2 venues; depth≥trade size; top-{ranked.Count}"
        };
    }


    /// <summary>
    /// Sample Binance futures depth; score = min(bid,ask) top-book quote notional / target size.
    /// Prefer DepthScore ≥ 1 (can fill QuoteSize on both sides near touch).
    /// </summary>
    private async Task<List<DiscoveredSymbol>> EnrichAndRankByDepthAsync(
        List<DiscoveredSymbol> candidates,
        decimal targetNotional,
        int topN,
        CancellationToken ct)
    {
        if (candidates.Count == 0) return candidates;
        targetNotional = Math.Max(50m, targetNotional);
        var enriched = new List<DiscoveredSymbol>();

        foreach (var c in candidates.Take(40))
        {
            if (ct.IsCancellationRequested) break;
            var (depthUsd, score) = await SampleBinanceDepthAsync(c.Symbol, targetNotional, ct);
            enriched.Add(c with { DepthNotionalUsd = depthUsd, DepthScore = score });
            await Task.Delay(40, ct);
        }

        var seen = new HashSet<string>(enriched.Select(e => e.Symbol), StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            if (!seen.Contains(c.Symbol))
                enriched.Add(c with { DepthScore = 0.5m });
        }

        var ordered = enriched
            .OrderByDescending(d => d.DepthScore >= 1m)
            .ThenByDescending(d => d.DepthScore)
            .ThenByDescending(d => d.ExchangeCount)
            .ThenByDescending(d => d.MedianQuoteVolume)
            .ToList();

        var fillable = ordered.Where(d => d.DepthScore >= 0.75m).ToList();
        var pick = (fillable.Count >= Math.Min(4, topN) ? fillable : ordered)
            .Take(topN)
            .ToList();

        var ok = pick.Count(d => d.DepthScore >= 1m);
        _logger.LogInformation(
            "Depth score: {Ok}/{Total} pairs fill ≥{Target:0} USDT near touch (sample Binance book)",
            ok, pick.Count, targetNotional);

        return pick;
    }

    private async Task<(decimal depthUsd, decimal score)> SampleBinanceDepthAsync(
        string symbol,
        decimal targetNotional,
        CancellationToken ct)
    {
        try
        {
            var url = $"https://fapi.binance.com/fapi/v1/depth?symbol={symbol.ToUpperInvariant()}&limit=20";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return (0, 0);

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            decimal bidQ = 0, askQ = 0;
            if (doc.RootElement.TryGetProperty("bids", out var bids))
            {
                foreach (var lvl in bids.EnumerateArray())
                {
                    if (lvl.GetArrayLength() < 2) continue;
                    if (!decimal.TryParse(lvl[0].GetString(), System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var px)) continue;
                    if (!decimal.TryParse(lvl[1].GetString(), System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var qty)) continue;
                    bidQ += px * qty;
                }
            }
            if (doc.RootElement.TryGetProperty("asks", out var asks))
            {
                foreach (var lvl in asks.EnumerateArray())
                {
                    if (lvl.GetArrayLength() < 2) continue;
                    if (!decimal.TryParse(lvl[0].GetString(), System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var px)) continue;
                    if (!decimal.TryParse(lvl[1].GetString(), System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var qty)) continue;
                    askQ += px * qty;
                }
            }

            var depth = Math.Min(bidQ, askQ);
            var score = targetNotional > 0 ? depth / targetNotional : 0;
            if (score > 50m) score = 50m;
            return (Math.Round(depth, 2), Math.Round(score, 2));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Depth sample failed for {S}", symbol);
            return (0, 0);
        }
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
        var midVolLog = (double)((minVol + maxVol) / 2m);
        var scored = new List<(DiscoveredSymbol d, double score)>();

        foreach (var (symbol, byEx) in volumes)
        {
            if (byEx.Count < minVenues) continue;
            var vols = byEx.Values.Where(v => v > 0).OrderBy(v => v).ToList();
            if (vols.Count == 0) continue;
            var median = vols[vols.Count / 2];
            if (median < minVol || median > maxVol) continue;

            // Score: venue count strongly, proximity to mid-band volume, log volume
            var venueScore = byEx.Count * 35.0; // more venues = better arb routes
            var volDist = Math.Abs(Math.Log10((double)median + 1) - Math.Log10(midVolLog + 1));
            var bandScore = Math.Max(0, 20.0 - volDist * 10.0);
            var logVol = Math.Log10((double)median + 1);
            // Prefer mid/thin over mega-liquid (spreads die on majors)
            var thinBoost = median < 20_000_000m ? 12.0 : (median < 80_000_000m ? 6.0 : 0.0);
            var score = venueScore + bandScore + logVol * 0.5 + thinBoost;

            var baseAsset = BaseOf(symbol);
            if (IsMoverish(baseAsset)) score += 8;
            // metals / tradfi perps often fragmented across venues
            if (baseAsset is "XAU" or "XAG" or "PAXG") score += 10;

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

        // Stratified pick: high / mid / low volume within band so list is not always the same majors-mid
        var ordered = scored.OrderByDescending(x => x.score).Select(x => x.d).ToList();
        if (ordered.Count <= topN)
            return ordered;

        var byVol = ordered.OrderByDescending(d => d.MedianQuoteVolume).ToList();
        var nHigh = Math.Max(1, topN / 3);
        var nLow = Math.Max(1, topN / 3);
        var nMid = topN - nHigh - nLow;

        // Hourly salt so refresh can rotate borderline names
        var salt = (int)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute / 10); // changes every 10 min
        var rng = new Random(salt ^ ordered.Count * 397);

        List<DiscoveredSymbol> TakeSlice(IEnumerable<DiscoveredSymbol> src, int n)
        {
            var arr = src.ToList();
            if (arr.Count <= n) return arr;
            // shuffle top candidates in slice then take n
            return arr.OrderBy(_ => rng.Next()).Take(Math.Min(n * 2, arr.Count))
                .OrderByDescending(d => d.MedianQuoteVolume)
                .Take(n)
                .ToList();
        }

        var high = TakeSlice(byVol.Take(Math.Max(nHigh * 3, nHigh)), nHigh);
        var low = TakeSlice(byVol.Skip(Math.Max(0, byVol.Count - nLow * 4)), nLow);
        var used = new HashSet<string>(high.Concat(low).Select(d => d.Symbol), StringComparer.OrdinalIgnoreCase);
        var midPool = byVol.Where(d => !used.Contains(d.Symbol)).ToList();
        // mid: around median volume
        midPool = midPool.OrderBy(d => Math.Abs(Math.Log10((double)d.MedianQuoteVolume + 1) - Math.Log10((double)((minVol + maxVol) / 2m) + 1))).ToList();
        var midSlice = TakeSlice(midPool, nMid);

        var result = high.Concat(midSlice).Concat(low)
            .GroupBy(d => d.Symbol, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // fill remainder from score order
        foreach (var d in ordered)
        {
            if (result.Count >= topN) break;
            if (result.All(x => !x.Symbol.Equals(d.Symbol, StringComparison.OrdinalIgnoreCase)))
                result.Add(d);
        }

        return result.Take(topN).ToList();
    }

    private static bool IsMoverish(string baseAsset) =>
        baseAsset is "DOGE" or "WIF" or "PEPE" or "1000PEPE" or "1000BONK" or "WLD" or "ENA"
            or "ARB" or "OP" or "SUI" or "SEI" or "TIA" or "INJ" or "JUP" or "STRK"
            or "ORDI" or "FET" or "RENDER" or "W" or "AAVE" or "MKR" or "CRV"
            or "XAU" or "XAG" or "PAXG" or "SOLV" or "BOME" or "NOT" or "TRB"
            or "BLUR" or "IMX" or "ZK" or "MANTA" or "DYM" or "ALT";

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


    private async Task MergeHttpBitgetAsync(
        Dictionary<string, Dictionary<string, decimal>> volumes,
        HashSet<string> excluded,
        CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(
                "https://api.bitget.com/api/v2/mix/market/tickers?productType=USDT-FUTURES", ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Bitget tickers HTTP {Code}", (int)resp.StatusCode);
                return;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return;
            var n = 0;
            foreach (var el in data.EnumerateArray())
            {
                var sym = el.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : "";
                if (!sym.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)) continue;
                var baseAsset = BaseOf(sym);
                if (excluded.Contains(baseAsset)) continue;
                // usdtVolume or quoteVolume
                var volStr = el.TryGetProperty("usdtVolume", out var uv) ? uv.GetString()
                    : el.TryGetProperty("quoteVolume", out var qv) ? qv.GetString() : null;
                if (!decimal.TryParse(volStr,
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var vol) || vol <= 0)
                    continue;
                AddVol(volumes, sym, "Bitget", vol);
                n++;
            }
            _logger.LogInformation("Bitget USDT-FUTURES: {N} tickers with volume", n);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bitget public ticker fetch failed");
        }
    }

    private async Task MergeHttpGateAsync(
        Dictionary<string, Dictionary<string, decimal>> volumes,
        HashSet<string> excluded,
        CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync("https://api.gateio.ws/api/v4/futures/usdt/tickers", ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("GateIo tickers HTTP {Code}", (int)resp.StatusCode);
                return;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var n = 0;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                // contract like BTC_USDT
                var contract = el.TryGetProperty("contract", out var c) ? c.GetString() ?? "" : "";
                if (!contract.EndsWith("_USDT", StringComparison.OrdinalIgnoreCase)) continue;
                var baseAsset = contract.Split('_')[0];
                if (excluded.Contains(baseAsset)) continue;
                var sym = baseAsset + "USDT";
                // volume_24h_quote or volume_24h_settle
                var volStr = el.TryGetProperty("volume_24h_quote", out var vq) ? vq.GetString()
                    : el.TryGetProperty("volume_24h_settle", out var vs) ? vs.GetString()
                    : el.TryGetProperty("volume_24h", out var v) ? v.GetString() : null;
                if (!decimal.TryParse(volStr,
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var vol) || vol <= 0)
                    continue;
                AddVol(volumes, sym, "GateIo", vol);
                n++;
            }
            _logger.LogInformation("GateIo USDT futures: {N} tickers with volume", n);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GateIo public ticker fetch failed");
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
        var seed = (int)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute / 10);
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
            Message = reason + " | rotates ~10m"
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
