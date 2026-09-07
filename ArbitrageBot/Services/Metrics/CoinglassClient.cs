using System.Net.Http.Headers;
using System.Text.Json;
using ArbitrageBot.Configuration;
using Microsoft.Extensions.Options;

namespace ArbitrageBot.Services.Metrics;

/// <summary>
/// Coinglass open-api — funding, OI, liquidations, long/short.
/// Docs: https://docs.coinglass.com (header CG-API-KEY).
/// Graceful when ApiKey missing: returns empty + note.
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
        _http.BaseAddress = new Uri(_opt.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(20);
        if (!string.IsNullOrWhiteSpace(_opt.ApiKey))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("CG-API-KEY", _opt.ApiKey);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public bool HasKey => !string.IsNullOrWhiteSpace(_opt.ApiKey);
    public bool Enabled => _opt.Enabled;

    public async Task<(List<FundingSnapshot> funding, List<OpenInterestSnapshot> oi,
        List<LiquidationSnapshot> liq, List<LongShortSnapshot> ls, string? err)>
        FetchBundleAsync(CancellationToken ct)
    {
        var funding = new List<FundingSnapshot>();
        var oi = new List<OpenInterestSnapshot>();
        var liq = new List<LiquidationSnapshot>();
        var ls = new List<LongShortSnapshot>();
        if (!_opt.Enabled)
            return (funding, oi, liq, ls, "coinglass disabled");
        if (!HasKey)
            return (funding, oi, liq, ls, "Coinglass ApiKey not set — put key in ExternalMetrics:Coinglass:ApiKey or env");

        string? lastErr = null;
        foreach (var baseAsset in _opt.Bases)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await PullFundingAsync(baseAsset, funding, ct);
                await Task.Delay(120, ct); // soft rate limit
                await PullOiAsync(baseAsset, oi, ct);
                await Task.Delay(120, ct);
                await PullLongShortAsync(baseAsset, ls, ct);
                await Task.Delay(120, ct);
            }
            catch (Exception ex)
            {
                lastErr = ex.Message;
                _log.LogDebug(ex, "Coinglass pull {Base}", baseAsset);
            }
        }

        try { await PullLiquidationsAsync(liq, ct); }
        catch (Exception ex)
        {
            lastErr ??= ex.Message;
            _log.LogDebug(ex, "Coinglass liquidations");
        }

        return (funding, oi, liq, ls, lastErr);
    }

    private async Task PullFundingAsync(string baseAsset, List<FundingSnapshot> sink, CancellationToken ct)
    {
        // Public v2 style endpoints vary by plan — try common paths
        var paths = new[]
        {
            $"public/v2/funding?symbol={baseAsset}",
            $"api/futures/fundingRate?symbol={baseAsset}USDT",
            $"api/fundingRate?symbol={baseAsset}"
        };
        foreach (var path in paths)
        {
            using var resp = await _http.GetAsync(path, ct);
            if (!resp.IsSuccessStatusCode) continue;
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (TryParseFunding(json, baseAsset, sink))
                return;
        }
    }

    private async Task PullOiAsync(string baseAsset, List<OpenInterestSnapshot> sink, CancellationToken ct)
    {
        var paths = new[]
        {
            $"public/v2/open_interest?symbol={baseAsset}",
            $"api/futures/openInterest?symbol={baseAsset}USDT",
            $"api/openInterest?symbol={baseAsset}"
        };
        foreach (var path in paths)
        {
            using var resp = await _http.GetAsync(path, ct);
            if (!resp.IsSuccessStatusCode) continue;
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (TryParseOi(json, baseAsset, sink))
                return;
        }
    }

    private async Task PullLongShortAsync(string baseAsset, List<LongShortSnapshot> sink, CancellationToken ct)
    {
        var paths = new[]
        {
            $"public/v2/long_short?symbol={baseAsset}",
            $"api/futures/longShortRatio?symbol={baseAsset}USDT"
        };
        foreach (var path in paths)
        {
            using var resp = await _http.GetAsync(path, ct);
            if (!resp.IsSuccessStatusCode) continue;
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (TryParseLs(json, baseAsset, sink))
                return;
        }
    }

    private async Task PullLiquidationsAsync(List<LiquidationSnapshot> sink, CancellationToken ct)
    {
        var paths = new[] { "public/v2/liquidation_history", "api/futures/liquidation" };
        foreach (var path in paths)
        {
            using var resp = await _http.GetAsync(path, ct);
            if (!resp.IsSuccessStatusCode) continue;
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (TryParseLiq(json, sink))
                return;
        }
    }

    private static bool TryParseFunding(string json, string baseAsset, List<FundingSnapshot> sink)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var data = root.TryGetProperty("data", out var d) ? d : root;
            if (data.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in data.EnumerateArray())
                {
                    var ex = GetStr(row, "exchangeName", "exchange", "exName") ?? "?";
                    var rate = GetDec(row, "rate", "fundingRate", "uRate", "value");
                    if (rate is null) continue;
                    sink.Add(new FundingSnapshot
                    {
                        Exchange = NormalizeEx(ex),
                        Symbol = baseAsset + "USDT",
                        Rate = rate.Value,
                        PredictedRate = GetDec(row, "predictedRate", "nextFundingRate"),
                        NextFundingUtc = null
                    });
                }
                return sink.Count > 0;
            }
        }
        catch { /* ignore parse */ }
        return false;
    }

    private static bool TryParseOi(string json, string baseAsset, List<OpenInterestSnapshot> sink)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.TryGetProperty("data", out var d) ? d : doc.RootElement;
            if (data.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in data.EnumerateArray())
                {
                    var ex = GetStr(row, "exchangeName", "exchange") ?? "?";
                    var oi = GetDec(row, "openInterest", "oI", "oi", "openInterestUsd");
                    if (oi is null) continue;
                    sink.Add(new OpenInterestSnapshot
                    {
                        Exchange = NormalizeEx(ex),
                        Symbol = baseAsset + "USDT",
                        OpenInterestUsd = oi.Value,
                        OpenInterestChangePct24h = GetDec(row, "h24Change", "change24h", "oiChange")
                    });
                }
                return sink.Count > 0;
            }
            if (data.ValueKind == JsonValueKind.Object)
            {
                var oi = GetDec(data, "openInterest", "oI", "oi");
                if (oi is not null)
                {
                    sink.Add(new OpenInterestSnapshot
                    {
                        Exchange = "AGG",
                        Symbol = baseAsset + "USDT",
                        OpenInterestUsd = oi.Value
                    });
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    private static bool TryParseLs(string json, string baseAsset, List<LongShortSnapshot> sink)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.TryGetProperty("data", out var d) ? d : doc.RootElement;
            if (data.ValueKind != JsonValueKind.Array) return false;
            foreach (var row in data.EnumerateArray())
            {
                var ex = GetStr(row, "exchangeName", "exchange") ?? "AGG";
                var lr = GetDec(row, "longRate", "longRatio", "longAccount");
                var sr = GetDec(row, "shortRate", "shortRatio", "shortAccount");
                if (lr is null || sr is null) continue;
                sink.Add(new LongShortSnapshot
                {
                    Exchange = NormalizeEx(ex),
                    Symbol = baseAsset + "USDT",
                    LongRatio = lr.Value,
                    ShortRatio = sr.Value
                });
            }
            return sink.Count > 0;
        }
        catch { return false; }
    }

    private static bool TryParseLiq(string json, List<LiquidationSnapshot> sink)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.TryGetProperty("data", out var d) ? d : doc.RootElement;
            if (data.ValueKind != JsonValueKind.Array) return false;
            var bySym = new Dictionary<string, (decimal L, decimal S)>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in data.EnumerateArray())
            {
                var sym = GetStr(row, "symbol", "pair") ?? "BTCUSDT";
                if (!sym.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) && sym.Length <= 5)
                    sym += "USDT";
                var longUsd = GetDec(row, "longLiquidationUsd", "longLiq", "buyVolUsd") ?? 0;
                var shortUsd = GetDec(row, "shortLiquidationUsd", "shortLiq", "sellVolUsd") ?? 0;
                if (!bySym.TryGetValue(sym, out var cur)) cur = (0, 0);
                bySym[sym] = (cur.L + longUsd, cur.S + shortUsd);
            }
            foreach (var (sym, v) in bySym)
                sink.Add(new LiquidationSnapshot { Symbol = sym, LongLiqUsd24h = v.L, ShortLiqUsd24h = v.S });
            return sink.Count > 0;
        }
        catch { return false; }
    }

    private static string NormalizeEx(string ex) => ex.Trim() switch
    {
        "Binance" or "BinanceFutures" => "Binance",
        "OKX" or "Okex" or "OKEX" => "OKX",
        "Bybit" => "Bybit",
        "Gate" or "Gate.io" or "Gateio" => "GateIo",
        "Bitget" => "Bitget",
        "Huobi" or "HTX" => "HTX",
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
            if (p.ValueKind == JsonValueKind.String && decimal.TryParse(p.GetString(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var d2))
                return d2;
        }
        return null;
    }
}
