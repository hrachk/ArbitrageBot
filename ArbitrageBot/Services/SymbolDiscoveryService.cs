using ArbitrageBot.Configuration;
using CryptoClients.Net.Interfaces;
using CryptoExchange.Net.SharedApis;
using Microsoft.Extensions.Options;

namespace ArbitrageBot.Services;

public class SymbolDiscoveryService : ISymbolDiscoveryService
{
    private readonly IExchangeRestClient _rest;
    private readonly ArbitrageOptions _options;
    private readonly ILogger<SymbolDiscoveryService> _logger;

    // Mid-liquid movers — not BTC/ETH majors
    private static readonly HashSet<string> PreferredAlts = new(StringComparer.OrdinalIgnoreCase)
    {
        "SOL", "XRP", "DOGE", "ADA", "AVAX", "LINK", "DOT", "LTC", "ATOM", "NEAR",
        "APT", "ARB", "OP", "SUI", "TON", "TRX", "FIL", "INJ", "SEI", "TIA",
        "WLD", "PEPE", "WIF", "RENDER", "FET", "AAVE", "UNI", "ENA", "JUP", "STRK"
    };

    private static readonly string[] CuratedAltFutures =
    [
        "DOGEUSDT", "ADAUSDT", "AVAXUSDT", "LINKUSDT", "SUIUSDT",
        "NEARUSDT", "ARBUSDT", "OPUSDT", "INJUSDT", "APTUSDT",
        "SEIUSDT", "TIAUSDT", "WIFUSDT", "FILUSDT", "ATOMUSDT",
        "LTCUSDT", "XRPUSDT", "SOLUSDT", "TONUSDT", "DOTUSDT",
        "RENDERUSDT", "FETUSDT", "PEPEUSDT"
    ];

    public SymbolDiscoveryService(
        IExchangeRestClient rest,
        IOptions<ArbitrageOptions> options,
        ILogger<SymbolDiscoveryService> logger)
    {
        _rest = rest;
        _options = options.Value;
        _logger = logger;
    }

    private HashSet<string> ExcludedBases =>
        new((_options.ExcludeMajorBases is { Count: > 0 }
                ? _options.ExcludeMajorBases
                : ["BTC", "ETH", "BNB"]),
            StringComparer.OrdinalIgnoreCase);

    public async Task<DiscoveryResult> DiscoverAsync(
        IReadOnlyList<string> exchanges,
        CancellationToken ct = default)
    {
        var topN = _options.DynamicTopN > 0 ? _options.DynamicTopN : 10;
        var minVol = _options.DynamicMinQuoteVolumeUsd;
        var maxVol = _options.DynamicMaxQuoteVolumeUsd > 0 ? _options.DynamicMaxQuoteVolumeUsd : 800_000_000m;
        var quote = (_options.DynamicQuoteAsset ?? "USDT").ToUpperInvariant();
        var excluded = ExcludedBases;

        var volumes = new Dictionary<string, Dictionary<string, decimal>>(StringComparer.OrdinalIgnoreCase);
        var bases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var okExchanges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (_options.IsFuturesCross)
            {
                _logger.LogInformation("Discovery: futures tickers on {Ex} (exclude majors {Maj})",
                    string.Join(",", exchanges), string.Join(",", excluded));
                var fut = await _rest.GetFuturesTickersAsync(new GetTickersRequest(), exchanges, ct);
                foreach (var exResult in fut)
                {
                    if (!exResult.Success || exResult.Data == null)
                    {
                        _logger.LogWarning("Futures tickers failed {Ex}: {Err}", exResult.Exchange, exResult.Error?.Message);
                        continue;
                    }
                    okExchanges.Add(exResult.Exchange);
                    foreach (var t in exResult.Data)
                        ConsiderTicker(exResult.Exchange, t.SharedSymbol, t.LastPrice, t.Volumes, quote, excluded, volumes, bases);
                }
            }

            _logger.LogInformation("Discovery: spot tickers (supplemental ranking)");
            var spot = await _rest.GetSpotTickersAsync(new GetTickersRequest(), exchanges, ct);
            foreach (var exResult in spot)
            {
                if (!exResult.Success || exResult.Data == null) continue;
                okExchanges.Add(exResult.Exchange);
                foreach (var t in exResult.Data)
                    ConsiderTicker(exResult.Exchange, t.SharedSymbol, t.LastPrice, t.Volumes, quote, excluded, volumes, bases);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Symbol discovery REST error");
            return FallbackResult("REST error: " + ex.Message);
        }

        if (volumes.Count == 0 || okExchanges.Count == 0)
            return FallbackResult("автоподбор без объёмов (REST/geo) — curated mid-alts");

        var ranked = Rank(volumes, bases, exchanges.Count, minVol, maxVol, topN);
        if (ranked.Count == 0 && okExchanges.Count >= 2)
            ranked = Rank(volumes, bases, okExchanges.Count, minVol, maxVol, topN, okExchanges);
        if (ranked.Count == 0)
            ranked = Rank(volumes, bases, Math.Min(2, okExchanges.Count), minVol * 0.5m, maxVol * 2, topN, okExchanges);

        if (ranked.Count == 0)
            return FallbackResult("пересечение mid-alt пар пустое — curated");

        _logger.LogInformation("Discovered {Count} mid-alts: {List}",
            ranked.Count, string.Join(", ", ranked.Select(r => $"{r.Symbol}({Fmt(r.MedianQuoteVolume)})")));

        return new DiscoveryResult
        {
            Symbols = ranked,
            Source = okExchanges.Count < exchanges.Count ? "partial-dynamic-alts" : "dynamic-alts",
            Message = $"исключены {string.Join("/", excluded)}; топ-{ranked.Count} по объёму"
        };
    }

    private List<DiscoveredSymbol> Rank(
        Dictionary<string, Dictionary<string, decimal>> volumes,
        Dictionary<string, string> bases,
        int minExchangeCount,
        decimal minVol,
        decimal maxVol,
        int topN,
        HashSet<string>? onlyExchanges = null)
    {
        var list = new List<DiscoveredSymbol>();
        foreach (var (symbol, byEx0) in volumes)
        {
            var byEx = onlyExchanges == null
                ? byEx0
                : byEx0.Where(kv => onlyExchanges.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            if (byEx.Count < minExchangeCount) continue;
            var vols = byEx.Values.OrderBy(v => v).ToList();
            var median = vols[vols.Count / 2];
            if (median < minVol || median > maxVol) continue;

            var baseAsset = bases.GetValueOrDefault(symbol, "");
            list.Add(new DiscoveredSymbol
            {
                Symbol = symbol,
                BaseAsset = baseAsset,
                QuoteAsset = "USDT",
                MedianQuoteVolume = median,
                ExchangeCount = byEx.Count,
                Exchanges = byEx.Keys.ToList()
            });
        }

        // Prefer known liquid alts, then by volume (sweet spot: high but not ultra-major)
        return list
            .OrderByDescending(d => PreferredAlts.Contains(d.BaseAsset) ? 1 : 0)
            .ThenByDescending(d => d.MedianQuoteVolume)
            .Take(topN)
            .ToList();
    }

    private void ConsiderTicker(
        string exchange,
        SharedSymbol? shared,
        decimal? lastPrice,
        SharedOrderQuantity? volumes,
        string quote,
        HashSet<string> excluded,
        Dictionary<string, Dictionary<string, decimal>> volumesMap,
        Dictionary<string, string> bases)
    {
        var baseAsset = shared?.BaseAsset ?? "";
        var quoteAsset = shared?.QuoteAsset ?? "";
        if (string.IsNullOrEmpty(baseAsset) || !quoteAsset.Equals(quote, StringComparison.OrdinalIgnoreCase))
            return;
        if (IsJunk(baseAsset)) return;
        if (excluded.Contains(baseAsset)) return;

        var vol = volumes?.GetQuantityInQuoteAsset(lastPrice) ?? 0;
        if (vol <= 0) return;

        var symbol = $"{baseAsset.ToUpperInvariant()}{quote}";
        var b = baseAsset.ToUpperInvariant();
        if (!volumesMap.TryGetValue(symbol, out var byEx))
        {
            byEx = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            volumesMap[symbol] = byEx;
            bases[symbol] = b;
        }
        if (!byEx.TryGetValue(exchange, out var prev) || vol > prev)
            byEx[exchange] = vol;
    }

    private static bool IsJunk(string baseAsset) =>
        baseAsset.Contains('↑') || baseAsset.Contains('↓') ||
        baseAsset.EndsWith("UP", StringComparison.OrdinalIgnoreCase) ||
        baseAsset.EndsWith("DOWN", StringComparison.OrdinalIgnoreCase) ||
        baseAsset.EndsWith("3L", StringComparison.OrdinalIgnoreCase) ||
        baseAsset.EndsWith("3S", StringComparison.OrdinalIgnoreCase) ||
        baseAsset.Contains("BEAR", StringComparison.OrdinalIgnoreCase) ||
        baseAsset.Contains("BULL", StringComparison.OrdinalIgnoreCase);

    private static string Fmt(decimal v) =>
        v >= 1_000_000_000 ? $"{v / 1_000_000_000m:F1}B" :
        v >= 1_000_000 ? $"{v / 1_000_000m:F1}M" :
        v >= 1_000 ? $"{v / 1_000m:F0}K" : $"{v:F0}";

    private DiscoveryResult FallbackResult(string reason)
    {
        _logger.LogWarning("Discovery fallback: {Reason}", reason);
        var excluded = ExcludedBases;
        var topN = _options.DynamicTopN > 0 ? _options.DynamicTopN : 10;

        // Config symbols first (already mid-alts), pad from curated alts — never inject BTC/ETH
        var symbols = new List<string>();
        foreach (var s in _options.Symbols ?? [])
        {
            var u = s.ToUpperInvariant();
            var b = u.EndsWith("USDT") ? u[..^4] : u;
            if (excluded.Contains(b)) continue;
            if (!symbols.Contains(u)) symbols.Add(u);
        }
        foreach (var c in CuratedAltFutures)
        {
            if (symbols.Count >= topN) break;
            var b = c.EndsWith("USDT") ? c[..^4] : c;
            if (excluded.Contains(b)) continue;
            if (!symbols.Contains(c)) symbols.Add(c);
        }
        if (symbols.Count == 0)
            symbols = CuratedAltFutures.Take(topN).ToList();

        var list = symbols.Take(topN).Select(s => new DiscoveredSymbol
        {
            Symbol = s,
            BaseAsset = s.EndsWith("USDT") ? s[..^4] : s,
            QuoteAsset = "USDT",
            MedianQuoteVolume = 0,
            ExchangeCount = _options.NormalizedExchanges.Count,
            Exchanges = _options.NormalizedExchanges.ToList()
        }).ToList();

        return new DiscoveryResult
        {
            Symbols = list,
            Source = "curated-alts",
            Message = reason + " | без BTC/ETH/BNB"
        };
    }
}
