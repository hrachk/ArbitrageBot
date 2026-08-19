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

    private static readonly HashSet<string> PreferredBases = new(StringComparer.OrdinalIgnoreCase)
    {
        "BTC", "ETH", "SOL", "XRP", "BNB", "DOGE", "ADA", "AVAX", "LINK", "DOT",
        "LTC", "BCH", "ATOM", "NEAR", "APT", "ARB", "OP", "SUI", "TON", "TRX",
        "PEPE", "WIF", "FIL", "INJ", "SEI"
    };

    /// <summary>Highly liquid USDT-M perps present on Binance/Bybit/OKX — used when REST volume discovery fails (geo).</summary>
    private static readonly string[] CuratedFuturesUsdt =
    [
        "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "BNBUSDT",
        "DOGEUSDT", "ADAUSDT", "AVAXUSDT", "LINKUSDT", "SUIUSDT"
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

    public async Task<DiscoveryResult> DiscoverAsync(
        IReadOnlyList<string> exchanges,
        CancellationToken ct = default)
    {
        var topN = _options.DynamicTopN > 0 ? _options.DynamicTopN : 8;
        var minVol = _options.DynamicMinQuoteVolumeUsd;
        var quote = (_options.DynamicQuoteAsset ?? "USDT").ToUpperInvariant();

        var volumes = new Dictionary<string, Dictionary<string, decimal>>(StringComparer.OrdinalIgnoreCase);
        var bases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var okExchanges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (_options.IsFuturesCross)
            {
                _logger.LogInformation("Discovery: futures tickers on {Ex}", string.Join(",", exchanges));
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
                        ConsiderTicker(exResult.Exchange, t.SharedSymbol, t.LastPrice, t.Volumes, null, null, quote, volumes, bases);
                }
            }

            // Always try spot volumes as supplemental ranking signal
            _logger.LogInformation("Discovery: spot tickers on {Ex}", string.Join(",", exchanges));
            var spot = await _rest.GetSpotTickersAsync(new GetTickersRequest(), exchanges, ct);
            foreach (var exResult in spot)
            {
                if (!exResult.Success || exResult.Data == null)
                {
                    _logger.LogWarning("Spot tickers failed {Ex}: {Err}", exResult.Exchange, exResult.Error?.Message);
                    continue;
                }
                okExchanges.Add(exResult.Exchange);
                foreach (var t in exResult.Data)
                    ConsiderTicker(exResult.Exchange, t.SharedSymbol, t.LastPrice, t.Volumes, null, null, quote, volumes, bases);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Symbol discovery REST error");
            return FallbackResult("REST exception: " + ex.Message);
        }

        if (volumes.Count == 0 || okExchanges.Count == 0)
            return FallbackResult("автоподбор не получил объёмы (REST/geo) — используется список из конфига / curated");

        // Prefer pairs on ALL configured exchanges; else on all exchanges that responded
        var required = exchanges.Count;
        var ranked = Rank(volumes, bases, required, minVol, topN);
        if (ranked.Count == 0 && okExchanges.Count >= 2)
        {
            _logger.LogWarning("No symbols on all {N} exchanges; relaxing to responding set ({Ok})",
                required, string.Join(",", okExchanges));
            ranked = Rank(volumes, bases, okExchanges.Count, minVol, topN, okExchanges);
        }

        // Soften volume filter if still empty
        if (ranked.Count == 0)
            ranked = Rank(volumes, bases, Math.Min(required, okExchanges.Count), 0, topN, okExchanges.Count >= 2 ? okExchanges : null);

        if (ranked.Count == 0)
            return FallbackResult("объёмы пришли, но пересечение пар пустое — fallback");

        _logger.LogInformation("Discovered {Count} via REST: {List}",
            ranked.Count, string.Join(", ", ranked.Select(r => $"{r.Symbol}({Fmt(r.MedianQuoteVolume)})")));

        return new DiscoveryResult
        {
            Symbols = ranked,
            Source = okExchanges.Count < exchanges.Count ? "partial-dynamic" : "dynamic",
            Message = okExchanges.Count < exchanges.Count
                ? $"объёмы с {okExchanges.Count}/{exchanges.Count} бирж"
                : $"топ-{ranked.Count} по median quote volume"
        };
    }

    private List<DiscoveredSymbol> Rank(
        Dictionary<string, Dictionary<string, decimal>> volumes,
        Dictionary<string, string> bases,
        int minExchangeCount,
        decimal minVol,
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
            if (median < minVol) continue;
            list.Add(Make(symbol, bases, byEx, median));
        }

        return list
            .OrderByDescending(d => PreferredBases.Contains(d.BaseAsset) ? 1 : 0)
            .ThenByDescending(d => d.MedianQuoteVolume)
            .Take(topN)
            .ToList();
    }

    private static DiscoveredSymbol Make(
        string symbol,
        Dictionary<string, string> bases,
        Dictionary<string, decimal> byEx,
        decimal median) => new()
    {
        Symbol = symbol,
        BaseAsset = bases.GetValueOrDefault(symbol, symbol),
        QuoteAsset = "USDT",
        MedianQuoteVolume = median,
        ExchangeCount = byEx.Count,
        Exchanges = byEx.Keys.ToList()
    };

    private static void ConsiderTicker(
        string exchange,
        SharedSymbol? shared,
        decimal? lastPrice,
        SharedOrderQuantity? volumes,
        decimal? quoteVolume,
        decimal? volume,
        string quote,
        Dictionary<string, Dictionary<string, decimal>> volumesMap,
        Dictionary<string, string> bases)
    {
        var baseAsset = shared?.BaseAsset ?? "";
        var quoteAsset = shared?.QuoteAsset ?? "";
        if (string.IsNullOrEmpty(baseAsset) || !quoteAsset.Equals(quote, StringComparison.OrdinalIgnoreCase))
            return;
        if (IsJunk(baseAsset)) return;

        var vol = volumes?.GetQuantityInQuoteAsset(lastPrice) ?? 0;
#pragma warning disable CS0618
        if (vol <= 0 && quoteVolume is > 0) vol = quoteVolume.Value;
        if (vol <= 0 && volume is > 0 && lastPrice is > 0) vol = volume.Value * lastPrice.Value;
#pragma warning restore CS0618
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

        List<string> symbols;
        string source;

        if (_options.IsFuturesCross)
        {
            // Curated liquid perps, then intersect with config if user fixed a short list
            var curated = CuratedFuturesUsdt.Take(_options.DynamicTopN > 0 ? _options.DynamicTopN : 8).ToList();
            if (_options.Symbols is { Count: > 0 })
            {
                // Prefer config order, pad from curated
                symbols = _options.Symbols.Select(s => s.ToUpperInvariant()).Distinct().ToList();
                foreach (var c in curated)
                    if (!symbols.Contains(c) && symbols.Count < (_options.DynamicTopN > 0 ? _options.DynamicTopN : 8))
                        symbols.Add(c);
                source = "config+curated-futures";
            }
            else
            {
                symbols = curated;
                source = "curated-futures";
            }
        }
        else
        {
            symbols = (_options.Symbols?.Count > 0 ? _options.Symbols : ["BTCUSDT", "ETHUSDT", "SOLUSDT"])
                .Select(s => s.ToUpperInvariant()).Distinct().ToList();
            source = "config-fallback";
        }

        var list = symbols.Select(s => new DiscoveredSymbol
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
            Source = source,
            Message = reason
        };
    }
}
