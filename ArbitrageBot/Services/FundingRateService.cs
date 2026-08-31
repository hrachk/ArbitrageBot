using System.Collections.Concurrent;
using System.Text.Json;
using ArbitrageBot.Configuration;
using Microsoft.Extensions.Options;

namespace ArbitrageBot.Services;

/// <summary>
/// Polls funding rates from all active exchanges every 5 minutes.
/// Stores rolling 48-period history per symbol/exchange (~16 days at 8h intervals).
/// Provides EMA-based trend analysis for HoldDecisionEngine.
/// </summary>
public sealed class FundingRateService : BackgroundService
{
    private readonly ILogger<FundingRateService> _logger;
    private readonly IHttpClientFactory _http;
    private readonly ArbitrageState _state;
    private readonly IOptions<ArbitrageOptions> _opts;

    // [symbol][exchange] = rolling history of funding rates (newest first)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, FundingRateSnapshot[]>>
        _history = new(StringComparer.OrdinalIgnoreCase);

    // [symbol][exchange] = latest snapshot
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, FundingRateSnapshot>>
        _latest = new(StringComparer.OrdinalIgnoreCase);

    private const int HistorySize = 48;   // 48 × 8h = 16 days
    private const int PollIntervalMin = 5;

    public FundingRateService(
        ILogger<FundingRateService> logger,
        IHttpClientFactory http,
        ArbitrageState state,
        IOptions<ArbitrageOptions> opts)
    {
        _logger = logger;
        _http = http;
        _state = state;
        _opts = opts;
    }

    // ── Public API ──────────────────────────────────────────────────────────

    /// <summary>Latest funding rate for a symbol/exchange. null if not yet fetched.</summary>
    public FundingRateSnapshot? GetLatest(string symbol, string exchange)
    {
        symbol = Normalize(symbol);
        if (_latest.TryGetValue(symbol, out var byEx) &&
            byEx.TryGetValue(exchange, out var snap))
            return snap;
        return null;
    }

    /// <summary>Cross-exchange funding delta = rate(shortEx) - rate(longEx).
    /// Positive = short side receives, which is what we want when long on longEx.</summary>
    public FundingDelta? GetDelta(string symbol, string longExchange, string shortExchange)
    {
        var longRate  = GetLatest(symbol, longExchange);
        var shortRate = GetLatest(symbol, shortExchange);
        if (longRate == null || shortRate == null) return null;

        var delta = shortRate.Rate - longRate.Rate;  // we receive shortRate, pay longRate
        var ema5  = ComputeEma(symbol, longExchange, shortExchange, 5);
        var ema20 = ComputeEma(symbol, longExchange, shortExchange, 20);

        return new FundingDelta(
            Symbol:        symbol,
            LongExchange:  longExchange,
            ShortExchange: shortExchange,
            DeltaRate:     delta,
            LongRate:      longRate.Rate,
            ShortRate:     shortRate.Rate,
            IntervalHours: shortRate.IntervalHours,
            NextFundingUtc: shortRate.NextFundingUtc,
            Ema5:          ema5,
            Ema20:         ema20,
            Trend:         ema5 > ema20 ? "expanding" : "converging",
            FetchedUtc:    DateTime.UtcNow
        );
    }

    /// <summary>Best funding delta across all exchange pairs for a symbol.</summary>
    public FundingDelta? GetBestDelta(string symbol, IReadOnlyList<string> exchanges)
    {
        FundingDelta? best = null;
        for (int i = 0; i < exchanges.Count; i++)
        for (int j = 0; j < exchanges.Count; j++)
        {
            if (i == j) continue;
            var d = GetDelta(symbol, exchanges[i], exchanges[j]);
            if (d != null && (best == null || d.DeltaRate > best.DeltaRate))
                best = d;
        }
        return best;
    }

    /// <summary>All latest rates as a flat list — for Reports/Dashboard UI.</summary>
    public List<FundingRateSnapshot> GetAllLatest()
    {
        var result = new List<FundingRateSnapshot>();
        foreach (var (_, byEx) in _latest)
        foreach (var (_, snap) in byEx)
            result.Add(snap);
        return result;
    }

    // ── Background loop ─────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("FundingRateService started (poll every {Min}m)", PollIntervalMin);
        // Initial fetch after 10s startup delay
        await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var symbols   = _state.Symbols;
                var exchanges = _state.Exchanges;
                if (symbols.Count > 0 && exchanges.Count > 0)
                    await PollAllAsync(symbols, exchanges, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FundingRateService poll error");
            }

            await Task.Delay(TimeSpan.FromMinutes(PollIntervalMin), ct).ConfigureAwait(false);
        }
    }

    private async Task PollAllAsync(
        IReadOnlyList<string> symbols,
        IReadOnlyList<string> exchanges,
        CancellationToken ct)
    {
        var tasks = exchanges.Select(ex => FetchExchangeAsync(ex, symbols, ct)).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        _logger.LogDebug("FundingRateService: polled {Ex} exchanges, {Sym} symbols",
            exchanges.Count, symbols.Count);
    }

    private async Task FetchExchangeAsync(
        string exchange,
        IReadOnlyList<string> symbols,
        CancellationToken ct)
    {
        try
        {
            var snapshots = exchange.ToUpperInvariant() switch
            {
                "BINANCE" => await FetchBinanceAsync(symbols, ct),
                "BYBIT"   => await FetchBybitAsync(symbols, ct),
                "OKX"     => await FetchOkxAsync(symbols, ct),
                "BITGET"  => await FetchBitgetAsync(symbols, ct),
                "GATEIO"  => await FetchGateIoAsync(symbols, ct),
                _         => []
            };

            foreach (var snap in snapshots)
                Store(snap);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FundingRate fetch failed for {Exchange}", exchange);
        }
    }

    // ── Exchange fetchers ───────────────────────────────────────────────────

    private async Task<List<FundingRateSnapshot>> FetchBinanceAsync(
        IReadOnlyList<string> symbols, CancellationToken ct)
    {
        // GET /fapi/v1/premiumIndex — public, returns all perps
        var client = _http.CreateClient("discovery");
        var url = "https://fapi.binance.com/fapi/v1/premiumIndex";
        var resp = await client.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return [];

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var result = new List<FundingRateSnapshot>();

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var sym = el.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : "";
            if (!sym.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)) continue;
            var normalized = Normalize(sym);
            if (!IsWatched(normalized, symbols)) continue;

            decimal rate = 0, markPrice = 0;
            long nextTime = 0;
            if (el.TryGetProperty("lastFundingRate", out var r))
                decimal.TryParse(r.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out rate);
            if (el.TryGetProperty("markPrice", out var mp))
                decimal.TryParse(mp.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out markPrice);
            if (el.TryGetProperty("nextFundingTime", out var nft))
                nft.TryGetInt64(out nextTime);

            result.Add(new FundingRateSnapshot(
                Symbol:         normalized,
                Exchange:       "Binance",
                Rate:           rate,
                MarkPrice:      markPrice,
                IntervalHours:  8,
                NextFundingUtc: nextTime > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(nextTime).UtcDateTime : default,
                FetchedUtc:     DateTime.UtcNow
            ));
        }
        return result;
    }

    private async Task<List<FundingRateSnapshot>> FetchBybitAsync(
        IReadOnlyList<string> symbols, CancellationToken ct)
    {
        var client = _http.CreateClient("discovery");
        var url = "https://api.bybit.com/v5/market/tickers?category=linear";
        var resp = await client.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return [];

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var result = new List<FundingRateSnapshot>();

        if (!doc.RootElement.TryGetProperty("result", out var res) ||
            !res.TryGetProperty("list", out var list)) return [];

        foreach (var el in list.EnumerateArray())
        {
            var sym = el.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : "";
            var normalized = Normalize(sym);
            if (!IsWatched(normalized, symbols)) continue;

            decimal rate = 0, markPrice = 0;
            if (el.TryGetProperty("fundingRate", out var r))
                decimal.TryParse(r.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out rate);
            if (el.TryGetProperty("markPrice", out var mp))
                decimal.TryParse(mp.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out markPrice);

            long nextTime = 0;
            if (el.TryGetProperty("nextFundingTime", out var nft))
                long.TryParse(nft.GetString(), out nextTime);

            result.Add(new FundingRateSnapshot(
                Symbol:         normalized,
                Exchange:       "Bybit",
                Rate:           rate,
                MarkPrice:      markPrice,
                IntervalHours:  8,
                NextFundingUtc: nextTime > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(nextTime).UtcDateTime : default,
                FetchedUtc:     DateTime.UtcNow
            ));
        }
        return result;
    }

    private async Task<List<FundingRateSnapshot>> FetchOkxAsync(
        IReadOnlyList<string> symbols, CancellationToken ct)
    {
        var client = _http.CreateClient("discovery");
        // OKX requires per-instrument calls — batch by symbol
        var result = new List<FundingRateSnapshot>();

        foreach (var sym in symbols.Take(20))  // limit to avoid hammering
        {
            try
            {
                var instId = sym.Replace("USDT", "-USDT-SWAP", StringComparison.OrdinalIgnoreCase);
                var r = await client.GetAsync(
                    $"https://www.okx.com/api/v5/public/funding-rate?instId={instId}", ct)
                    .ConfigureAwait(false);
                if (!r.IsSuccessStatusCode) continue;

                var json = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var data)) continue;

                foreach (var el in data.EnumerateArray())
                {
                    decimal rate = 0;
                    if (el.TryGetProperty("fundingRate", out var fr))
                        decimal.TryParse(fr.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out rate);
                    long nextTime = 0;
                    if (el.TryGetProperty("nextFundingTime", out var nft))
                        long.TryParse(nft.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out nextTime);

                    result.Add(new FundingRateSnapshot(
                        Symbol:         Normalize(sym),
                        Exchange:       "OKX",
                        Rate:           rate,
                        MarkPrice:      0,
                        IntervalHours:  8,
                        NextFundingUtc: nextTime > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(nextTime).UtcDateTime : default,
                        FetchedUtc:     DateTime.UtcNow
                    ));
                }
                await Task.Delay(50, ct).ConfigureAwait(false);  // 50ms between OKX calls
            }
            catch { /* skip this symbol */ }
        }
        return result;
    }

    private async Task<List<FundingRateSnapshot>> FetchBitgetAsync(
        IReadOnlyList<string> symbols, CancellationToken ct)
    {
        var client = _http.CreateClient("discovery");
        var url = "https://api.bitget.com/api/v2/mix/market/tickers?productType=USDT-FUTURES";
        var resp = await client.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return [];

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var result = new List<FundingRateSnapshot>();

        if (!doc.RootElement.TryGetProperty("data", out var data)) return [];

        foreach (var el in data.EnumerateArray())
        {
            var sym = el.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : "";
            // Bitget format: XRPUSDT → normalize
            var normalized = Normalize(sym.Replace("_UMCBL", "").Replace("USDT_SPBL", "USDT"));
            if (!IsWatched(normalized, symbols)) continue;

            decimal rate = 0;
            if (el.TryGetProperty("fundingRate", out var fr))
                decimal.TryParse(fr.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out rate);

            result.Add(new FundingRateSnapshot(
                Symbol:         normalized,
                Exchange:       "Bitget",
                Rate:           rate,
                MarkPrice:      0,
                IntervalHours:  8,
                NextFundingUtc: default,
                FetchedUtc:     DateTime.UtcNow
            ));
        }
        return result;
    }

    private async Task<List<FundingRateSnapshot>> FetchGateIoAsync(
        IReadOnlyList<string> symbols, CancellationToken ct)
    {
        var client = _http.CreateClient("discovery");
        var url = "https://api.gateio.ws/api/v4/futures/usdt/contracts";
        var resp = await client.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return [];

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var result = new List<FundingRateSnapshot>();

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var sym = el.TryGetProperty("name", out var s) ? s.GetString() ?? "" : "";
            var normalized = Normalize(sym.Replace("_USDT", "USDT").Replace("BTC_USD", "BTCUSD"));
            if (!IsWatched(normalized, symbols)) continue;

            decimal rate = 0;
            if (el.TryGetProperty("funding_rate", out var fr))
                decimal.TryParse(fr.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out rate);

            result.Add(new FundingRateSnapshot(
                Symbol:         normalized,
                Exchange:       "GateIo",
                Rate:           rate,
                MarkPrice:      0,
                IntervalHours:  8,
                NextFundingUtc: default,
                FetchedUtc:     DateTime.UtcNow
            ));
        }
        return result;
    }

    // ── Storage & EMA ───────────────────────────────────────────────────────

    private void Store(FundingRateSnapshot snap)
    {
        var byEx = _latest.GetOrAdd(snap.Symbol, _ =>
            new ConcurrentDictionary<string, FundingRateSnapshot>(StringComparer.OrdinalIgnoreCase));
        byEx[snap.Exchange] = snap;

        var hist = _history.GetOrAdd(snap.Symbol, _ =>
            new ConcurrentDictionary<string, FundingRateSnapshot[]>(StringComparer.OrdinalIgnoreCase));

        hist.AddOrUpdate(snap.Exchange,
            _ => [snap],
            (_, existing) =>
            {
                var arr = new FundingRateSnapshot[Math.Min(existing.Length + 1, HistorySize)];
                arr[0] = snap;
                Array.Copy(existing, 0, arr, 1, arr.Length - 1);
                return arr;
            });
    }

    private decimal ComputeEma(string symbol, string longEx, string shortEx, int period)
    {
        symbol = Normalize(symbol);
        if (!_history.TryGetValue(symbol, out var hist)) return 0;
        if (!hist.TryGetValue(longEx, out var longH) || !hist.TryGetValue(shortEx, out var shortH))
            return 0;

        int n = Math.Min(Math.Min(period, longH.Length), shortH.Length);
        if (n == 0) return 0;

        // Compute deltas (index 0 = newest)
        var deltas = Enumerable.Range(0, n)
            .Select(i => shortH[i].Rate - longH[i].Rate)
            .Reverse()  // oldest first for EMA
            .ToArray();

        decimal k = 2m / (period + 1);
        decimal ema = deltas[0];
        for (int i = 1; i < deltas.Length; i++)
            ema = deltas[i] * k + ema * (1 - k);
        return ema;
    }

    private static string Normalize(string sym) =>
        sym.Trim().ToUpperInvariant()
           .Replace("-USDT-SWAP", "USDT")
           .Replace("-USDT",      "USDT")
           .Replace("_USDT",      "USDT");

    private static bool IsWatched(string normalized, IReadOnlyList<string> symbols) =>
        symbols.Any(s => string.Equals(Normalize(s), normalized, StringComparison.OrdinalIgnoreCase));
}

// ── Data records ─────────────────────────────────────────────────────────────

public sealed record FundingRateSnapshot(
    string   Symbol,
    string   Exchange,
    decimal  Rate,           // per-interval rate (e.g. 0.0001 = 0.01%)
    decimal  MarkPrice,
    int      IntervalHours,  // usually 8
    DateTime NextFundingUtc,
    DateTime FetchedUtc
)
{
    /// <summary>APR = Rate × (24/IntervalHours) × 365</summary>
    public decimal AnnualizedApr =>
        IntervalHours > 0 ? Rate * (24m / IntervalHours) * 365m : 0;
}

public sealed record FundingDelta(
    string   Symbol,
    string   LongExchange,   // we are long here (pay this rate)
    string   ShortExchange,  // we are short here (receive this rate)
    decimal  DeltaRate,      // ShortRate - LongRate (positive = we net receive)
    decimal  LongRate,
    decimal  ShortRate,
    int      IntervalHours,
    DateTime NextFundingUtc,
    decimal  Ema5,           // EMA(5) of delta history
    decimal  Ema20,          // EMA(20) of delta history
    string   Trend,          // "expanding" | "converging"
    DateTime FetchedUtc
)
{
    public decimal AnnualizedApr =>
        IntervalHours > 0 ? DeltaRate * (24m / IntervalHours) * 365m : 0;

    public TimeSpan? TimeToNextFunding =>
        NextFundingUtc > DateTime.UtcNow ? NextFundingUtc - DateTime.UtcNow : null;
}
