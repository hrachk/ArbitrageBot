using System.Text.Json;
using ArbitrageBot.Configuration;

namespace ArbitrageBot.Services;

/// <summary>
/// Order-book density scalper ("плотности"), modelled on how manual DOM scalpers (MetaScalp,
/// CScalp) trade market-maker walls on volatile perps:
///
///  1. find a large resting order (wall) near the price that has stood long enough not to be bait;
///  2. when price comes close, rest a passive limit one tick in front of it — the wall is the
///     reason the price should bounce, and it is the stop: if it is eaten or pulled, get out;
///  3. take a small move away from the wall with a resting maker order; time-stop otherwise.
///
/// Paper only for now, with a pessimistic fill model: an entry or take-profit fills only when the
/// market trades through our price (a touch is not a fill), orders become active only after the
/// configured order latency, and market exits are priced at the book one latency later. That is
/// the opposite of the optimistic model that made the lag scalper look profitable in paper.
/// </summary>
public sealed class DensityScalper
{
    private readonly IFuturesMarketService _market;
    private readonly ActiveMarketContext _markets;
    private readonly RuntimeRiskConfig _runtime;
    private readonly ILogger<DensityScalper> _logger;
    private readonly string _ledgerPath;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly Dictionary<string, WallTrack> _tracks = new(StringComparer.OrdinalIgnoreCase);
    private Resting? _resting;
    private Position? _pos;
    private readonly List<Trade> _closed = [];
    private DateTime _lastCloseUtc = DateTime.MinValue;
    private string _lastNote = "";
    private int _placed, _filled, _cancelled;
    private readonly Dictionary<string, int> _cancelReasons = new();

    public DensityScalper(IFuturesMarketService market, ActiveMarketContext markets, RuntimeRiskConfig runtime,
        IWebHostEnvironment env, ILogger<DensityScalper> logger)
    {
        _market = market;
        _markets = markets;
        _runtime = runtime;
        _logger = logger;
        _ledgerPath = Path.Combine(env.ContentRootPath, "data", "paper", "density-ledger.json");
        Load();
    }

    public bool Active => _runtime.Snapshot.SpatialScalpMode && _runtime.Snapshot.ScalpStrategy == "density";

    // ------------------------------------------------------------------ tick

    public void Tick()
    {
        var snap = _runtime.Snapshot;
        var now = DateTime.UtcNow;
        var venues = Venues.AllowSet(snap.ScalpVenues)?.ToList() ?? ["Binance"];
        var latency = TimeSpan.FromMilliseconds(Math.Max(0, snap.PaperFillLatencyMs));

        lock (_lock)
        {
            // 1) Walls: refresh tracks for every symbol on the scalp venues.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ex in venues)
            foreach (var symbol in _markets.Symbols)
            {
                var book = _market.BookSides(ex, symbol);
                if (book is not { } b) continue;
                var bid = b.Bids[0].Price; var ask = b.Asks[0].Price;
                if (bid <= 0 || ask <= bid) continue;
                var mid = (bid + ask) / 2m;
                foreach (var w in DensityMath.FindWalls(b.Bids, true, mid, snap.DensityScanPercent, snap.DensityMinWallUsd, snap.DensityMinMultiple)
                             .Concat(DensityMath.FindWalls(b.Asks, false, mid, snap.DensityScanPercent, snap.DensityMinWallUsd, snap.DensityMinMultiple)))
                {
                    var key = $"{ex}|{symbol}|{(w.IsBid ? "B" : "A")}|{w.Price}";
                    seen.Add(key);
                    if (!_tracks.TryGetValue(key, out var t) || w.Qty < t.MaxQty * 0.5m)
                        _tracks[key] = t = new WallTrack(ex, symbol, w.IsBid, w.Price, now) { MaxQty = w.Qty };
                    t.MaxQty = Math.Max(t.MaxQty, w.Qty);
                    t.Qty = w.Qty; t.Notional = w.NotionalUsd; t.Multiple = w.Multiple; t.DistancePercent = w.DistancePercent;
                    t.LastSeen = now;
                }
            }
            foreach (var k in _tracks.Keys.ToList())
                if (!seen.Contains(k) && now - _tracks[k].LastSeen > TimeSpan.FromSeconds(2)) _tracks.Remove(k);

            if (_pos != null) { ManagePosition(snap, now, latency); return; }
            if (_resting != null) { ManageResting(snap, now); return; }
            if (!Active) return;

            // 2) Risk gates.
            var today = now.Date;
            var dayPnl = _closed.Where(c => c.ClosedAt.Date == today).Sum(c => c.PnlUsd);
            var lossCap = Math.Abs(snap.ScalpDailyLossUsd) > 0 ? Math.Abs(snap.ScalpDailyLossUsd) : 3m;
            if (dayPnl <= -lossCap) { _lastNote = $"дневной лимит убытка {lossCap:F2} достигнут"; return; }
            if ((now - _lastCloseUtc).TotalSeconds < Math.Max(0, snap.ScalpCooldownSeconds)) return;
            var notional = Math.Max(0m, snap.ScalpDepositUsd) * Math.Clamp(snap.ScalpLeverage, 1m, 5m);
            if (notional < 6m) { _lastNote = "депозит скальпа слишком мал"; return; }

            // 3) Pick the strongest mature wall that price is approaching.
            WallTrack? best = null;
            foreach (var t in _tracks.Values)
            {
                if ((now - t.FirstSeen).TotalSeconds < snap.DensityMinWallAgeSeconds) continue;
                if (t.DistancePercent > snap.DensityApproachPercent) continue;
                if (t.Notional < notional * 3m) continue; // our order must be small next to the wall
                if (best == null || t.Multiple > best.Multiple) best = t;
            }
            if (best == null) return;
            var bk = _market.BookSides(best.Exchange, best.Symbol);
            if (bk is not { } bb) return;
            var tick = DensityMath.InferTick(bb.Bids, bb.Asks);
            if (tick <= 0) return;
            var limit = DensityMath.FrontOf(new BookWall(best.IsBid, best.Price, best.Qty, best.Notional, best.Multiple, best.DistancePercent), tick);
            // Our price must still be away from the touch, or it would be a taker order.
            if (best.IsBid ? limit >= bb.Bids[0].Price : limit <= bb.Asks[0].Price) return;
            _resting = new Resting(best.Exchange, best.Symbol, best.IsBid, limit, best.Price, best.Qty, notional / limit, now, now + latency);
            _placed++;
            _lastNote = $"{best.Symbol} {(best.IsBid ? "лонг" : "шорт")} лимит {limit} перед плотностью {best.Price} (${best.Notional:N0}, ×{best.Multiple:F0})";
            _logger.LogInformation("DENSITY rest {Sym} {Side} @ {Px} in front of wall {Wall} ${N:N0} x{M:F0}",
                best.Symbol, best.IsBid ? "L" : "S", limit, best.Price, best.Notional, best.Multiple);
        }
    }

    private void ManageResting(ArbitrageOptions snap, DateTime now)
    {
        var r = _resting!;
        var book = _market.BookSides(r.Exchange, r.Symbol);
        if (book is not { } b) { Cancel("нет живого стакана"); return; }
        var bid = b.Bids[0].Price; var ask = b.Asks[0].Price; var mid = (bid + ask) / 2m;
        var crossed = DensityMath.RestingFilled(r.IsLong, r.Limit, bid, ask);
        if (now < r.ActiveAt)
        {
            // The order is still on its way; if price is already through, it would land as a taker. Skip.
            if (crossed) Cancel("цена прошла уровень раньше, чем дошёл ордер");
            return;
        }
        var sideLevels = r.IsLong ? b.Bids : b.Asks;
        var wallHolds = DensityMath.WallHolds(sideLevels, r.WallPrice, r.WallRefQty, 0.6m);
        if (crossed)
        {
            // Filled only if the wall was still there when price reached us; otherwise price went
            // through us and the wall together — a real fill at the worst moment.
            _filled++;
            var tp = r.IsLong ? r.Limit * (1m + snap.DensityTakeProfitPercent / 100m) : r.Limit * (1m - snap.DensityTakeProfitPercent / 100m);
            var stop = r.IsLong ? r.WallPrice * (1m - snap.DensityStopBeyondPercent / 100m) : r.WallPrice * (1m + snap.DensityStopBeyondPercent / 100m);
            _pos = new Position(r.Exchange, r.Symbol, r.IsLong, r.Qty, r.Limit, r.WallPrice, r.WallRefQty, tp, stop, now);
            _resting = null;
            _lastNote = $"{r.Symbol} вход {(r.IsLong ? "лонг" : "шорт")} {r.Limit}, тейк {tp:G6}, стоп за плотностью {stop:G6}";
            _logger.LogInformation("DENSITY fill {Sym} {Side} @ {Px} tp {Tp} stop {Stop} wall {Wall}{Holds}",
                r.Symbol, r.IsLong ? "L" : "S", r.Limit, tp, stop, r.WallPrice, wallHolds ? "" : " (wall already gone)");
            if (!wallHolds) _pos.ExitTriggeredAt = now; // no wall behind us: leave at once
            _pos.ExitReason = wallHolds ? null : "wall-gone";
            return;
        }
        if (!wallHolds) { Cancel("плотность съели или сняли"); return; }
        var dist = Math.Abs(mid - r.WallPrice) / mid * 100m;
        if (dist > snap.DensityApproachPercent * 2m) { Cancel("цена ушла от плотности"); return; }
        if ((now - r.PlacedAt).TotalSeconds > 60) { Cancel("за 60 с цена не дошла"); return; }
    }

    private void Cancel(string why)
    {
        _cancelled++;
        _cancelReasons[why] = _cancelReasons.GetValueOrDefault(why) + 1;
        _lastNote = $"{_resting!.Symbol}: заявка снята — {why}";
        _resting = null;
    }

    private void ManagePosition(ArbitrageOptions snap, DateTime now, TimeSpan latency)
    {
        var p = _pos!;
        var book = _market.BookSides(p.Exchange, p.Symbol);
        if (book is not { } b)
        {
            p.NoBookSince ??= now;
            if (now - p.NoBookSince > TimeSpan.FromSeconds(30)) { p.ExitTriggeredAt ??= now; p.ExitReason ??= "no-book"; }
            return;
        }
        p.NoBookSince = null;
        var bid = b.Bids[0].Price; var ask = b.Asks[0].Price;
        p.Mark = p.IsLong ? bid : ask;

        if (p.ExitTriggeredAt == null)
        {
            // Take profit: resting maker order, fills only on trade-through.
            if (DensityMath.RestingFilled(!p.IsLong, p.TakeProfit, bid, ask))
            {
                Close(p.TakeProfit, snap.DensityMakerFeePercent, "take-profit", now);
                return;
            }
            var levels = p.IsLong ? b.Bids : b.Asks;
            var beyond = p.IsLong ? bid <= p.Stop : ask >= p.Stop;
            // The wall is our reason to be in: once it is eaten or pulled, leave.
            var wallGone = !DensityMath.WallHolds(levels, p.WallPrice, p.WallRefQty, 0.4m);
            var timeUp = (now - p.OpenedAt).TotalSeconds > snap.DensityMaxHoldSeconds;
            if (beyond || wallGone || timeUp)
            {
                p.ExitTriggeredAt = now;
                p.ExitReason = beyond ? "stop" : wallGone ? "wall-gone" : "time";
            }
            return;
        }
        // Market exit priced one latency after the trigger, walking the book for our size.
        if (now - p.ExitTriggeredAt < latency) return;
        var px = DepthMath.VwapForBaseQty(p.IsLong ? b.Bids : b.Asks, p.Qty) ?? (p.IsLong ? bid : ask);
        Close(px, snap.DensityTakerFeePercent, p.ExitReason ?? "exit", now);
    }

    private void Close(decimal exit, decimal exitFeePct, string reason, DateTime now)
    {
        var p = _pos!;
        var snap = _runtime.Snapshot;
        var gross = (p.IsLong ? exit - p.Entry : p.Entry - exit) * p.Qty;
        var fees = p.Entry * p.Qty * snap.DensityMakerFeePercent / 100m + exit * p.Qty * exitFeePct / 100m;
        var t = new Trade(p.Exchange, p.Symbol, p.IsLong, p.Qty, p.Entry, exit, p.WallPrice, p.OpenedAt, now, gross - fees, fees, reason);
        _closed.Insert(0, t);
        if (_closed.Count > 2000) _closed.RemoveRange(2000, _closed.Count - 2000);
        _pos = null;
        _lastCloseUtc = now;
        _lastNote = $"{t.Symbol} закрыт: {ReasonRu(reason)}, {(t.PnlUsd >= 0 ? "+" : "")}{t.PnlUsd:F2}$";
        _logger.LogInformation("DENSITY close {Sym} {Side} {Entry} -> {Exit} {Reason} pnl={Pnl:F2}",
            t.Symbol, t.IsLong ? "L" : "S", t.Entry, t.Exit, reason, t.PnlUsd);
        Save();
    }

    private static string ReasonRu(string r) => r switch
    {
        "take-profit" => "тейк",
        "stop" => "стоп за плотностью",
        "wall-gone" => "плотность съели или сняли",
        "time" => "время вышло",
        "no-book" => "пропал стакан",
        _ => r
    };

    // ------------------------------------------------------------------ snapshot

    public object Snapshot()
    {
        var snap = _runtime.Snapshot;
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            var today = now.Date;
            var t = _closed;
            return new
            {
                active = Active,
                paperOnly = true,
                venues = Venues.AllowSet(snap.ScalpVenues)?.ToArray() ?? ["Binance"],
                lastNote = _lastNote,
                walls = _tracks.Values
                    .OrderBy(w => w.DistancePercent)
                    .Take(14)
                    .Select(w => new
                    {
                        w.Exchange, w.Symbol, side = w.IsBid ? "bid" : "ask", w.Price, notionalUsd = w.Notional,
                        w.Multiple, w.DistancePercent, ageSeconds = (int)(now - w.FirstSeen).TotalSeconds,
                        mature = (now - w.FirstSeen).TotalSeconds >= snap.DensityMinWallAgeSeconds
                    }),
                resting = _resting is { } r ? new { r.Symbol, r.Exchange, side = r.IsLong ? "Long" : "Short", r.Limit, r.WallPrice, r.PlacedAt } : null,
                position = _pos is { } p ? new
                {
                    p.Symbol, p.Exchange, side = p.IsLong ? "Long" : "Short", p.Entry, p.TakeProfit, p.Stop, p.WallPrice, p.Mark,
                    notionalUsd = p.Entry * p.Qty,
                    unrealizedUsd = p.Mark > 0 ? (p.IsLong ? p.Mark - p.Entry : p.Entry - p.Mark) * p.Qty : 0m,
                    p.OpenedAt, exiting = p.ExitReason
                } : null,
                stats = new
                {
                    trades = t.Count,
                    wins = t.Count(x => x.PnlUsd > 0),
                    realizedUsd = t.Sum(x => x.PnlUsd),
                    feesUsd = t.Sum(x => x.FeesUsd),
                    todayUsd = t.Where(x => x.ClosedAt.Date == today).Sum(x => x.PnlUsd),
                    tradesToday = t.Count(x => x.ClosedAt.Date == today),
                    placed = _placed, filled = _filled, cancelled = _cancelled,
                    cancelReasons = _cancelReasons.OrderByDescending(kv => kv.Value).Select(kv => new { reason = kv.Key, count = kv.Value }),
                    byReason = t.GroupBy(x => x.Reason).Select(g => new { reason = g.Key, count = g.Count(), pnlUsd = g.Sum(x => x.PnlUsd) })
                },
                closed = t.Take(60)
            };
        }
    }

    public int ResetHistory()
    {
        lock (_lock)
        {
            var n = _closed.Count;
            _closed.Clear();
            _placed = _filled = _cancelled = 0;
            _cancelReasons.Clear();
            Save();
            return n;
        }
    }

    // ------------------------------------------------------------------ persistence

    private void Load()
    {
        try
        {
            if (!File.Exists(_ledgerPath)) return;
            var list = JsonSerializer.Deserialize<List<Trade>>(File.ReadAllText(_ledgerPath), Json);
            if (list != null) _closed.AddRange(list);
            _logger.LogInformation("Density ledger: {N} paper trades", _closed.Count);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Density ledger unreadable"); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_ledgerPath)!);
            var tmp = _ledgerPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_closed, Json));
            File.Copy(tmp, _ledgerPath, true);
            File.Delete(tmp);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Density ledger save failed"); }
    }

    // ------------------------------------------------------------------ types

    private sealed class WallTrack(string exchange, string symbol, bool isBid, decimal price, DateTime firstSeen)
    {
        public string Exchange { get; } = exchange;
        public string Symbol { get; } = symbol;
        public bool IsBid { get; } = isBid;
        public decimal Price { get; } = price;
        public DateTime FirstSeen { get; } = firstSeen;
        public DateTime LastSeen { get; set; } = firstSeen;
        public decimal Qty { get; set; }
        public decimal MaxQty { get; set; }
        public decimal Notional { get; set; }
        public decimal Multiple { get; set; }
        public decimal DistancePercent { get; set; }
    }

    private sealed record Resting(string Exchange, string Symbol, bool IsLong, decimal Limit, decimal WallPrice, decimal WallRefQty,
        decimal Qty, DateTime PlacedAt, DateTime ActiveAt);

    private sealed class Position(string exchange, string symbol, bool isLong, decimal qty, decimal entry, decimal wallPrice,
        decimal wallRefQty, decimal takeProfit, decimal stop, DateTime openedAt)
    {
        public string Exchange { get; } = exchange;
        public string Symbol { get; } = symbol;
        public bool IsLong { get; } = isLong;
        public decimal Qty { get; } = qty;
        public decimal Entry { get; } = entry;
        public decimal WallPrice { get; } = wallPrice;
        public decimal WallRefQty { get; } = wallRefQty;
        public decimal TakeProfit { get; } = takeProfit;
        public decimal Stop { get; } = stop;
        public DateTime OpenedAt { get; } = openedAt;
        public decimal Mark { get; set; }
        public DateTime? ExitTriggeredAt { get; set; }
        public string? ExitReason { get; set; }
        public DateTime? NoBookSince { get; set; }
    }

    public sealed record Trade(string Exchange, string Symbol, bool IsLong, decimal Qty, decimal Entry, decimal Exit, decimal WallPrice,
        DateTime OpenedAt, DateTime ClosedAt, decimal PnlUsd, decimal FeesUsd, string Reason);
}
