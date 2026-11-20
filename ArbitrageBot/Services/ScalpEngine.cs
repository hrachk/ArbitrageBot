using System.Collections.Concurrent;
using System.Text.Json;
using ArbitrageBot.Configuration;
using ArbitrageBot.Models;

namespace ArbitrageBot.Services;

/// <summary>
/// Single-leg scalper that runs beside the hedge without touching it.
///
/// Signal: lead-lag. When the median of the other venues has moved and one venue has not
/// followed beyond its learned normal offset, take one leg on the lagging venue in the
/// market's direction and exit when it catches up (see <see cref="LeadLag"/>).
///
/// Isolation from the hedge:
///  - own budget (ScalpDepositUsd x ScalpLeverage), own daily loss cap, own position limit;
///    scalp positions are not counted in the hedge's open-position or daily-loss limits;
///  - runs on its own loop (ScalpWorker), so an order round trip never delays the hedge scan;
///  - never trades a symbol the hedge holds or is entering, and the hedge skips symbols the
///    scalper holds (ExposureRegistry) — two strategies on one symbol would net legs;
///  - real orders only when live orders are on AND ScalpLive is on.
/// </summary>
public sealed class ScalpEngine
{
    private readonly IFuturesMarketService _market;
    private readonly ActiveMarketContext _markets;
    private readonly RuntimeRiskConfig _runtime;
    private readonly LiveOrderEngine _orders;
    private readonly IFuturesPaperService _paper;
    private readonly LiveTradingGuard _guard;
    private readonly ExposureRegistry _exposure;
    private readonly ILogger<ScalpEngine> _logger;
    private readonly string _ledgerPath;
    private static readonly JsonSerializerOptions LedgerJson = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly List<ScalpPosition> _open = [];
    private readonly List<ScalpPosition> _closed = [];
    private readonly List<PendingEntry> _pending = [];
    // symbol|venue -> learned normal offset of that venue against the others
    private readonly ConcurrentDictionary<string, BasisEstimator> _offsets = new(StringComparer.OrdinalIgnoreCase);
    // symbol -> recent all-venue medians
    private readonly ConcurrentDictionary<string, List<(long Ms, decimal Mid)>> _medians = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (bool IsLong, long SinceMs)> _signalSince = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _symbolPauseUntil = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastOpenUtc = DateTime.MinValue;
    private string _lastNote = "";
    // What the scalper sees right now, for the UI: how many venue/symbol pairs it watches and the one
    // closest to an entry, with what is still missing. Rebuilt every pick.
    private object? _watch;

    private const long MarketMoveWindowMs = 3000;
    private const int OffsetMinSamples = 40;
    private const long OffsetMinSpanMs = 45_000;

    public ScalpEngine(
        IFuturesMarketService market,
        ActiveMarketContext markets,
        RuntimeRiskConfig runtime,
        LiveOrderEngine orders,
        IFuturesPaperService paper,
        LiveTradingGuard guard,
        ExposureRegistry exposure,
        IWebHostEnvironment env,
        ILogger<ScalpEngine> logger)
    {
        _market = market;
        _markets = markets;
        _runtime = runtime;
        _orders = orders;
        _paper = paper;
        _guard = guard;
        _exposure = exposure;
        _ledgerPath = Path.Combine(env.ContentRootPath, "data", "live", "scalp-ledger.json");
        _logger = logger;
        LoadLedger();
        // The scalper no longer borrows the hedge's position slots.
        _guard.SetAdditionalOpenPositions(0);
    }

    /// <summary>Drop closed PAPER scalps from history (live history is never touched).</summary>
    public int ResetPaperHistory()
    {
        lock (_lock)
        {
            var n = _closed.RemoveAll(p => !p.Live);
            SaveLedgerUnlocked();
            return n;
        }
    }

    /// <summary>True when the scalper holds or is entering this symbol. The hedge skips it.</summary>
    public bool HasExposure(string symbol)
    {
        lock (_lock)
            return _open.Any(p => p.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
                || _pending.Any(p => p.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
    }

    private bool LiveScalping => _guard.CanPlaceOrders && _runtime.Snapshot.ScalpLive;

    public object Snapshot()
    {
        var snap = _runtime.Snapshot;
        lock (_lock)
        {
            var today = DateTime.UtcNow.Date;
            var paperClosed = _closed.Where(p => !p.Live).ToList();
            var liveClosed = _closed.Where(p => p.Live).ToList();
            var paperRealized = paperClosed.Sum(p => p.RealizedUsd ?? 0m);
            var liveNow = LiveScalping;
            object Book(bool isLive)
            {
                var c = _closed.Where(p => p.Live == isLive).ToList();
                var o = _open.Where(p => p.Live == isLive).ToList();
                var realized = c.Sum(p => p.RealizedUsd ?? 0m);
                var unreal = o.Sum(p => p.UnrealizedUsd);
                return new
                {
                    equityUsd = snap.ScalpDepositUsd + realized + unreal,
                    realizedUsd = realized,
                    unrealizedUsd = unreal,
                    realizedTodayUsd = c.Where(p => p.ClosedAt?.Date == today).Sum(p => p.RealizedUsd ?? 0m),
                    trades = c.Count,
                    wins = c.Count(p => (p.RealizedUsd ?? 0m) > 0),
                    tradesToday = c.Count(p => p.ClosedAt?.Date == today),
                    open = o.Count
                };
            }
            var lev = Math.Clamp(snap.ScalpLeverage, 1m, 5m);
            var marginUsed = _open.Sum(p => p.NotionalUsd > 0 ? p.NotionalUsd / Math.Max(1m, p.Leverage) : p.Entry * p.Qty / lev);
            return new
            {
                enabled = snap.SpatialScalpMode,
                live = LiveScalping,
                liveRequested = snap.ScalpLive,
                venues = string.IsNullOrWhiteSpace(snap.ScalpVenues) ? "все" : snap.ScalpVenues,
                venuesUnknown = UnknownVenues(snap.ScalpVenues),
                liveVenues = LiveScalping
                    ? Venues.All
                        .Where(x => VenueTradable(x, ParseVenues(snap.ScalpVenues), true)).ToArray()
                    : Array.Empty<string>(),
                lastNote = _lastNote,
                watch = _watch,
                settings = new
                {
                    depositUsd = snap.ScalpDepositUsd, leverage = lev, maxOpen = snap.ScalpMaxOpen,
                    dailyLossUsd = snap.ScalpDailyLossUsd, entryPercent = snap.ScalpEntryPercent,
                    stopPercent = snap.ScalpStopPercent, maxHoldSeconds = snap.ScalpMaxHoldSeconds
                },
                wallet = new
                {
                    depositUsd = snap.ScalpDepositUsd,
                    paperEquityUsd = snap.ScalpDepositUsd + paperRealized + _open.Where(p => !p.Live).Sum(p => p.UnrealizedUsd),
                    paperRealizedUsd = paperRealized,
                    liveRealizedUsd = liveClosed.Sum(p => p.RealizedUsd ?? 0m),
                    // Today / trades / wins below are for the mode we trade in now.
                    mode = liveNow ? "live" : "paper",
                    realizedTodayUsd = _closed.Where(p => p.Live == liveNow && p.ClosedAt?.Date == today).Sum(p => p.RealizedUsd ?? 0m),
                    marginUsedUsd = marginUsed,
                    trades = _closed.Count(p => p.Live == liveNow),
                    wins = _closed.Count(p => p.Live == liveNow && (p.RealizedUsd ?? 0m) > 0),
                    tradesToday = _closed.Count(p => p.Live == liveNow && p.ClosedAt?.Date == today),
                    live = Book(true),
                    paper = Book(false)
                },
                open = _open.Select(p => new
                {
                    p.Id, p.Symbol, p.Exchange, p.Side, p.Qty, p.Entry, p.OpenedAt, p.LastMarkPrice,
                    p.UnrealizedUsd, p.Note, p.Status, p.NeedsReconciliation, p.Live, p.NotionalUsd, p.Leverage
                }),
                pending = _pending.Select(p => new { p.Symbol, p.Exchange, Side = p.IsLong ? "Long" : "Short", p.DueUtc }),
                closed = _closed.Take(60).Select(p => new
                {
                    p.Id, p.Symbol, p.Exchange, p.Side, p.Qty, p.Entry, p.Exit,
                    p.OpenedAt, p.ClosedAt, p.RealizedUsd, p.Note, p.Live, p.NotionalUsd
                }),
                openCount = _open.Count,
                realizedUsd = _closed.Where(p => p.RealizedUsd.HasValue).Sum(p => p.RealizedUsd!.Value)
            };
        }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        UpdateMarketModel();
        // Always manage existing positions, even after the entry toggle is disabled.
        MarkAndMaybeClose();
        ResolvePending();
        if (ct.IsCancellationRequested || !_runtime.Snapshot.SpatialScalpMode) return;
        // Only one scalp strategy opens trades at a time; the density scalper has its own engine.
        if (_runtime.Snapshot.ScalpStrategy == "density") { _watch = null; return; }

        var snap = _runtime.Snapshot;
        decimal realizedToday;
        int openNow;
        lock (_lock)
        {
            // The daily stop counts only the mode we trade in now: paper results never offset real losses.
            var liveNow = LiveScalping;
            realizedToday = _closed.Where(p => p.Live == liveNow && p.ClosedAt?.Date == DateTime.UtcNow.Date).Sum(p => p.RealizedUsd ?? 0m);
            openNow = _open.Count + _pending.Count;
        }
        var lossCap = Math.Abs(snap.ScalpDailyLossUsd) > 0 ? Math.Abs(snap.ScalpDailyLossUsd) : Math.Max(1m, snap.ScalpDepositUsd * 0.1m);
        if (realizedToday <= -lossCap) { _lastNote = $"дневной лимит убытка {lossCap:F2} достигнут"; return; }
        if (openNow >= Math.Max(1, snap.ScalpMaxOpen)) return;
        if ((DateTime.UtcNow - _lastOpenUtc).TotalSeconds < Math.Max(0, snap.ScalpCooldownSeconds)) return;

        var lev = Math.Clamp(snap.ScalpLeverage, 1m, 5m);
        if (LiveScalping) WarmUpLive(snap, lev);
        var deposit = Math.Max(0m, snap.ScalpDepositUsd);
        var notional = deposit * lev / Math.Max(1, snap.ScalpMaxOpen);
        if (notional < 6m) { _lastNote = "депозит скальпа слишком мал"; return; }

        var c = Pick(notional, snap);
        if (c == null) return;
        var (symbol, sig) = c.Value;
        var fee = ArbEconomics.TakerPercent(snap.EstimatedTakerFees, sig.Exchange);
        var qty = LiveOrderEngine.RoundBaseQty(notional / sig.EntryPrice, sig.EntryPrice);
        if (qty <= 0) return;
        // The signal IS a venue about to move toward the market, so by the time the order lands
        // (~0.3–0.8 s from a home connection) its touch has usually moved. A 4 bp IOC limit then
        // expires unfilled. Allow up to half of the expected edge (max 0.10%), never less than
        // IocSlipBps, so a fill still keeps at least half the expected result.
        var slipPct = Math.Max(Math.Max(0.5m, snap.IocSlipBps) / 100m, Math.Min(0.10m, sig.ExpectedNetPercent / 2m));
        var slip = slipPct / 100m;
        var limit = sig.IsLong ? sig.EntryPrice * (1m + slip) : sig.EntryPrice * (1m - slip);
        _lastOpenUtc = DateTime.UtcNow;

        if (!LiveScalping)
        {
            lock (_lock)
                _pending.Add(new PendingEntry(symbol, sig.Exchange, sig.IsLong, qty, limit, fee, notional, lev,
                    sig, MedianNow(symbol) ?? sig.ReferenceMid,
                    DateTime.UtcNow.AddMilliseconds(Math.Max(0, snap.PaperFillLatencyMs))));
            return;
        }

        var pending = NewPosition(symbol, sig, qty, sig.EntryPrice, live: true, fee, notional, lev, MedianNow(symbol) ?? sig.ReferenceMid);
        pending.Status = "Opening";
        pending.NeedsReconciliation = true;
        lock (_lock) { _open.Add(pending); SaveLedgerUnlocked(); }
        LiveLegOrderResult sent;
        try
        {
            sent = await _orders.PlaceScalpLegAsync(sig.Exchange, symbol, sig.IsLong, qty,
                LiveOrderEngine.SnapLimit(sig.EntryPrice, limit, sig.IsLong), lev, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            pending.Status = "ReconciliationRequired";
            pending.Note = $"{pending.Note} | order path interrupted: {ex.Message}";
            _guard.Kill($"Scalp entry interrupted for {symbol} {sig.Exchange}");
            lock (_lock) SaveLedgerUnlocked();
            return;
        }
        if ((sent.Verified && sent.FilledBaseQty <= 0) || (!sent.Verified && !sent.Accepted))
        {
            lock (_lock) { _open.Remove(pending); SaveLedgerUnlocked(); }
            PauseSymbol(symbol, 5000);
            _logger.LogInformation(sent.Accepted ? "SCALP live entry not filled {Sym} {Ex}: {Err}" : "SCALP live entry rejected before sending {Sym} {Ex}: {Err}", symbol, sig.Exchange, sent.Error);
            return;
        }
        if (!sent.Verified || (sent.FilledBaseQty > 0 && sent.AveragePrice <= 0))
        {
            pending.Status = "ReconciliationRequired";
            pending.OrderId = sent.Id;
            pending.Note = $"{pending.Note} | order state unknown: {sent.Error}";
            if (sent.AveragePrice <= 0) _guard.Kill($"Scalp fill price missing for {symbol} {sig.Exchange}");
            lock (_lock) SaveLedgerUnlocked();
            return;
        }
        pending.Qty = sent.FilledBaseQty;
        pending.Entry = sent.AveragePrice;
        pending.OrderId = sent.Id;
        pending.NeedsReconciliation = false;
        pending.Status = sent.Ok ? "Open" : "OpenPartialFill";
        lock (_lock) SaveLedgerUnlocked();
        _logger.LogInformation("SCALP LIVE {Side} {Sym} on {Ex} qty={Q} @ {P} | {Note}",
            pending.Side, symbol, sig.Exchange, pending.Qty, pending.Entry, pending.Note);
    }

    // ---------------- market model ----------------

    private List<VenueQuote> Quotes(string symbol)
    {
        var list = new List<VenueQuote>();
        foreach (var (ex, tk) in _market.GetBookTickers(symbol))
            if (tk.BestBid > 0 && tk.BestAsk > tk.BestBid)
                list.Add(new VenueQuote(ex, tk.BestBid, tk.BestAsk, _market.QuoteLive(ex, symbol)));
        return list;
    }

    private static decimal? Median(IEnumerable<decimal> values)
    {
        var v = values.OrderBy(x => x).ToList();
        if (v.Count == 0) return null;
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2m;
    }

    private void UpdateMarketModel()
    {
        var now = Environment.TickCount64;
        foreach (var symbol in _markets.Symbols)
        {
            var q = Quotes(symbol);
            var live = q.Where(x => x.Fresh).ToList();
            if (live.Count < 3) continue;
            var med = Median(live.Select(x => x.Mid));
            if (med is not { } m || m <= 0) continue;
            var hist = _medians.GetOrAdd(symbol, _ => []);
            lock (hist)
            {
                if (hist.Count == 0 || now - hist[^1].Ms >= 200) hist.Add((now, m));
                while (hist.Count > 0 && now - hist[0].Ms > 15_000) hist.RemoveAt(0);
            }
            foreach (var v in live)
            {
                if (v.WidthPercent > 0.12m) continue;
                var r = LeadLag.ReferenceMid(live, v.Exchange);
                if (r is not { } rm) continue;
                _offsets.GetOrAdd(symbol + "|" + v.Exchange, _ => new BasisEstimator())
                    .Observe(LeadLag.OffsetPercent(v.Mid, rm), now);
            }
        }
    }

    private decimal? MedianNow(string symbol)
    {
        if (!_medians.TryGetValue(symbol, out var h)) return null;
        lock (h) return h.Count > 0 ? h[^1].Mid : null;
    }

    /// <summary>All-venue median move over the last few seconds, percent.</summary>
    private decimal? MarketMove(string symbol)
    {
        if (!_medians.TryGetValue(symbol, out var h)) return null;
        lock (h)
        {
            if (h.Count < 2) return null;
            var now = h[^1];
            (long Ms, decimal Mid)? past = null;
            foreach (var x in h)
                if (now.Ms - x.Ms <= MarketMoveWindowMs) { past = x; break; }
            if (past is not { } p || now.Ms - p.Ms < MarketMoveWindowMs / 2 || p.Mid <= 0) return null;
            return (now.Mid - p.Mid) / p.Mid * 100m;
        }
    }

    private (decimal Fair, bool Ready) FairOffset(string symbol, string exchange)
    {
        if (!_offsets.TryGetValue(symbol + "|" + exchange, out var e)) return (0m, false);
        var (fair, _, ready) = e.Read(OffsetMinSamples, OffsetMinSpanMs);
        return (fair, ready);
    }

    private bool SymbolBusyForScalp(string symbol)
    {
        if (_exposure.IsArbReserved(symbol)) return true;
        if (_paper.GetOpenPositions().Any(p => p.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))) return true;
        if (_orders.GetOpen().Any(p => p.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))) return true;
        if (_symbolPauseUntil.TryGetValue(symbol, out var until) && until > DateTime.UtcNow) return true;
        return HasExposure(symbol);
    }

    private void PauseSymbol(string symbol, int ms)
        => _symbolPauseUntil[symbol] = DateTime.UtcNow.AddMilliseconds(ms);

    private static HashSet<string>? ParseVenues(string? csv) => Venues.AllowSet(csv);

    private string _warmKey = "";
    private DateTime _warmAtUtc = DateTime.MinValue;
    private int _warming;

    /// <summary>Background: filters + leverage for every live venue and symbol, redone when they change or every 30 min.</summary>
    private void WarmUpLive(ArbitrageOptions snap, decimal lev)
    {
        var allowed = ParseVenues(snap.ScalpVenues);
        var venues = Venues.All.Where(v => VenueTradable(v, allowed, true)).ToList();
        var symbols = _markets.Symbols.ToList();
        var key = string.Join(",", venues) + "|" + lev + "|" + string.Join(",", symbols);
        if (key == _warmKey && DateTime.UtcNow - _warmAtUtc < TimeSpan.FromMinutes(30)) return;
        if (Interlocked.Exchange(ref _warming, 1) == 1) return;
        _warmKey = key; _warmAtUtc = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var v in venues)
                    await _orders.PrepareScalpVenueAsync(v, symbols, lev, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Scalp live warm-up failed"); }
            finally { Interlocked.Exchange(ref _warming, 0); }
        });
    }

    private static string[] UnknownVenues(string? csv) { Venues.NormalizeCsv(csv, out var u); return u; }

    private bool VenueTradable(string exchange, HashSet<string>? allowed, bool liveMode)
        => (allowed == null || allowed.Contains(exchange))
           && (!liveMode || (_orders.HasTradeCredentials(exchange)
               && (_runtime.Snapshot.LiveAllowedExchanges is not { Count: > 0 } la
                   || la.Contains(exchange, StringComparer.OrdinalIgnoreCase))));

    private (string Symbol, LagSignal Signal)? Pick(decimal notional, ArbitrageOptions snap)
    {
        (string, LagSignal)? best = null;
        var now = Environment.TickCount64;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allowed = ParseVenues(snap.ScalpVenues);
        var liveMode = LiveScalping;
        int watched = 0, warming = 0, noMove = 0, fewVenues = 0;
        (string Sym, string Ex, decimal Dev, decimal Move, decimal Width, decimal Ratio)? near = null;
        foreach (var symbol in _markets.Symbols)
        {
            var move = MarketMove(symbol);
            if (move is null) { noMove++; continue; }
            var q = Quotes(symbol);
            var live = q.Where(x => x.Fresh).ToList();
            if (live.Count < 3) { fewVenues++; continue; }
            foreach (var v in live)
            {
                // The lag is measured against every venue, but we only trade on allowed venues
                // and, for real orders, only where trade keys exist.
                if (!VenueTradable(v.Exchange, allowed, liveMode)) continue;
                watched++;
                var (fair, ready) = FairOffset(symbol, v.Exchange);
                if (!ready) { warming++; continue; }
                var r = LeadLag.ReferenceMid(live, v.Exchange);
                if (r is not { } rm) continue;
                {
                    var dev0 = LeadLag.OffsetPercent(v.Mid, rm) - fair;
                    var th0 = Math.Max(0.05m, snap.ScalpEntryPercent);
                    var ratio = Math.Abs(dev0) / th0;
                    if (near == null || ratio > near.Value.Ratio)
                        near = (symbol, v.Exchange, dev0, move.Value, v.WidthPercent, ratio);
                }
                var fee = ArbEconomics.TakerPercent(snap.EstimatedTakerFees, v.Exchange);
                var sig = LeadLag.Evaluate(v, rm, fair, move.Value, snap.ScalpEntryPercent, fee, snap.ScalpMinNetPercent);
                var key = symbol + "|" + v.Exchange;
                if (sig == null) { _signalSince.TryRemove(key, out _); continue; }
                seen.Add(key);
                // The lag must hold for two looks at least 300 ms apart: a one-tick print is noise.
                if (!_signalSince.TryGetValue(key, out var s0) || s0.IsLong != sig.IsLong)
                { _signalSince[key] = (sig.IsLong, now); continue; }
                if (now - s0.SinceMs < 300) continue;
                var touchQty = sig.IsLong
                    ? _market.GetBookTickers(symbol).GetValueOrDefault(v.Exchange)?.AskQuantity ?? 0m
                    : _market.GetBookTickers(symbol).GetValueOrDefault(v.Exchange)?.BidQuantity ?? 0m;
                if (touchQty * sig.EntryPrice < notional) continue;
                if (SymbolBusyForScalp(symbol)) { _lastNote = $"{symbol}: занят арбитражем"; continue; }
                if (best == null || sig.ExpectedNetPercent > best.Value.Item2.ExpectedNetPercent) best = (symbol, sig);
            }
        }
        foreach (var k in _signalSince.Keys) if (!seen.Contains(k)) _signalSince.TryRemove(k, out _);
        {
            var th = Math.Max(0.05m, snap.ScalpEntryPercent);
            string? why = null;
            if (near is { } n)
            {
                var lagOk = Math.Abs(n.Dev) >= th;
                var moveOk = n.Dev < 0 ? n.Move >= th / 2m : n.Move <= -th / 2m;
                why = !lagOk ? $"отставание {Math.Abs(n.Dev):F3}% меньше порога {th:F2}%"
                    : !moveOk ? $"рынок сдвинулся на {n.Move:F3}%, нужно {(n.Dev < 0 ? "+" : "−")}{th / 2m:F3}%"
                    : n.Width > 0.12m ? $"стакан широкий {n.Width:F3}%"
                    : "не покрывает комиссии, держится меньше 300 мс или мало объёма на лучшей цене";
            }
            _watch = new
            {
                symbols = _markets.Symbols.Count(),
                watched, warming, noMove, fewVenues,
                thresholdPercent = th,
                nearest = near is { } m ? new
                {
                    symbol = m.Sym, exchange = m.Ex, lagPercent = m.Dev, marketMovePercent = m.Move,
                    widthPercent = m.Width, ofThreshold = m.Ratio, why
                } : null,
                utc = DateTime.UtcNow
            };
        }
        if (best is { } b)
            _lastNote = $"{b.Item1} {b.Item2.Exchange} {(b.Item2.IsLong ? "long" : "short")}: отставание {b.Item2.DeviationPercent:F3}%, рынок {b.Item2.MarketMovePercent:F3}%";
        return best;
    }

    // ---------------- paper entry with latency ----------------

    private void ResolvePending()
    {
        List<PendingEntry> due;
        lock (_lock)
        {
            due = _pending.Where(p => DateTime.UtcNow >= p.DueUtc).ToList();
            foreach (var d in due) _pending.Remove(d);
        }
        foreach (var p in due)
        {
            var tk = _market.GetBookTickers(p.Symbol).GetValueOrDefault(p.Exchange);
            var px = tk == null ? 0m : p.IsLong ? tk.BestAsk : tk.BestBid;
            var live = _market.QuoteLive(p.Exchange, p.Symbol);
            var ok = live && px > 0 && (p.IsLong ? px <= p.Limit : px >= p.Limit) && !SymbolBusyForScalpExceptSelf(p.Symbol);
            if (!ok)
            {
                PauseSymbol(p.Symbol, 3000);
                _logger.LogInformation("SCALP paper miss {Sym} {Ex}: price moved past limit before the order landed", p.Symbol, p.Exchange);
                continue;
            }
            var qty = LiveOrderEngine.RoundBaseQty(p.NotionalUsd / px, px);
            var pos = NewPosition(p.Symbol, p.Signal, qty > 0 ? qty : p.Qty, px, live: false, p.FeePercent, p.NotionalUsd, p.Leverage, p.MarketMid);
            lock (_lock) { _open.Add(pos); SaveLedgerUnlocked(); }
            _logger.LogInformation("SCALP PAPER {Side} {Sym} on {Ex} qty={Q} @ {P} | {Note}",
                pos.Side, pos.Symbol, pos.Exchange, pos.Qty, pos.Entry, pos.Note);
        }
    }

    private bool SymbolBusyForScalpExceptSelf(string symbol)
        => _exposure.IsArbReserved(symbol)
           || _paper.GetOpenPositions().Any(p => p.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
           || _orders.GetOpen().Any(p => p.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));

    private static ScalpPosition NewPosition(string symbol, LagSignal s, decimal qty, decimal entry, bool live,
        decimal feePct, decimal notional, decimal lev, decimal marketMid)
        => new()
        {
            Id = Guid.NewGuid(),
            Symbol = symbol,
            Exchange = s.Exchange,
            Side = s.IsLong ? "Long" : "Short",
            IsLong = s.IsLong,
            Qty = qty,
            Entry = entry,
            OpenedAt = DateTime.UtcNow,
            Live = live,
            FeePercent = feePct,
            NotionalUsd = notional,
            Leverage = lev,
            EntryMarketMid = marketMid,
            ExpectedNetPercent = s.ExpectedNetPercent,
            Note = $"lag {s.DeviationPercent:F3}% market {s.MarketMovePercent:F3}% exp {s.ExpectedNetPercent:F3}%"
        };

    // ---------------- marks and exits ----------------

    private void MarkAndMaybeClose()
    {
        List<ScalpPosition> open;
        lock (_lock) open = _open.ToList();
        var snap = _runtime.Snapshot;
        foreach (var pos in open)
        {
            if (pos.NeedsReconciliation || pos.Qty <= 0 || pos.Closing) continue;
            var tk = _market.GetBookTickers(pos.Symbol).GetValueOrDefault(pos.Exchange);
            if (tk == null || tk.BestBid <= 0 || tk.BestAsk <= 0)
            {
                pos.NoBookSinceUtc ??= DateTime.UtcNow;
                if ((DateTime.UtcNow - pos.NoBookSinceUtc.Value).TotalSeconds < 20) continue;
                lock (_lock)
                {
                    if (!_open.Contains(pos) || pos.Closing) continue;
                    pos.Closing = true;
                    pos.Status = "ClosingNoBook";
                    SaveLedgerUnlocked();
                }
                var last = pos.LastMarkPrice > 0 ? pos.LastMarkPrice : pos.Entry;
                _ = CloseAsync(pos, last, LeadLag.NetUsd(pos.IsLong, pos.Entry, last, pos.Qty, pos.FeePercent), "no-book");
                continue;
            }
            pos.NoBookSinceUtc = null;
            var mark = pos.IsLong ? tk.BestBid : tk.BestAsk;
            pos.LastMarkPrice = mark;
            var net = (pos.RealizedUsd ?? 0m) + LeadLag.NetUsd(pos.IsLong, pos.Entry, mark, pos.Qty, pos.FeePercent);
            pos.UnrealizedUsd = net;

            var live = Quotes(pos.Symbol).Where(x => x.Fresh).ToList();
            var r = LeadLag.ReferenceMid(live, pos.Exchange);
            var (fair, _) = FairOffset(pos.Symbol, pos.Exchange);
            var dev = r is { } rm ? LeadLag.OffsetPercent((tk.BestBid + tk.BestAsk) / 2m, rm) - fair : (pos.IsLong ? -1m : 1m);
            var medNow = MedianNow(pos.Symbol);
            var moveSince = medNow is { } mn && pos.EntryMarketMid > 0 ? (mn - pos.EntryMarketMid) / pos.EntryMarketMid * 100m : 0m;
            var why = LeadLag.ExitReason(pos.IsLong, pos.Entry, mark, net,
                pos.NotionalUsd > 0 ? pos.NotionalUsd : pos.Entry * pos.Qty, dev, moveSince,
                (DateTime.UtcNow - pos.OpenedAt).TotalSeconds,
                snap.ScalpStopPercent, snap.ScalpMaxHoldSeconds, pos.ExpectedNetPercent > 0 ? pos.ExpectedNetPercent : 0.1m);
            if (why == null) continue;
            lock (_lock)
            {
                if (!_open.Contains(pos) || pos.Closing) continue;
                pos.Closing = true;
                pos.Status = "Closing";
                SaveLedgerUnlocked();
            }
            _ = CloseAsync(pos, mark, net, why);
        }
    }

    private async Task CloseAsync(ScalpPosition pos, decimal mark, decimal net, string why)
    {
        var fillQty = pos.Qty;
        var exitPrice = mark;
        if (pos.Live)
        {
            LiveLegOrderResult closed;
            try
            {
                closed = await _orders.CloseScalpLegAsync(pos.Exchange, pos.Symbol, pos.IsLong, pos.Qty, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _guard.Kill($"Scalp close interrupted for {pos.Symbol} {pos.Exchange}");
                lock (_lock)
                {
                    pos.NeedsReconciliation = true;
                    pos.Status = "ReconciliationRequired";
                    pos.Note = $"{pos.Note} | close path interrupted: {ex.Message}";
                    SaveLedgerUnlocked();
                }
                return;
            }
            if (closed.Accepted && !closed.Verified)
            {
                lock (_lock)
                {
                    pos.NeedsReconciliation = true;
                    pos.Status = "ReconciliationRequired";
                    pos.Note = $"{pos.Note} | close state unknown: {closed.Error}";
                    SaveLedgerUnlocked();
                }
                return;
            }
            if (closed.FilledBaseQty <= 0)
            {
                lock (_lock)
                {
                    pos.Closing = false;
                    pos.Status = "Open";
                    SaveLedgerUnlocked();
                }
                _logger.LogWarning("SCALP close did not fill {Sym} {Ex}: {Err}", pos.Symbol, pos.Exchange, closed.Error);
                return;
            }
            if (closed.AveragePrice <= 0)
            {
                _guard.Kill($"Scalp close execution price missing for {pos.Symbol} {pos.Exchange}");
                lock (_lock)
                {
                    pos.NeedsReconciliation = true;
                    pos.Status = "ReconciliationRequired";
                    SaveLedgerUnlocked();
                }
                return;
            }
            fillQty = Math.Min(pos.Qty, closed.FilledBaseQty);
            exitPrice = closed.AveragePrice;
        }

        var grossClosed = pos.IsLong
            ? (exitPrice - pos.Entry) * fillQty
            : (pos.Entry - exitPrice) * fillQty;
        var closeNet = grossClosed - (pos.Entry + exitPrice) * fillQty * pos.FeePercent / 100m;
        // Scalp PnL stays in the scalp wallet; it does not consume the hedge's daily loss limit.
        pos.RealizedUsd = (pos.RealizedUsd ?? 0m) + closeNet;
        pos.Qty = Math.Max(0m, pos.Qty - fillQty);
        pos.Note = (pos.Note ?? "") + " | " + why;
        var flat = pos.Qty <= Math.Max(0.0000000001m, fillQty * 0.000001m);
        lock (_lock)
        {
            pos.Closing = false;
            if (flat)
            {
                pos.Qty = 0m;
                pos.Exit = exitPrice;
                pos.ClosedAt = DateTime.UtcNow;
                pos.IsOpen = false;
                pos.Status = "Closed";
                _open.RemoveAll(p => p.Id == pos.Id);
                _closed.Insert(0, pos);
                if (_closed.Count > 200) _closed.RemoveRange(200, _closed.Count - 200);
            }
            else
            {
                pos.Status = "OpenPartialClose";
                pos.Note += $" | remaining qty={pos.Qty}";
            }
            SaveLedgerUnlocked();
        }
        _logger.LogInformation("SCALP close {Side} {Sym} {Ex} {Why} pnl={Pnl:F2}",
            pos.Side, pos.Symbol, pos.Exchange, why, closeNet);
    }

    public sealed class ScalpPosition
    {
        public Guid Id { get; set; }
        public string Symbol { get; set; } = "";
        public string Exchange { get; set; } = "";
        public string Side { get; set; } = "";
        public bool IsLong { get; set; }
        public decimal Qty { get; set; }
        public decimal Entry { get; set; }
        public decimal LastMarkPrice { get; set; }
        public decimal? Exit { get; set; }
        public DateTime OpenedAt { get; set; }
        public DateTime? ClosedAt { get; set; }
        public bool Live { get; set; }
        public bool IsOpen { get; set; } = true;
        public bool Closing { get; set; }
        public bool NeedsReconciliation { get; set; }
        public string Status { get; set; } = "Open";
        public string? OrderId { get; set; }
        public decimal FeePercent { get; set; }
        public decimal UnrealizedUsd { get; set; }
        public decimal? RealizedUsd { get; set; }
        public string? Note { get; set; }
        public decimal NotionalUsd { get; set; }
        public decimal Leverage { get; set; } = 1m;
        public decimal EntryMarketMid { get; set; }
        public decimal ExpectedNetPercent { get; set; }
        public DateTime? NoBookSinceUtc { get; set; }
    }

    private sealed record PendingEntry(
        string Symbol, string Exchange, bool IsLong, decimal Qty, decimal Limit, decimal FeePercent,
        decimal NotionalUsd, decimal Leverage, LagSignal Signal, decimal MarketMid, DateTime DueUtc);

    private void LoadLedger()
    {
        try
        {
            if (!File.Exists(_ledgerPath)) return;
            var ledger = JsonSerializer.Deserialize<ScalpLedgerFile>(File.ReadAllText(_ledgerPath), LedgerJson);
            if (ledger == null) return;
            _open.AddRange((ledger.Open ?? []).Where(p => p.IsOpen));
            _closed.AddRange((ledger.Closed ?? []).OrderByDescending(p => p.ClosedAt).Take(200));
            var needsReview = false;
            foreach (var position in _open)
            {
                if (position.Closing || position.Status is "Opening" or "Closing")
                {
                    position.Closing = false;
                    position.NeedsReconciliation = true;
                    position.Status = "ReconciliationRequired";
                    position.Note = $"{position.Note} | process restarted during order operation";
                }
                needsReview |= position.NeedsReconciliation;
            }
            if (needsReview)
            {
                _guard.Kill("Scalp orders require reconciliation after restart");
                SaveLedgerUnlocked();
            }
            _logger.LogWarning("Loaded scalp ledger: open={Open}, closed={Closed}, reconciliation={Review}",
                _open.Count, _closed.Count, needsReview);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load scalp ledger; killing live orders for safety");
            _guard.Kill("Scalp ledger could not be loaded");
        }
    }

    private void SaveLedgerUnlocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_ledgerPath)!);
            var temp = _ledgerPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new ScalpLedgerFile(_open.ToList(), _closed.ToList()), LedgerJson));
            File.Move(temp, _ledgerPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not persist scalp ledger");
            foreach (var position in _open.Where(p => p.Live))
            {
                position.NeedsReconciliation = true;
                position.Status = "ReconciliationRequired";
            }
            _guard.Kill("Scalp ledger persistence failed");
        }
    }


    private sealed record ScalpLedgerFile(List<ScalpPosition>? Open, List<ScalpPosition>? Closed);
}
