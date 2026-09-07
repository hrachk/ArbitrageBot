using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using ArbitrageBot.Configuration;
using Microsoft.Extensions.Options;

namespace ArbitrageBot.Services.Metrics;

/// <summary>
/// CoinGlass API V4 — https://open-api-v4.coinglass.com
/// Auth header: CG-API-KEY
/// Docs: https://docs.coinglass.com/reference/endpoint-overview
/// </summary>
public sealed class CoinglassClient
{
    private readonly HttpClient _http;
    private readonly CoinglassOptions _opt;
    private readonly ILogger<CoinglassClient> _log;

    public CoinglassClient(HttpClient http, IOptions<ExternalMetricsOptions> opt, ILogger<CoinglassClient> log)
    {
        _http = http;
        _opt = opt.Value.Coinglass;
        _log = log;
        var baseUrl = string.IsNullOrWhiteSpace(_opt.BaseUrl)
            ? "https://open-api-v4.coinglass.com"
            : _opt.BaseUrl.TrimEnd('/');
        // force v4 if old host left in config
        if (baseUrl.Contains("open-api.coinglass.com", StringComparison.OrdinalIgnoreCase)
            && !baseUrl.Contains("v4", StringComparison.OrdinalIgnoreCase))
            baseUrl = "https://open-api-v4.coinglass.com";
        _http.BaseAddress = new Uri(baseUrl + "/");
        _http.Timeout = TimeSpan.FromSeconds(25);
        if (!string.IsNullOrWhiteSpace(_opt.ApiKey))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("CG-API-KEY", _opt.ApiKey.Trim());
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public bool HasKey => !string.IsNullOrWhiteSpace(_opt.ApiKey);
    public bool Enabled => _opt.Enabled;

    public async Task<(List<FundingSnapshot> funding, List<OpenInterestSnapshot> oi,
        List<LiquidationSnapshot> liq, List<LongShortSnapshot> ls, List<FundingSpreadRow> arb, string? err)>
        FetchBundleAsync(CancellationToken ct)
    {
        var funding = new List<FundingSnapshot>();
        var oi = new List<OpenInterestSnapshot>();
        var liq = new List<LiquidationSnapshot>();
        var ls = new List<LongShortSnapshot>();
        var arb = new List<FundingSpreadRow>();

        if (!_opt.Enabled)
            return (funding, oi, liq, ls, arb, "coinglass disabled");
        if (!HasKey)
            return (funding, oi, liq, ls, arb, "Coinglass ApiKey empty — set ExternalMetrics:Coinglass:ApiKey");

        string? lastErr = null;

        // 1) All funding rates by exchange (one call covers many symbols)
        try
        {
            await PullFundingExchangeListAsync(funding, ct);
        }
        catch (Exception ex)
        {
            lastErr = "funding-list: " + ex.Message;
            _log.LogWarning(ex, "Coinglass funding-rate/exchange-list");
        }

        await SoftDelay(ct);

        // 2) Funding arbitrage opportunities (Startup+ plan)
        try
        {
            await PullFundingArbitrageAsync(arb, ct);
        }
        catch (Exception ex)
        {
            lastErr ??= "arbitrage: " + ex.Message;
            _log.LogDebug(ex, "Coinglass funding-rate/arbitrage (needs Startup+ plan)");
        }

        // 3) OI + liq per base
        foreach (var baseAsset in _opt.Bases)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await PullOiExchangeListAsync(baseAsset, oi, ct);
                await SoftDelay(ct);
                await PullLiquidationCoinAsync(baseAsset, liq, ct);
                await SoftDelay(ct);
            }
            catch (Exception ex)
            {
                lastErr = baseAsset + ": " + ex.Message;
                _log.LogDebug(ex, "Coinglass OI/liq {Base}", baseAsset);
            }
        }

        if (funding.Count == 0 && oi.Count == 0 && arb.Count == 0)
            lastErr ??= "empty response — check key/plan/base URL v4";

        return (funding, oi, liq, ls, arb, lastErr);
    }

    private static Task SoftDelay(CancellationToken ct) =>
        Task.Delay(150, ct);

    private async Task<JsonElement?> GetDataAsync(string pathAndQuery, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(pathAndQuery, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("Coinglass HTTP {Code} {Path}: {Body}",
                (int)resp.StatusCode, pathAndQuery, Trunc(body, 200));
            return null;
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement.Clone();
        var code = root.TryGetProperty("code", out var c) ? c.ToString() : null;
        if (code is not null && code != "0" && code != "200")
        {
            var msg = root.TryGetProperty("msg", out var m) ? m.ToString() : body;
            _log.LogWarning("Coinglass code={Code} path={Path}: {Msg}", code, pathAndQuery, Trunc(msg, 200));
            return null;
        }

        if (root.TryGetProperty("data", out var data))
            return data.Clone();
        return root.Clone();
    }

    /// <summary>GET /api/futures/funding-rate/exchange-list</summary>
    private async Task PullFundingExchangeListAsync(List<FundingSnapshot> sink, CancellationToken ct)
    {
        var data = await GetDataAsync("api/futures/funding-rate/exchange-list", ct);
        if (data is null || data.Value.ValueKind != JsonValueKind.Array) return;

        foreach (var row in data.Value.EnumerateArray())
        {
            var sym = GetStr(row, "symbol") ?? "?";
            // USDT/stable margin only
            if (row.TryGetProperty("stablecoin_margin_list", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    var ex = GetStr(item, "exchange") ?? "?";
                    var rate = GetDec(item, "funding_rate");
                    if (rate is null) continue;
                    // API returns percent-style numbers (0.01 = 0.01%); store as fraction for our bot (0.0001)
                    var rateFrac = NormalizeFundingRate(rate.Value);
                    long? nextMs = GetLong(item, "next_funding_time");
                    sink.Add(new FundingSnapshot
                    {
                        Exchange = NormalizeEx(ex),
                        Symbol = sym.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) ? sym : sym + "USDT",
                        Rate = rateFrac,
                        NextFundingUtc = nextMs is > 0
                            ? DateTimeOffset.FromUnixTimeMilliseconds(nextMs.Value).UtcDateTime
                            : null
                    });
                }
            }
        }

        _log.LogInformation("Coinglass funding exchange-list rows={N}", sink.Count);
    }

    /// <summary>GET /api/futures/funding-rate/arbitrage?usd=10000&exchange_list=...</summary>
    private async Task PullFundingArbitrageAsync(List<FundingSpreadRow> sink, CancellationToken ct)
    {
        const string exchanges = "Binance,OKX,Bybit,Bitget,Gate,KuCoin";
        var path = $"api/futures/funding-rate/arbitrage?usd=10000&exchange_list={Uri.EscapeDataString(exchanges)}";
        var data = await GetDataAsync(path, ct);
        if (data is null || data.Value.ValueKind != JsonValueKind.Array) return;

        foreach (var row in data.Value.EnumerateArray())
        {
            var sym = GetStr(row, "symbol") ?? "?";
            if (!row.TryGetProperty("buy", out var buy) || !row.TryGetProperty("sell", out var sell))
                continue;
            var longEx = GetStr(buy, "exchange") ?? "?";
            var shortEx = GetStr(sell, "exchange") ?? "?";
            var longRate = NormalizeFundingRate(GetDec(buy, "funding_rate") ?? 0);
            var shortRate = NormalizeFundingRate(GetDec(sell, "funding_rate") ?? 0);
            var delta = shortRate - longRate;
            var apr = GetDec(row, "apr") ?? (delta * 3m * 365m * 100m);

            sink.Add(new FundingSpreadRow
            {
                Symbol = sym.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) ? sym : sym + "USDT",
                LongExchange = NormalizeEx(longEx),
                ShortExchange = NormalizeEx(shortEx),
                LongRate = longRate,
                ShortRate = shortRate,
                DeltaRate = delta,
                DeltaAprApprox = apr
            });
        }

        _log.LogInformation("Coinglass funding arbitrage rows={N}", sink.Count);
    }

    /// <summary>GET /api/futures/open-interest/exchange-list?symbol=BTC</summary>
    private async Task PullOiExchangeListAsync(string baseAsset, List<OpenInterestSnapshot> sink, CancellationToken ct)
    {
        var data = await GetDataAsync($"api/futures/open-interest/exchange-list?symbol={Uri.EscapeDataString(baseAsset)}", ct);
        if (data is null || data.Value.ValueKind != JsonValueKind.Array) return;

        foreach (var row in data.Value.EnumerateArray())
        {
            var ex = GetStr(row, "exchange") ?? "All";
            if (ex.Equals("All", StringComparison.OrdinalIgnoreCase)) continue; // keep per-venue
            var oiUsd = GetDec(row, "open_interest_usd");
            if (oiUsd is null) continue;
            sink.Add(new OpenInterestSnapshot
            {
                Exchange = NormalizeEx(ex),
                Symbol = baseAsset + "USDT",
                OpenInterestUsd = oiUsd.Value,
                OpenInterestChangePct24h = GetDec(row, "open_interest_change_percent_24h")
            });
        }
    }

    /// <summary>GET /api/futures/liquidation/aggregated-history?symbol=BTC&amp;interval=1d&amp;limit=1</summary>
    private async Task PullLiquidationCoinAsync(string baseAsset, List<LiquidationSnapshot> sink, CancellationToken ct)
    {
        // coin-level aggregated; interval 1d last bar ≈ 24h
        var path =
            $"api/futures/liquidation/aggregated-history?symbol={Uri.EscapeDataString(baseAsset)}&interval=1d&limit=1";
        var data = await GetDataAsync(path, ct);
        if (data is null) return;

        if (data.Value.ValueKind == JsonValueKind.Array && data.Value.GetArrayLength() > 0)
        {
            var last = data.Value[data.Value.GetArrayLength() - 1];
            var longUsd = GetDec(last, "long_liquidation_usd") ?? 0;
            var shortUsd = GetDec(last, "short_liquidation_usd") ?? 0;
            sink.Add(new LiquidationSnapshot
            {
                Symbol = baseAsset + "USDT",
                LongLiqUsd24h = longUsd,
                ShortLiqUsd24h = shortUsd
            });
            return;
        }

        // fallback coin-list style if present
        if (data.Value.ValueKind == JsonValueKind.Object)
        {
            var longUsd = GetDec(data.Value, "long_liquidation_usd", "longLiquidationUsd") ?? 0;
            var shortUsd = GetDec(data.Value, "short_liquidation_usd", "shortLiquidationUsd") ?? 0;
            if (longUsd + shortUsd > 0)
                sink.Add(new LiquidationSnapshot
                {
                    Symbol = baseAsset + "USDT",
                    LongLiqUsd24h = longUsd,
                    ShortLiqUsd24h = shortUsd
                });
        }
    }

    /// <summary>
    /// CoinGlass often returns funding as percent number (0.01 = 0.01%).
    /// Our engine uses fraction (0.0001 = 0.01%). If |x| > 0.05 assume already percent points → /100.
    /// Typical 8h rate is ~0.0001–0.001 as fraction or 0.01–0.1 as percent — heuristic:
    /// if abs >= 0.005 treat as percent and divide by 100.
    /// </summary>
    private static decimal NormalizeFundingRate(decimal raw)
    {
        var a = Math.Abs(raw);
        if (a >= 0.005m)
            return raw / 100m;
        return raw;
    }

    private static string NormalizeEx(string ex) => ex.Trim() switch
    {
        "Binance" or "BinanceFutures" or "BINANCE" => "Binance",
        "OKX" or "Okex" or "OKEX" or "Okx" => "OKX",
        "Bybit" or "BYBIT" => "Bybit",
        "Gate" or "Gate.io" or "Gateio" or "GateIo" or "GATE" => "GateIo",
        "Bitget" or "BITGET" => "Bitget",
        "KuCoin" or "Kucoin" or "KUCOIN" => "Kucoin",
        "Coinbase" or "Coinbase Intx" or "Coinbase International" => "Coinbase",
        "Huobi" or "HTX" => "HTX",
        "MEXC" or "Mexc" => "MEXC",
        _ => ex.Trim()
    };

    private static string? GetStr(JsonElement el, params string[] names)
    {
        foreach (var n in names)
            if (el.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String)
                return p.GetString();
        return null;
    }

    private static decimal? GetDec(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (!el.TryGetProperty(n, out var p)) continue;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var d)) return d;
            if (p.ValueKind == JsonValueKind.String &&
                decimal.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d2))
                return d2;
        }
        return null;
    }

    private static long? GetLong(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (!el.TryGetProperty(n, out var p)) continue;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var l)) return l;
            if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), out var l2)) return l2;
        }
        return null;
    }

    private static string Trunc(string? s, int n) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n] + "…";
}
