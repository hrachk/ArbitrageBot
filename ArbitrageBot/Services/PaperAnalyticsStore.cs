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
    object GetPerformanceReport(int days = 7);
    IReadOnlyList<object> GetTradeDetails(int take = 100);
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
            var holdMin = trade.ClosedAt is { } cAt
                ? (cAt - trade.OpenedAt).TotalMinutes
                : 0d;
            _tradeLedger.Insert(0, new
            {
                id = trade.Id,
                status = trade.Status,
                openedAt = trade.OpenedAt,
                closedAt = trade.ClosedAt,
                symbol = trade.Symbol,
                longExchange = trade.LongExchange,
                shortExchange = trade.ShortExchange,
                baseQty = trade.BaseQty,
                longEntry = trade.LongEntry,
                shortEntry = trade.ShortEntry,
                realizedPnlUsd = pnl,
                holdMin,
                message = trade.Message
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
                quality = ComputeQualityUnlocked(),
                dataDir = _dir,
                note = "Persisted under data/paper/ (events-*.jsonl, daily-*.json, trades-ledger.json)"
            };
        }
    }


    private object ComputeQualityUnlocked()
    {
        try
        {
            if (!File.Exists(LedgerPath))
                return new { winRate = 0m, closed = 0, wins = 0, avgPnl = 0m, avgHoldSec = 0m };
            using var doc = JsonDocument.Parse(File.ReadAllText(LedgerPath));
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return new { winRate = 0m, closed = 0, wins = 0, avgPnl = 0m, avgHoldSec = 0m };
            int closed = 0, wins = 0;
            decimal sumPnl = 0, sumHold = 0;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var status = el.TryGetProperty("status", out var st) ? st.GetString() ?? "" :
                             el.TryGetProperty("Status", out var st2) ? st2.GetString() ?? "" : "";
                var hasClosedAt = el.TryGetProperty("closedAt", out _) || el.TryGetProperty("ClosedAt", out _);
                if (status.StartsWith("Open", StringComparison.OrdinalIgnoreCase)) continue;
                if (!status.Contains("Closed", StringComparison.OrdinalIgnoreCase) && !hasClosedAt)
                    continue;
                closed++;
                decimal pnl = 0;
                if (el.TryGetProperty("realizedPnlUsd", out var pr) && pr.TryGetDecimal(out var pd)) pnl = pd;
                else if (el.TryGetProperty("RealizedPnlUsd", out var pr2) && pr2.TryGetDecimal(out var pd2)) pnl = pd2;
                sumPnl += pnl;
                if (pnl > 0) wins++;
                DateTime? openAt = null, closeAt = null;
                if (el.TryGetProperty("openedAt", out var oa) && oa.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(oa.GetString(), out var oad)) openAt = oad.ToUniversalTime();
                if (el.TryGetProperty("OpenedAt", out var oa2) && oa2.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(oa2.GetString(), out var oad2)) openAt = oad2.ToUniversalTime();
                if (el.TryGetProperty("closedAt", out var ca) && ca.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(ca.GetString(), out var cad)) closeAt = cad.ToUniversalTime();
                if (el.TryGetProperty("ClosedAt", out var ca2) && ca2.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(ca2.GetString(), out var cad2)) closeAt = cad2.ToUniversalTime();
                if (openAt is not null && closeAt is not null)
                    sumHold += (decimal)(closeAt.Value - openAt.Value).TotalSeconds;
            }
            return new
            {
                winRate = closed > 0 ? Math.Round(100m * wins / closed, 1) : 0m,
                closed,
                wins,
                avgPnl = closed > 0 ? Math.Round(sumPnl / closed, 4) : 0m,
                avgHoldSec = closed > 0 ? Math.Round(sumHold / closed, 1) : 0m
            };
        }
        catch
        {
            return new { winRate = 0m, closed = 0, wins = 0, avgPnl = 0m, avgHoldSec = 0m };
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


    public object GetPerformanceReport(int days = 7)
    {
        days = Math.Clamp(days, 1, 90);
        var since = DateTime.UtcNow.Date.AddDays(-(days - 1));
        List<JsonElement> closed = [];
        lock (_lock)
        {
            // Prefer re-read ledger file for stable shape
            try
            {
                if (File.Exists(LedgerPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(LedgerPath));
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            var status = el.TryGetProperty("status", out var st) ? st.GetString() : "";
                            if (status is null) continue;
                            if (!status.Contains("Close", StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(status, "Closed", StringComparison.OrdinalIgnoreCase)
                                && !status.Contains("converged", StringComparison.OrdinalIgnoreCase)
                                && !status.Contains("stop", StringComparison.OrdinalIgnoreCase)
                                && !status.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                                && !status.Contains("manual", StringComparison.OrdinalIgnoreCase))
                            {
                                // still include if has realizedPnl
                                if (!el.TryGetProperty("realizedPnlUsd", out var rp) || rp.ValueKind == JsonValueKind.Null)
                                    continue;
                            }
                            if (!el.TryGetProperty("realizedPnlUsd", out var pnlEl) || pnlEl.ValueKind == JsonValueKind.Null)
                                continue;
                            DateTime closedAt = DateTime.MinValue;
                            if (el.TryGetProperty("closedAt", out var ca) && ca.ValueKind == JsonValueKind.String
                                && DateTime.TryParse(ca.GetString(), out var cdt))
                                closedAt = cdt.ToUniversalTime();
                            else if (el.TryGetProperty("ClosedAt", out var ca2) && ca2.ValueKind == JsonValueKind.String
                                && DateTime.TryParse(ca2.GetString(), out var cdt2))
                                closedAt = cdt2.ToUniversalTime();
                            if (closedAt == DateTime.MinValue) closedAt = DateTime.UtcNow;
                            if (closedAt.Date < since) continue;
                            closed.Add(el.Clone());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "performance report ledger parse");
            }
        }

        var pnls = new List<(DateTime closedAt, decimal pnl, double holdMin, string symbol, string status, string message)>();
        foreach (var el in closed)
        {
            var pnl = el.TryGetProperty("realizedPnlUsd", out var p) && p.TryGetDecimal(out var pd) ? pd : 0m;
            DateTime closedAt = DateTime.UtcNow;
            if (el.TryGetProperty("closedAt", out var ca) && ca.ValueKind == JsonValueKind.String
                && DateTime.TryParse(ca.GetString(), out var cdt))
                closedAt = cdt.ToUniversalTime();
            DateTime openedAt = closedAt;
            if (el.TryGetProperty("openedAt", out var oa) && oa.ValueKind == JsonValueKind.String
                && DateTime.TryParse(oa.GetString(), out var odt))
                openedAt = odt.ToUniversalTime();
            else if (el.TryGetProperty("OpenedAt", out var oa2) && oa2.ValueKind == JsonValueKind.String
                && DateTime.TryParse(oa2.GetString(), out var odt2))
                openedAt = odt2.ToUniversalTime();
            double hold = Math.Max(0, (closedAt - openedAt).TotalMinutes);
            if (el.TryGetProperty("holdMin", out var hm) && hm.TryGetDouble(out var hmd) && hmd > 0)
                hold = hmd;
            var sym = el.TryGetProperty("symbol", out var s) ? s.GetString() ?? "?" :
                      (el.TryGetProperty("Symbol", out var s2) ? s2.GetString() ?? "?" : "?");
            var status = el.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
            var msg = el.TryGetProperty("message", out var m) ? m.GetString() ?? "" :
                      (el.TryGetProperty("Message", out var m2) ? m2.GetString() ?? "" : "");
            pnls.Add((closedAt, pnl, hold, sym, status, msg));
        }

        pnls = pnls.OrderBy(x => x.closedAt).ToList();
        var wins = pnls.Where(x => x.pnl > 0).ToList();
        var losses = pnls.Where(x => x.pnl < 0).ToList();
        var flats = pnls.Where(x => x.pnl == 0).ToList();
        var net = pnls.Sum(x => x.pnl);
        var grossWin = wins.Sum(x => x.pnl);
        var grossLoss = Math.Abs(losses.Sum(x => x.pnl));
        var winRate = pnls.Count > 0 ? (decimal)wins.Count / pnls.Count * 100m : 0;
        var avgWin = wins.Count > 0 ? wins.Average(x => x.pnl) : 0;
        var avgLoss = losses.Count > 0 ? losses.Average(x => x.pnl) : 0;
        var pf = grossLoss > 0 ? grossWin / grossLoss : (grossWin > 0 ? 99m : 0);
        var avgHold = pnls.Count > 0 ? pnls.Average(x => x.holdMin) : 0;
        var expectancy = pnls.Count > 0 ? net / pnls.Count : 0;
        var avgRr = avgLoss != 0 ? Math.Abs(avgWin / avgLoss) : 0;

        // equity curve + max drawdown
        decimal peak = 0, equity = 0, maxDd = 0;
        var curve = new List<object>();
        foreach (var x in pnls)
        {
            equity += x.pnl;
            if (equity > peak) peak = equity;
            var dd = peak - equity;
            if (dd > maxDd) maxDd = dd;
            curve.Add(new { t = x.closedAt, equity = Math.Round(equity, 4), pnl = Math.Round(x.pnl, 4) });
        }

        // consecutive
        int consecW = 0, consecL = 0, maxCW = 0, maxCL = 0;
        foreach (var x in pnls)
        {
            if (x.pnl > 0) { consecW++; consecL = 0; maxCW = Math.Max(maxCW, consecW); }
            else if (x.pnl < 0) { consecL++; consecW = 0; maxCL = Math.Max(maxCL, consecL); }
            else { consecW = 0; consecL = 0; }
        }

        // daily calendar
        var byDay = pnls.GroupBy(x => x.closedAt.Date)
            .Select(g => new
            {
                day = g.Key.ToString("yyyy-MM-dd"),
                pnl = Math.Round(g.Sum(x => x.pnl), 4),
                trades = g.Count(),
                wins = g.Count(x => x.pnl > 0),
                losses = g.Count(x => x.pnl < 0)
            })
            .OrderBy(x => x.day)
            .ToList();

        var best = pnls.OrderByDescending(x => x.pnl).FirstOrDefault();
        var worst = pnls.OrderBy(x => x.pnl).FirstOrDefault();

        return new
        {
            mode = "PAPER",
            rangeDays = days,
            fromUtc = since.ToString("yyyy-MM-dd"),
            toUtc = DateTime.UtcNow.Date.ToString("yyyy-MM-dd"),
            netPnl = Math.Round(net, 4),
            winRate = Math.Round(winRate, 2),
            totalTrades = pnls.Count,
            wins = wins.Count,
            losses = losses.Count,
            flats = flats.Count,
            avgWin = Math.Round(avgWin, 4),
            avgLoss = Math.Round(avgLoss, 4),
            profitFactor = Math.Round(pf, 2),
            maxDrawdown = Math.Round(maxDd, 4),
            bestTrade = best.symbol != null ? new { best.symbol, pnl = Math.Round(best.pnl, 4), best.closedAt } : null,
            worstTrade = worst.symbol != null ? new { worst.symbol, pnl = Math.Round(worst.pnl, 4), worst.closedAt } : null,
            avgDurationMin = Math.Round((decimal)avgHold, 1),
            expectancy = Math.Round(expectancy, 4),
            avgRr = Math.Round((decimal)avgRr, 2),
            consecWins = maxCW,
            consecLoss = maxCL,
            equityCurve = curve,
            daily = byDay,
            note = "Built from data/paper/trades-ledger.json closed rows. Equity curve = cumulative realized (not mark-to-market)."
        };
    }

    public IReadOnlyList<object> GetTradeDetails(int take = 100)
    {
        take = Math.Clamp(take, 1, 500);
        try
        {
            if (!File.Exists(LedgerPath)) return Array.Empty<object>();
            using var doc = JsonDocument.Parse(File.ReadAllText(LedgerPath));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<object>();
            var list = new List<object>();
            foreach (var el in doc.RootElement.EnumerateArray().Take(take))
            {
                list.Add(JsonSerializer.Deserialize<object>(el.GetRawText())!);
            }
            return list;
        }
        catch
        {
            return Array.Empty<object>();
        }
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
