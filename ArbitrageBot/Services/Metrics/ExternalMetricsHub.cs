using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ArbitrageBot.Configuration;
using Microsoft.Extensions.Options;

namespace ArbitrageBot.Services.Metrics;

/// <summary>
/// Background aggregator: Coinglass (+ on-chain stubs) → in-memory snapshot + JSONL history.
/// Exposes REST for the trading bot; optional Telegram/webhook alerts on anomalies.
/// </summary>
public sealed class ExternalMetricsHub : BackgroundService, IExternalMetricsHub
{
    private readonly CoinglassClient _coinglass;
    private readonly OnChainMetricsClient _onchain;
    private readonly ExternalMetricsOptions _opt;
    private readonly ILogger<ExternalMetricsHub> _log;
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _historyDir;

    private AggregatedMetricsSnapshot _snap = new() { Note = "not-yet-refreshed" };
    private readonly ConcurrentQueue<MetricAlert> _alerts = new();
    private readonly object _gate = new();

    public ExternalMetricsHub(
        CoinglassClient coinglass,
        OnChainMetricsClient onchain,
        IOptions<ExternalMetricsOptions> opt,
        IWebHostEnvironment env,
        IHttpClientFactory httpFactory,
        ILogger<ExternalMetricsHub> log)
    {
        _coinglass = coinglass;
        _onchain = onchain;
        _opt = opt.Value;
        _log = log;
        _httpFactory = httpFactory;
        _historyDir = Path.Combine(env.ContentRootPath, "data", "metrics");
        Directory.CreateDirectory(_historyDir);
    }

    public AggregatedMetricsSnapshot GetSnapshot()
    {
        lock (_gate) return _snap;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opt.Enabled)
        {
            _log.LogInformation("ExternalMetrics hub disabled");
            return;
        }

        _log.LogInformation(
            "ExternalMetrics hub start | Coinglass={Cg} key={HasKey} poll={Sec}s",
            _opt.Coinglass.Enabled, _coinglass.HasKey, _opt.PollIntervalSeconds);

        // first refresh soon
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RefreshAsync(stoppingToken); }
            catch (Exception ex) { _log.LogWarning(ex, "ExternalMetrics refresh failed"); }

            var sec = Math.Clamp(_opt.PollIntervalSeconds, 30, 600);
            try { await Task.Delay(TimeSpan.FromSeconds(sec), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var (funding, oi, liq, ls, cgArb, err) = await _coinglass.FetchBundleAsync(ct).ConfigureAwait(false);
        var flows = await _onchain.FetchFlowsAsync(ct).ConfigureAwait(false);
        // Prefer Coinglass ready-made arb rows; merge with local cross from exchange-list
        var spreads = cgArb.Count > 0 ? cgArb : BuildFundingSpreads(funding);
        if (cgArb.Count > 0 && funding.Count >= 2)
        {
            var local = BuildFundingSpreads(funding);
            var keys = new HashSet<string>(spreads.Select(s => s.Symbol + "|" + s.LongExchange + "|" + s.ShortExchange),
                StringComparer.OrdinalIgnoreCase);
            foreach (var s in local)
            {
                var k = s.Symbol + "|" + s.LongExchange + "|" + s.ShortExchange;
                if (keys.Add(k)) spreads.Add(s);
            }
            spreads = spreads.OrderByDescending(r => r.DeltaRate).Take(40).ToList();
        }
        var alerts = DetectAnomalies(oi, liq, spreads);

        foreach (var a in alerts)
        {
            _alerts.Enqueue(a);
            while (_alerts.Count > 50) _alerts.TryDequeue(out _);
            _ = DispatchAlertAsync(a, ct);
        }

        var note = err;
        if (!_coinglass.HasKey)
            note = "Set ExternalMetrics:Coinglass:ApiKey to enable live Coinglass pulls. Structure ready.";
        else if (funding.Count == 0 && oi.Count == 0)
            note = "Coinglass key set but no rows parsed — check plan/endpoints (" + (err ?? "empty") + ")";

        var snap = new AggregatedMetricsSnapshot
        {
            Utc = DateTime.UtcNow,
            Funding = funding,
            OpenInterest = oi,
            Liquidations = liq,
            LongShort = ls,
            ExchangeFlows = flows,
            FundingSpreads = spreads,
            Alerts = _alerts.ToArray(),
            Note = note
        };

        lock (_gate) _snap = snap;
        AppendHistory(snap);
        _log.LogInformation(
            "Metrics refresh | funding={F} oi={O} liq={L} ls={Ls} spreads={S} alerts={A}",
            funding.Count, oi.Count, liq.Count, ls.Count, spreads.Count, alerts.Count);
    }

    private static List<FundingSpreadRow> BuildFundingSpreads(List<FundingSnapshot> funding)
    {
        var rows = new List<FundingSpreadRow>();
        foreach (var g in funding.GroupBy(f => f.Symbol, StringComparer.OrdinalIgnoreCase))
        {
            var list = g.ToList();
            if (list.Count < 2) continue;
            for (var i = 0; i < list.Count; i++)
            for (var j = 0; j < list.Count; j++)
            {
                if (i == j) continue;
                // Long cheaper funding (lower rate), short higher rate
                var longLeg = list[i];
                var shortLeg = list[j];
                if (shortLeg.Rate <= longLeg.Rate) continue;
                var delta = shortLeg.Rate - longLeg.Rate;
                if (delta < 0.00005m) continue; // noise
                rows.Add(new FundingSpreadRow
                {
                    Symbol = g.Key,
                    LongExchange = longLeg.Exchange,
                    ShortExchange = shortLeg.Exchange,
                    LongRate = longLeg.Rate,
                    ShortRate = shortLeg.Rate,
                    DeltaRate = delta,
                    DeltaAprApprox = delta * 3m * 365m * 100m // rate is fraction per 8h → rough APR %
                });
            }
        }
        return rows.OrderByDescending(r => r.DeltaRate).Take(40).ToList();
    }

    private static List<MetricAlert> DetectAnomalies(
        List<OpenInterestSnapshot> oi,
        List<LiquidationSnapshot> liq,
        List<FundingSpreadRow> spreads)
    {
        var alerts = new List<MetricAlert>();
        foreach (var x in oi.Where(o => o.OpenInterestChangePct24h is >= 15m))
        {
            alerts.Add(new MetricAlert
            {
                Type = "oi-spike",
                Symbol = x.Symbol,
                Message = $"OI +{x.OpenInterestChangePct24h:F1}% 24h on {x.Exchange} {x.Symbol} (OI ${x.OpenInterestUsd:N0})"
            });
        }
        foreach (var x in liq.Where(l => l.LongLiqUsd24h + l.ShortLiqUsd24h >= 50_000_000m))
        {
            alerts.Add(new MetricAlert
            {
                Type = "liquidation-burst",
                Symbol = x.Symbol,
                Message = $"Liq 24h {x.Symbol}: long ${x.LongLiqUsd24h:N0} / short ${x.ShortLiqUsd24h:N0}"
            });
        }
        foreach (var s in spreads.Take(3).Where(s => s.DeltaAprApprox >= 30m))
        {
            alerts.Add(new MetricAlert
            {
                Type = "funding-spread",
                Symbol = s.Symbol,
                Message = $"Funding Δ {s.Symbol} {s.LongExchange}→{s.ShortExchange} ≈ {s.DeltaAprApprox:F0}% APR"
            });
        }
        return alerts;
    }

    private void AppendHistory(AggregatedMetricsSnapshot snap)
    {
        try
        {
            var path = Path.Combine(_historyDir, $"metrics-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
            var line = JsonSerializer.Serialize(new
            {
                snap.Utc,
                funding = snap.Funding.Count,
                oi = snap.OpenInterest.Count,
                liq = snap.Liquidations.Count,
                spreads = snap.FundingSpreads.Take(10),
                alerts = snap.Alerts.Take(5),
                snap.Note
            });
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "metrics history write");
        }
    }

    private async Task DispatchAlertAsync(MetricAlert alert, CancellationToken ct)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_opt.AlertWebhookUrl))
            {
                var client = _httpFactory.CreateClient();
                var body = JsonSerializer.Serialize(alert);
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                await client.PostAsync(_opt.AlertWebhookUrl, content, ct).ConfigureAwait(false);
            }
            if (!string.IsNullOrWhiteSpace(_opt.AlertTelegramBotToken)
                && !string.IsNullOrWhiteSpace(_opt.AlertTelegramChatId))
            {
                var client = _httpFactory.CreateClient();
                var url =
                    $"https://api.telegram.org/bot{_opt.AlertTelegramBotToken}/sendMessage";
                var payload = JsonSerializer.Serialize(new
                {
                    chat_id = _opt.AlertTelegramChatId,
                    text = $"[{alert.Type}] {alert.Message}"
                });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                await client.PostAsync(url, content, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "alert dispatch");
        }
    }
}
