using System.Collections.Concurrent;
using System.Text.Json;
using ArbitrageBot.Models;

namespace ArbitrageBot.Services;

public interface IPaperAnalyticsStore
{
    void RecordScan(int candidates, decimal? bestOpenPct, decimal? bestRtPct, decimal minProfitPct, string? note = null);
    void RecordSkip(FuturesOpportunity? opp, string reason, string? detail = null);
    void RecordOpen(FuturesPaperTrade trade, FuturesOpportunity opp);
    void RecordClose(FuturesPaperTrade trade);
    object GetLiveSummary();
    IReadOnlyList<object> GetRecentEvents(int take = 80);
    IReadOnlyList<object> GetRecentSkips(int take = 40);
    IReadOnlyList<object> GetDaySummaries(int maxDays = 14);
}

/// <summary>
/// Append-only paper analytics on disk for multi-day evaluation.
/// data/paper/events-YYYY-MM-DD.jsonl + daily-YYYY-MM-DD.json + trades-ledger.json
/// </summary>
public sealed class PaperAnalyticsStore : IPaperAnalyticsStore
{
    private readonly ILogger<PaperAnalyticsStore> _logger;
    private readonly string _dir;
    private readonly object _lock = new();
    private readonly ConcurrentQueue<object> _recent = new();
    private readonly ConcurrentQueue<object> _recentSkips = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    // live day counters
    private DateTime _day = DateTime.UtcNow.Date;
    private int _scans, _candidatesSum, _opens, _closes, _skips;
    private decimal _realizedDay;
    private decimal _bestOpenSeen, _bestRtSeen;
    private readonly Dictionary<string, int> _skipReasons = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<object> _tradeLedger = [];

    public PaperAnalyticsStore(IWebHostEnvironment env, ILogger<PaperAnalyticsStore> logger)
    {
        _logger = logger;
        _dir = Path.Combine(env.ContentRootPath, "data", "paper");
        Directory.CreateDirectory(_dir);
        LoadLedger();
        LoadTodayCounters();
        _logger.LogInformation("Paper analytics store: {Dir}", _dir);
    }

    private string EventsPath(DateTime day) => Path.Combine(_dir, $"events-{day:yyyy-MM-dd}.jsonl");
    private string DailyPath(DateTime day) => Path.Combine(_dir, $"daily-{day:yyyy-MM-dd}.json");
    private string LedgerPath => Path.Combine(_dir, "trades-ledger.json");

    private void EnsureDay()
    {
        var today = DateTime.UtcNow.Date;
        if (today == _day) return;
        PersistDailySummary(_day);
        _day = today;
        _scans = _candidatesSum = _opens = _closes = _skips = 0;
        _realizedDay = 0;
        _bestOpenSeen = _bestRtSeen = 0;
        _skipReasons.Clear();
    }

    public void RecordScan(int candidates, decimal? bestOpenPct, decimal? bestRtPct, decimal minProfitPct, string? note = null)
    {
        lock (_lock)
        {
            EnsureDay();
            _scans++;
            _candidatesSum += candidates;
            if (bestOpenPct is > 0 && bestOpenPct > _bestOpenSeen) _bestOpenSeen = bestOpenPct.Value;
            if (bestRtPct is { } rt && rt > _bestRtSeen) _bestRtSeen = rt;

            var ev = new
            {
                type = "scan",
                utc = DateTime.UtcNow,
                candidates,
                bestOpenPct,
                bestRtPct,
                minProfitPct,
                note
            };
            Append(ev);
            EnqueueRecent(ev);
            MaybeFlushDaily();
        }
    }

    public void RecordSkip(FuturesOpportunity? opp, string reason, string? detail = null)
    {
        lock (_lock)
        {
            EnsureDay();
            _skips++;
            _skipReasons[reason] = _skipReasons.GetValueOrDefault(reason) + 1;

            var ev = new
            {
                type = "skip",
                utc = DateTime.UtcNow,
                reason,
                detail,
                symbol = opp?.Symbol,
                longEx = opp?.LongExchange,
                shortEx = opp?.ShortExchange,
                openNet = opp?.NetSpreadPercent,
                rtNet = opp?.NetRoundTripPercent,
                fundNet = opp?.NetAfterFundingPercent,
                gross = opp?.GrossSpreadPercent,
                fullFill = opp?.FullyFilled,
                estPnl = opp?.EstNetPnlUsd
            };
            Append(ev);
            EnqueueRecent(ev);
            EnqueueSkip(ev);
            MaybeFlushDaily();
        }
    }

    public void RecordOpen(FuturesPaperTrade trade, FuturesOpportunity opp)
    {
        lock (_lock)
        {
            EnsureDay();
            _opens++;
            var ev = new
            {
                type = "open",
                utc = DateTime.UtcNow,
                trade.Id,
                trade.Symbol,
                trade.LongExchange,
                trade.ShortExchange,
                trade.BaseQty,
                trade.LongEntry,
                trade.ShortEntry,
                openNet = opp.NetSpreadPercent,
                rtNet = opp.NetRoundTripPercent,
                fundNet = opp.NetAfterFundingPercent,
                gross = opp.GrossSpreadPercent,
                trade.OpenFeesUsd,
                trade.Message
            };
            Append(ev);
            EnqueueRecent(ev);
            _tradeLedger.Insert(0, new
            {
                trade.Id,
                status = "Open",
                trade.OpenedAt,
                closedAt = (DateTime?)null,
                trade.Symbol,
                trade.LongExchange,
                trade.ShortExchange,
                trade.BaseQty,
                trade.LongEntry,
                trade.ShortEntry,
                openNet = opp.NetSpreadPercent,
                rtNet = opp.NetRoundTripPercent,
                realizedPnlUsd = (decimal?)null,
                trade.Message
            });
            if (_tradeLedger.Count > 2000) _tradeLedger.RemoveRange(2000, _tradeLedger.Count - 2000);
            SaveLedger();
            MaybeFlushDaily();
        }
    }

    public void RecordClose(FuturesPaperTrade trade)
    {
        lock (_lock)
        {
            EnsureDay();
            _closes++;
            var pnl = trade.RealizedPnlUsd ?? 0;
            _realizedDay += pnl;
            var ev = new
            {
                type = "close",
                utc = DateTime.UtcNow,
                trade.Id,
                trade.Symbol,
                trade.LongExchange,
                trade.ShortExchange,
                realizedPnlUsd = pnl,
                trade.LongExit,
                trade.ShortExit,
                trade.CloseFeesUsd,
                trade.Status,
                trade.Message,
                holdMin = trade.ClosedAt is { } c ? (c - trade.OpenedAt).TotalMinutes : (double?)null
            };
            Append(ev);
            EnqueueRecent(ev);

            // update ledger row
            for (var i = 0; i < _tradeLedger.Count; i++)
            {
                var row = _tradeLedger[i];
                var idProp = row.GetType().GetProperty("Id")?.GetValue(row)?.ToString()
                             ?? row.GetType().GetProperty("id")?.GetValue(row)?.ToString();
                // anonymous types — serialize compare via json
            }
            _tradeLedger.Insert(0, new
            {
                trade.Id,
                status = trade.Status,
                trade.OpenedAt,
                trade.ClosedAt,
                trade.Symbol,
                trade.LongExchange,
                trade.ShortExchange,
                trade.BaseQty,
                trade.LongEntry,
                trade.ShortEntry,
                realizedPnlUsd = pnl,
                trade.Message
            });
            SaveLedger();
            MaybeFlushDaily();
        }
    }

    public object GetLiveSummary()
    {
        lock (_lock)
        {
            EnsureDay();
            var avgCand = _scans > 0 ? (decimal)_candidatesSum / _scans : 0;
            return new
            {
                dayUtc = _day.ToString("yyyy-MM-dd"),
                scans = _scans,
                avgCandidates = Math.Round(avgCand, 2),
                opens = _opens,
                closes = _closes,
                skips = _skips,
                realizedPnlUsd = Math.Round(_realizedDay, 4),
                bestOpenPctSeen = Math.Round(_bestOpenSeen, 4),
                bestRtPctSeen = Math.Round(_bestRtSeen, 4),
                skipReasons = _skipReasons.OrderByDescending(kv => kv.Value)
                    .Select(kv => new { reason = kv.Key, count = kv.Value }).ToList(),
                dataDir = _dir,
                note = "Persisted under data/paper/ (events-*.jsonl, daily-*.json, trades-ledger.json)"
            };
        }
    }

    public IReadOnlyList<object> GetRecentEvents(int take = 80)
    {
        return _recent.Reverse().Take(Math.Clamp(take, 1, 200)).ToList();
    }

    public IReadOnlyList<object> GetRecentSkips(int take = 40)
    {
        return _recentSkips.Reverse().Take(Math.Clamp(take, 1, 100)).ToList();
    }

    public IReadOnlyList<object> GetDaySummaries(int maxDays = 14)
    {
        var list = new List<object>();
        if (!Directory.Exists(_dir)) return list;
        foreach (var file in Directory.GetFiles(_dir, "daily-*.json").OrderByDescending(f => f).Take(maxDays))
        {
            try
            {
                var json = File.ReadAllText(file);
                list.Add(JsonSerializer.Deserialize<JsonElement>(json));
            }
            catch { /* ignore */ }
        }
        // include today live at front if not flushed yet
        list.Insert(0, GetLiveSummary());
        return list;
    }

    private void Append(object ev)
    {
        try
        {
            var line = JsonSerializer.Serialize(ev, JsonOpts);
            File.AppendAllText(EventsPath(_day), line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append paper event");
        }
    }

    private void EnqueueRecent(object ev)
    {
        _recent.Enqueue(ev);
        while (_recent.Count > 300 && _recent.TryDequeue(out _)) { }
    }

    private void EnqueueSkip(object ev)
    {
        _recentSkips.Enqueue(ev);
        while (_recentSkips.Count > 100 && _recentSkips.TryDequeue(out _)) { }
    }

    private DateTime _lastDailyFlush = DateTime.MinValue;
    private void MaybeFlushDaily()
    {
        if ((DateTime.UtcNow - _lastDailyFlush).TotalSeconds < 30) return;
        _lastDailyFlush = DateTime.UtcNow;
        PersistDailySummary(_day);
    }

    private void PersistDailySummary(DateTime day)
    {
        try
        {
            var avgCand = _scans > 0 ? (decimal)_candidatesSum / _scans : 0;
            var summary = new
            {
                dayUtc = day.ToString("yyyy-MM-dd"),
                scans = _scans,
                avgCandidates = Math.Round(avgCand, 2),
                opens = _opens,
                closes = _closes,
                skips = _skips,
                realizedPnlUsd = Math.Round(_realizedDay, 4),
                bestOpenPctSeen = Math.Round(_bestOpenSeen, 4),
                bestRtPctSeen = Math.Round(_bestRtSeen, 4),
                skipReasons = _skipReasons.OrderByDescending(kv => kv.Value)
                    .Select(kv => new { reason = kv.Key, count = kv.Value }).ToList(),
                updatedUtc = DateTime.UtcNow
            };
            File.WriteAllText(DailyPath(day), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write daily summary");
        }
    }

    private void SaveLedger()
    {
        try
        {
            File.WriteAllText(LedgerPath, JsonSerializer.Serialize(_tradeLedger, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save trades ledger");
        }
    }

    private void LoadLedger()
    {
        try
        {
            if (!File.Exists(LedgerPath)) return;
            var json = File.ReadAllText(LedgerPath);
            var list = JsonSerializer.Deserialize<List<JsonElement>>(json);
            if (list == null) return;
            foreach (var el in list.Take(500))
                _tradeLedger.Add(el);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load trades ledger");
        }
    }

    private void LoadTodayCounters()
    {
        try
        {
            var path = DailyPath(_day);
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            if (r.TryGetProperty("scans", out var s)) _scans = s.GetInt32();
            if (r.TryGetProperty("opens", out var o)) _opens = o.GetInt32();
            if (r.TryGetProperty("closes", out var c)) _closes = c.GetInt32();
            if (r.TryGetProperty("skips", out var sk)) _skips = sk.GetInt32();
            if (r.TryGetProperty("realizedPnlUsd", out var p)) _realizedDay = p.GetDecimal();
            if (r.TryGetProperty("bestOpenPctSeen", out var bo)) _bestOpenSeen = bo.GetDecimal();
            if (r.TryGetProperty("bestRtPctSeen", out var br)) _bestRtSeen = br.GetDecimal();
            if (r.TryGetProperty("skipReasons", out var reasons) && reasons.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in reasons.EnumerateArray())
                {
                    var reason = item.GetProperty("reason").GetString() ?? "?";
                    var count = item.GetProperty("count").GetInt32();
                    _skipReasons[reason] = count;
                }
            }
        }
        catch { /* ignore */ }
    }
}
