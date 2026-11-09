using System.Collections.Concurrent;
using System.Text.Json;
using ArbitrageBot.Configuration;
using ArbitrageBot.Models;

namespace ArbitrageBot.Services;

/// <summary>
/// Directional scalp alongside the hedge. One leg, one venue.
/// Side and venue come from the live books: touch imbalance that holds,
/// a mid move in the same direction, and agreement from the other venues.
/// Entry is a limit one tick through the touch. Exit when the plus covers
/// the fee bill, or when the imbalance that justified the trade dies.
/// </summary>
public sealed class ScalpEngine
{
    private readonly IFuturesMarketService _market;
    private readonly ActiveMarketContext _markets;
    private readonly RuntimeRiskConfig _runtime;
    private readonly LiveOrderEngine _orders;
    private readonly IFuturesPaperService _paper;
    private readonly LiveTradingGuard _guard;
    private readonly ILogger<ScalpEngine> _logger;
    private readonly string _ledgerPath;
    private static readonly JsonSerializerOptions LedgerJson = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly List<ScalpPosition> _open = [];
    private readonly List<ScalpPosition> _closed = [];
    private readonly ConcurrentDictionary<string, MidSample> _mids = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _imbalanceSince = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastOpenUtc = DateTime.MinValue;

    public ScalpEngine(
        IFuturesMarketService market,
        ActiveMarketContext markets,
        RuntimeRiskConfig runtime,
        LiveOrderEngine orders,
        IFuturesPaperService paper,
        LiveTradingGuard guard,
        IWebHostEnvironment env,
        ILogger<ScalpEngine> logger)
    {
        _market = market;
        _markets = markets;
        _runtime = runtime;
        _orders = orders;
        _paper = paper;
        _guard = guard;
        _ledgerPath = Path.Combine(env.ContentRootPath, "data", "live", "scalp-ledger.json");
        _logger = logger;
        LoadLedger();
    }

    public object Snapshot()
    {
        lock (_lock)
        {
            return new
            {
                enabled = _runtime.Snapshot.SpatialScalpMode,
                open = _open.Select(p => new
                {
                    p.Id, p.Symbol, p.Exchange, p.Side, p.Qty, p.Entry, p.OpenedAt,
                    p.UnrealizedUsd, p.Note, p.Status, p.NeedsReconciliation
                }),
                closed = _closed.Take(40).Select(p => new
                {
                    p.Id, p.Symbol, p.Exchange, p.Side, p.Qty, p.Entry, p.Exit,
                    p.OpenedAt, p.ClosedAt, p.RealizedUsd, p.Note
                }),
                openCount = _open.Count,
                realizedUsd = _closed.Where(p => p.RealizedUsd.HasValue).Sum(p => p.RealizedUsd!.Value)
            };
        }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        // Always manage existing positions, even after the entry toggle is disabled.
        MarkAndMaybeClose();
        if (ct.IsCancellationRequested || !_runtime.Snapshot.SpatialScalpMode) return;

        var snap = _runtime.Snapshot;
        var scalpDailyLossCap = Math.Max(1m,
            (snap.LiveEquityPerExchangeUsd > 0 ? snap.LiveEquityPerExchangeUsd : snap.PaperStartingQuote) * 0.01m);
        decimal realizedToday;
        lock (_lock)
            realizedToday = _closed.Where(p => p.ClosedAt?.Date == DateTime.UtcNow.Date)
                .Sum(p => p.RealizedUsd ?? 0m);
        if (realizedToday <= -scalpDailyLossCap)
        {
            _logger.LogWarning("SCALP entry circuit open: today's realized={Pnl:F2} limit=-{Limit:F2}",
                realizedToday, scalpDailyLossCap);
            return;
        }

        var candidate = Pick();
        if (candidate == null) return;

        lock (_lock)
        {
            if (_open.Count >= 1) return;
            if (_open.Any(p => p.Symbol.Equals(candidate.Symbol, StringComparison.OrdinalIgnoreCase))) return;
            if (!_guard.CanPlaceOrders
                && _paper.OpenCount + _open.Count(p => !p.Live) >= Math.Max(1, _runtime.Snapshot.FuturesMaxOpenPositions))
                return;
        }
        if ((DateTime.UtcNow - _lastOpenUtc).TotalSeconds < 60) return;

        var notional = snap.QuoteSize > 0 ? snap.QuoteSize : 100m;
        if (snap.FuturesMaxNotionalUsd > 0) notional = Math.Min(notional, snap.FuturesMaxNotionalUsd);
        if (_guard.CanPlaceOrders)
        {
            if (snap.LiveMaxNotionalUsd > 0) notional = Math.Min(notional, snap.LiveMaxNotionalUsd);
            var leverage = Math.Clamp(snap.LivePaperLeverage > 0 ? snap.LivePaperLeverage : 2m, 1m, 10m);
            var usage = Math.Clamp(snap.LiveMarginUsageFraction > 0 ? snap.LiveMarginUsageFraction : 0.5m, 0.15m, 0.5m);
            var equity = snap.LiveEquityPerExchangeUsd > 0 ? snap.LiveEquityPerExchangeUsd : 100m;
            notional = Math.Min(notional, equity * leverage * usage);
        }
        if (notional < 20m || candidate.TouchDepthUsd < notional * 1.25m) return;
        var qty = LiveOrderEngine.RoundBaseQty(notional / candidate.Touch, candidate.Touch);
        if (qty <= 0) return;

        var fee = ArbEconomics.TakerPercent(snap.EstimatedTakerFees, candidate.Exchange);
        var live = _guard.CanPlaceOrders;

        if (live)
        {
            int scalpOpen;
            lock (_lock) scalpOpen = _open.Count(p => p.Live);
            var risk = _guard.CheckOpenAllowed(_orders.GetOpen().Count + scalpOpen, notional);
            if (!risk.ok) return;
        }

        if (live)
        {
            var pending = NewPosition(candidate, qty, candidate.Touch, live: true, null, fee);
            pending.Status = "Opening";
            pending.NeedsReconciliation = true;
            lock (_lock)
            {
                _open.Add(pending);
                SaveLedgerUnlocked();
            }
            UpdateAdditionalOpenPositions();
            var limit = LiveOrderEngine.SnapLimit(candidate.Touch, candidate.Touch, candidate.IsLong);
            var lev = snap.LivePaperLeverage > 0 ? snap.LivePaperLeverage : 3m;
            LiveLegOrderResult sent;
            try
            {
                sent = await _orders.PlaceScalpLegAsync(
                    candidate.Exchange, candidate.Symbol, candidate.IsLong, qty, limit, lev, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                pending.Status = "ReconciliationRequired";
                pending.Note = $"{pending.Note} | order path interrupted: {ex.Message}";
                _guard.Kill($"Scalp entry interrupted for {candidate.Symbol} {candidate.Exchange}");
                lock (_lock) SaveLedgerUnlocked();
                return;
            }
            if (sent.Verified && sent.FilledBaseQty <= 0)
            {
                lock (_lock)
                {
                    _open.Remove(pending);
                    SaveLedgerUnlocked();
                }
                UpdateAdditionalOpenPositions();
                return;
            }
            if (!sent.Verified && !sent.Accepted)
            {
                lock (_lock)
                {
                    _open.Remove(pending);
                    SaveLedgerUnlocked();
                }
                UpdateAdditionalOpenPositions();
                _logger.LogDebug("SCALP skip {Sym} {Ex}: {Err}", candidate.Symbol, candidate.Exchange, sent.Error);
                return;
            }
            if (!sent.Verified)
            {
                pending.Status = "ReconciliationRequired";
                pending.NeedsReconciliation = true;
                pending.OrderId = sent.Id;
                pending.Note = $"{pending.Note} | order state unknown: {sent.Error}";
                lock (_lock) SaveLedgerUnlocked();
                return;
            }

            if (sent.FilledBaseQty > 0 && sent.AveragePrice <= 0)
            {
                pending.Status = "ReconciliationRequired";
                pending.Note = $"{pending.Note} | fill price missing";
                _guard.Kill($"Scalp fill price missing for {candidate.Symbol} {candidate.Exchange}");
                lock (_lock) SaveLedgerUnlocked();
                return;
            }

            pending.Qty = sent.FilledBaseQty;
            pending.Entry = sent.AveragePrice;
            pending.OrderId = sent.Id;
            pending.NeedsReconciliation = false;
            pending.Status = sent.Ok ? "Open" : "OpenPartialFill";
            lock (_lock) SaveLedgerUnlocked();
            _lastOpenUtc = DateTime.UtcNow;
            return;
        }

        Remember(candidate, qty, candidate.Touch, live: false, null, fee);
    }

    private void Remember(ScalpCandidate c, decimal qty, decimal entry, bool live, string? orderId, decimal feePct)
    {
        var pos = NewPosition(c, qty, entry, live, orderId, feePct);
        lock (_lock)
        {
            _open.Add(pos);
            SaveLedgerUnlocked();
        }
        UpdateAdditionalOpenPositions();
        _lastOpenUtc = DateTime.UtcNow;
        _logger.LogInformation("SCALP {Mode} {Side} {Sym} on {Ex} qty={Q} @ {P} | {Note}",
            live ? "LIVE" : "PAPER", pos.Side, pos.Symbol, pos.Exchange, qty, entry, pos.Note);
    }

    private static ScalpPosition NewPosition(ScalpCandidate c, decimal qty, decimal entry, bool live, string? orderId, decimal feePct)
        => new()
        {
            Id = Guid.NewGuid(),
            Symbol = c.Symbol,
            Exchange = c.Exchange,
            Side = c.IsLong ? "Long" : "Short",
            IsLong = c.IsLong,
            Qty = qty,
            Entry = entry,
            OpenedAt = DateTime.UtcNow,
            Live = live,
            OrderId = orderId,
            FeePercent = feePct,
            Note = $"OBI {c.Imbalance:F2} agree {c.Agreement} mid {c.MidMovePct:F3}%"
        };

    private void MarkAndMaybeClose()
    {
        List<ScalpPosition> open;
        lock (_lock) open = _open.ToList();
        foreach (var pos in open)
        {
            if (pos.NeedsReconciliation || pos.Qty <= 0) continue;
            var books = _market.GetBookTickers(pos.Symbol);
            if (!books.TryGetValue(pos.Exchange, out var tk) || tk.BestBid <= 0 || tk.BestAsk <= 0)
            {
                var noBookAge = (DateTime.UtcNow - pos.OpenedAt).TotalSeconds;
                if (noBookAge >= 90 && pos.Live)
                {
                    lock (_lock)
                    {
                        if (!_open.Contains(pos) || pos.Closing) continue;
                        pos.Closing = true;
                        pos.Status = "ClosingStaleBook";
                        SaveLedgerUnlocked();
                    }
                    _ = CloseAsync(pos, pos.LastMarkPrice > 0 ? pos.LastMarkPrice : pos.Entry,
                        pos.UnrealizedUsd, "hard-timeout-no-book");
                }
                else if (noBookAge >= 90 && pos.LastMarkPrice > 0)
                {
                    lock (_lock)
                    {
                        if (!_open.Contains(pos) || pos.Closing) continue;
                        pos.Closing = true;
                        pos.Status = "ClosingStaleBookPaper";
                        SaveLedgerUnlocked();
                    }
                    _ = CloseAsync(pos, pos.LastMarkPrice, pos.UnrealizedUsd, "hard-timeout-stale-mark");
                }
                continue;
            }

            var mark = pos.IsLong ? tk.BestBid : tk.BestAsk;
            pos.LastMarkPrice = mark;
            var gross = pos.IsLong
                ? (mark - pos.Entry) * pos.Qty
                : (pos.Entry - mark) * pos.Qty;
            var fees = pos.Entry * pos.Qty * pos.FeePercent / 100m * 2m;
            var net = (pos.RealizedUsd ?? 0m) + gross - fees;
            pos.UnrealizedUsd = net;

            var obi = TouchImbalance(tk);
            var flipped = pos.IsLong ? obi < -0.15m : obi > 0.15m;
            var age = (DateTime.UtcNow - pos.OpenedAt).TotalSeconds;
            // Target is net of round-trip fees above; leave enough room for fee and exit cross.
            var target = Math.Max(0.10m, pos.Entry * pos.Qty * 0.0015m);
            var stop = -Math.Max(0.25m, target * 1.4m);

            string? why = null;
            if (net >= target) why = "target";
            else if (flipped && age >= 2) why = "imbalance died";
            else if (net <= stop) why = "stop";
            else if (age >= 45 && net > 0) why = "time lock";
            else if (age >= 90) why = "time flat";

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
        if (pos.Live) _guard.RecordRealized(closeNet);
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
        if (flat) UpdateAdditionalOpenPositions();
        _logger.LogInformation("SCALP close {Side} {Sym} {Ex} {Why} pnl={Pnl:F2}",
            pos.Side, pos.Symbol, pos.Exchange, why, closeNet);
    }

    private ScalpCandidate? Pick()
    {
        ScalpCandidate? best = null;
        foreach (var symbol in _markets.Symbols)
        {
            var books = _market.GetBookTickers(symbol);
            if (books.Count == 0) continue;

            var signs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (ex, tk) in books)
            {
                if (tk.BestBid <= 0 || tk.BestAsk <= 0) continue;
                var obi = TouchImbalance(tk);
                if (Math.Abs(obi) < 0.35m) continue;
                signs[ex] = obi > 0 ? 1 : -1;
                NoteMid(symbol, ex, (tk.BestBid + tk.BestAsk) / 2m);
            }
            if (signs.Count == 0) continue;

            foreach (var (ex, sign) in signs)
            {
                var tk = books[ex];
                var obi = TouchImbalance(tk);
                var key = symbol + "|" + ex;
                if (!_imbalanceSince.TryGetValue(key, out var since) || Math.Sign(obi) != sign)
                {
                    _imbalanceSince[key] = DateTime.UtcNow;
                    continue;
                }
                if ((DateTime.UtcNow - since).TotalMilliseconds < 800) continue;

                var midMove = MidMovePct(symbol, ex);
                if (Math.Sign(midMove) != sign || Math.Abs(midMove) < 0.01m) continue;

                var agree = signs.Values.Count(s => s == sign);
                if (agree < 2) continue;
                var width = ArbEconomics.BookSpreadPercent(tk.BestBid, tk.BestAsk);
                if (width > 0.12m) continue;

                var depthBias = DepthBias(symbol, ex);
                if (Math.Sign(depthBias) == -sign && Math.Abs(depthBias) > 0.2m) continue;

                var score = Math.Abs(obi) * agree * (1m / (1m + width * 10m));
                var touch = sign > 0 ? tk.BestAsk : tk.BestBid;
                if (best == null || score > best.Score)
                {
                    best = new ScalpCandidate
                    {
                        Symbol = symbol,
                        Exchange = ex,
                        IsLong = sign > 0,
                        Imbalance = obi,
                        Agreement = agree,
                        MidMovePct = midMove,
                        Touch = touch,
                        TouchDepthUsd = (sign > 0 ? tk.AskQuantity : tk.BidQuantity) * touch,
                        Score = score
                    };
                }
            }
        }
        return best;
    }

    private decimal DepthBias(string symbol, string exchange)
    {
        var depth = _market.GetDepth(symbol, 8);
        if (!depth.TryGetValue(exchange, out var raw)) return 0;
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(raw);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            decimal bid = 0, ask = 0;
            if (doc.RootElement.TryGetProperty("bids", out var bids))
                foreach (var b in bids.EnumerateArray())
                    if (b.TryGetProperty("qty", out var q) && q.TryGetDecimal(out var qv)) bid += qv;
            if (doc.RootElement.TryGetProperty("asks", out var asks))
                foreach (var a in asks.EnumerateArray())
                    if (a.TryGetProperty("qty", out var q) && q.TryGetDecimal(out var qv)) ask += qv;
            var sum = bid + ask;
            return sum <= 0 ? 0 : (bid - ask) / sum;
        }
        catch
        {
            return 0;
        }
    }

    private static decimal TouchImbalance(BookTicker tk)
    {
        var bid = tk.BidQuantity;
        var ask = tk.AskQuantity;
        var sum = bid + ask;
        return sum <= 0 ? 0 : (bid - ask) / sum;
    }

    private void NoteMid(string symbol, string exchange, decimal mid)
    {
        var key = symbol + "|" + exchange;
        var now = Environment.TickCount64;
        var s = _mids.GetOrAdd(key, _ => new MidSample());
        if (s.LastMs > 0 && now - s.LastMs < 200) return;
        s.Prev = s.Last > 0 ? s.Last : mid;
        s.PrevMs = s.LastMs;
        s.Last = mid;
        s.LastMs = now;
    }

    private decimal MidMovePct(string symbol, string exchange)
    {
        var key = symbol + "|" + exchange;
        if (!_mids.TryGetValue(key, out var s) || s.Prev <= 0 || s.Last <= 0) return 0;
        if (Environment.TickCount64 - s.PrevMs > 4000) return 0;
        return (s.Last - s.Prev) / s.Prev * 100m;
    }

    private sealed class MidSample
    {
        public decimal Prev;
        public decimal Last;
        public long PrevMs;
        public long LastMs;
    }

    private sealed class ScalpCandidate
    {
        public string Symbol { get; set; } = "";
        public string Exchange { get; set; } = "";
        public bool IsLong { get; set; }
        public decimal Imbalance { get; set; }
        public int Agreement { get; set; }
        public decimal MidMovePct { get; set; }
        public decimal Touch { get; set; }
        public decimal TouchDepthUsd { get; set; }
        public decimal Score { get; set; }
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
    }

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
            UpdateAdditionalOpenPositions();
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

    private void UpdateAdditionalOpenPositions()
    {
        int n;
        lock (_lock) n = _open.Count(p => p.Live && p.IsOpen);
        _guard.SetAdditionalOpenPositions(n);
    }

    private sealed record ScalpLedgerFile(List<ScalpPosition>? Open, List<ScalpPosition>? Closed);
}
