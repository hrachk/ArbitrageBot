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

    // Stable majors always considered if present on all exchanges
    private static readonly HashSet<string> PreferredBases = new(StringComparer.OrdinalIgnoreCase)
    {
        "BTC", "ETH", "SOL", "XRP", "BNB", "DOGE", "ADA", "AVAX", "LINK", "DOT",
        "LTC", "BCH", "ATOM", "NEAR", "APT", "ARB", "OP", "SUI", "TON", "TRX"
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

        // symbol -> exchange -> quote volume
        var volumes = new Dictionary<string, Dictionary<string, decimal>>(StringComparer.OrdinalIgnoreCase);
        var bases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var results = await _rest.GetSpotTickersAsync(new GetTickersRequest(), exchanges, ct);

            foreach (var exResult in results)
            {
                if (!exResult.Success || exResult.Data == null)
                {
                    _logger.LogWarning("Tickers failed on {Exchange}: {Error}",
                        exResult.Exchange, exResult.Error?.Message);
                    continue;
                }

                foreach (var t in exResult.Data)
                {
                    // Prefer SharedSymbol fields when available
                    var baseAsset = t.SharedSymbol?.BaseAsset ?? "";
                    var quoteAsset = t.SharedSymbol?.QuoteAsset ?? "";
                    if (string.IsNullOrEmpty(baseAsset) || string.IsNullOrEmpty(quoteAsset))
                        continue;
                    if (!quoteAsset.Equals(quote, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Skip leveraged / weird tokens
                    if (baseAsset.Contains('↑') || baseAsset.Contains('↓') ||
                        baseAsset.EndsWith("UP", StringComparison.OrdinalIgnoreCase) ||
                        baseAsset.EndsWith("DOWN", StringComparison.OrdinalIgnoreCase) ||
                        baseAsset.EndsWith("3L", StringComparison.OrdinalIgnoreCase) ||
                        baseAsset.EndsWith("3S", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var symbol = $"{baseAsset.ToUpperInvariant()}{quote}";
                    var vol = t.Volumes?.GetQuantityInQuoteAsset(t.LastPrice) ?? t.QuoteVolume;
                    if (vol is null or <= 0) vol = t.Volume;
                    if (vol is null or <= 0) continue;
                    var volVal = vol.Value;
                    if (vol <= 0) continue;

                    if (!volumes.TryGetValue(symbol, out var byEx))
                    {
                        byEx = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                        volumes[symbol] = byEx;
                        bases[symbol] = baseAsset.ToUpperInvariant();
                    }
                    byEx[exResult.Exchange] = volVal;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Symbol discovery failed");
            return FallbackSymbols();
        }

        var needed = exchanges.Count;
        var discovered = new List<DiscoveredSymbol>();

        foreach (var (symbol, byEx) in volumes)
        {
            // Must exist on ALL configured exchanges
            if (byEx.Count < needed) continue;
            if (!exchanges.All(e => byEx.ContainsKey(e))) continue;

            var vols = byEx.Values.OrderBy(v => v).ToList();
            var median = vols[vols.Count / 2];
            if (median < minVol) continue;

            discovered.Add(new DiscoveredSymbol
            {
                Symbol = symbol,
                BaseAsset = bases[symbol],
                QuoteAsset = quote,
                MedianQuoteVolume = median,
                ExchangeCount = byEx.Count,
                Exchanges = byEx.Keys.ToList()
            });
        }

        // Rank: preferred majors first boost, then by median volume
        var ranked = discovered
            .OrderByDescending(d => PreferredBases.Contains(d.BaseAsset) ? 1 : 0)
            .ThenByDescending(d => d.MedianQuoteVolume)
            .Take(topN)
            .ToList();

        if (ranked.Count == 0)
        {
            _logger.LogWarning("Discovery returned 0 symbols, using fallback list");
            return FallbackSymbols();
        }

        _logger.LogInformation("Discovered {Count} symbols: {List}",
            ranked.Count,
            string.Join(", ", ranked.Select(r => $"{r.Symbol}(vol≈{r.MedianQuoteVolume:F0})")));

        return ranked;
    }

    private IReadOnlyList<DiscoveredSymbol> FallbackSymbols()
    {
        var list = (_options.Symbols?.Count > 0
                ? _options.Symbols
                : new List<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT" })
            .Select(s => s.ToUpperInvariant())
            .Distinct()
            .Select(s => new DiscoveredSymbol
            {
                Symbol = s,
                BaseAsset = s.EndsWith("USDT") ? s[..^4] : s,
                QuoteAsset = "USDT",
                MedianQuoteVolume = 0,
                ExchangeCount = _options.NormalizedExchanges.Count,
                Exchanges = _options.NormalizedExchanges.ToList()
            })
            .ToList();
        return list;
    }
}
