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

    public SymbolDiscoveryService(
        IExchangeRestClient rest,
        IOptions<ArbitrageOptions> options,
        ILogger<SymbolDiscoveryService> logger)
    {
        _rest = rest;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DiscoveredSymbol>> DiscoverAsync(
        IReadOnlyList<string> exchanges,
        CancellationToken ct = default)
    {
        var topN = _options.DynamicTopN > 0 ? _options.DynamicTopN : 8;
        var minVol = _options.DynamicMinQuoteVolumeUsd;
        var quote = (_options.DynamicQuoteAsset ?? "USDT").ToUpperInvariant();

        var volumes = new Dictionary<string, Dictionary<string, decimal>>(StringComparer.OrdinalIgnoreCase);
        var bases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
                    foreach (var t in exResult.Data)
                        ConsiderTicker(exResult.Exchange, t.SharedSymbol, t.LastPrice, t.Volumes, null, null, quote, volumes, bases);
                }
            }

            _logger.LogInformation("Discovery: spot tickers on {Ex}", string.Join(",", exchanges));
            var spot = await _rest.GetSpotTickersAsync(new GetTickersRequest(), exchanges, ct);
            foreach (var exResult in spot)
            {
                if (!exResult.Success || exResult.Data == null)
                {
                    _logger.LogWarning("Spot tickers failed {Ex}: {Err}", exResult.Exchange, exResult.Error?.Message);
                    continue;
                }
                foreach (var t in exResult.Data)
                    ConsiderTicker(exResult.Exchange, t.SharedSymbol, t.LastPrice, t.Volumes, t.QuoteVolume, t.Volume, quote, volumes, bases);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Symbol discovery failed");
            return Fallback("exception");
        }

        var discovered = new List<DiscoveredSymbol>();
        foreach (var (symbol, byEx) in volumes)
        {
            if (!exchanges.All(e => byEx.ContainsKey(e))) continue;
            var vols = byEx.Values.OrderBy(v => v).ToList();
            var median = vols[vols.Count / 2];
            if (median < minVol) continue;
            discovered.Add(Make(symbol, bases, byEx, median));
        }

        if (discovered.Count == 0)
        {
            _logger.LogWarning("No symbols above minVol={Min}, relaxing filter", minVol);
            foreach (var (symbol, byEx) in volumes)
            {
                if (!exchanges.All(e => byEx.ContainsKey(e))) continue;
                var vols = byEx.Values.OrderBy(v => v).ToList();
                if (vols.Count == 0) continue;
                discovered.Add(Make(symbol, bases, byEx, vols[vols.Count / 2]));
            }
        }

        var ranked = discovered
            .OrderByDescending(d => PreferredBases.Contains(d.BaseAsset) ? 1 : 0)
            .ThenByDescending(d => d.MedianQuoteVolume)
            .Take(topN)
            .ToList();

        if (ranked.Count == 0)
            return Fallback("empty");

        _logger.LogInformation("Discovered {N}: {List}", ranked.Count,
            string.Join(", ", ranked.Select(r => $"{r.Symbol}({Fmt(r.MedianQuoteVolume)})")));
        return ranked;
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

    private IReadOnlyList<DiscoveredSymbol> Fallback(string reason)
    {
        _logger.LogWarning("Fallback symbols ({Reason})", reason);
        return (_options.Symbols?.Count > 0 ? _options.Symbols : ["BTCUSDT", "ETHUSDT", "SOLUSDT"])
            .Select(s => s.ToUpperInvariant()).Distinct()
            .Select(s => new DiscoveredSymbol
            {
                Symbol = s,
                BaseAsset = s.EndsWith("USDT") ? s[..^4] : s,
                QuoteAsset = "USDT",
                MedianQuoteVolume = 0,
                ExchangeCount = _options.NormalizedExchanges.Count,
                Exchanges = _options.NormalizedExchanges.ToList()
            }).ToList();
    }
}
