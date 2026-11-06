using System.Collections.Concurrent;
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
    private readonly LiveTradingGuard _guard;
    private readonly ILogger<ScalpEngine> _logger;

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
        LiveTradingGuard guard,
        ILogger<ScalpEngine> logger)
    {
        _market = market;
        _markets = markets;
        _runtime = runtime;
        _orders = orders;
        _guard = guard;
        _logger = logger;
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
                    p.UnrealizedUsd, p.Note
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
        if (!_runtime.Snapshot.SpatialScalpMode) return;

        MarkAndMaybeClose();
        if (ct.IsCancellationRequested) return;

        var candidate = Pick();
        if (candidate == null) return;

        lock (_lock)
        {
            if (_open.Count >= 1) return;
            if (_open.Any(p => p.Symbol.Equals(candidate.Symbol, StringComparison.OrdinalIgnoreCase))) return;
        }
        if ((DateTime.UtcNow - _lastOpenUtc).TotalSeconds < 8) return;

        var snap = _runtime.Snapshot;
        var notional = snap.QuoteSize > 0 ? Math.Min(snap.QuoteSize, 150m) : 80m;
        if (notional < 20m) notional = 20m;
        var qty = LiveOrderEngine.RoundBaseQty(notional / candidate.Touch, candidate.Touch);
        if (qty <= 0) return;

        var fee = ArbEconomics.TakerPercent(snap.EstimatedTakerFees, candidate.Exchange);
        var live = _guard.CanPlaceOrders;

        if (live)
        {
            var limit = LiveOrderEngine.SnapLimit(candidate.Touch, candidate.Touch, candidate.IsLong);
            var lev = snap.FuturesPaperLeverage > 0 ? snap.FuturesPaperLeverage : 3m;
            var sent = await _orders.PlaceScalpLegAsync(
                candidate.Exchange, candidate.Symbol, candidate.IsLong, qty, limit, lev, ct)
                .ConfigureAwait(false);
            if (!sent.Ok)
            {
                _logger.LogDebug("SCALP skip {Sym} {Ex}: {Err}", candidate.Symbol, candidate.Exchange, sent.Error);
                return;
            }
            Remember(candidate, qty, limit, live: true, sent.Id, fee);
            return;
        }

        Remember(candidate, qty, candidate.Touch, live: false, null, fee);
    }

    private void Remember(ScalpCandidate c, decimal qty, decimal entry, bool live, string? orderId, decimal feePct)
    {
        var pos = new ScalpPosition
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
        lock (_lock) _open.Add(pos);
        _lastOpenUtc = DateTime.UtcNow;
        _logger.LogInformation("SCALP {Mode} {Side} {Sym} on {Ex} qty={Q} @ {P} | {Note}",
            live ? "LIVE" : "PAPER", pos.Side, pos.Symbol, pos.Exchange, qty, entry, pos.Note);
    }

    private void MarkAndMaybeClose()
    {
        List<ScalpPosition> open;
        lock (_lock) open = _open.ToList();
        foreach (var pos in open)
        {
            var books = _market.GetBookTickers(pos.Symbol);
            if (!books.TryGetValue(pos.Exchange, out var tk) || tk.BestBid <= 0 || tk.BestAsk <= 0)
                continue;

            var mark = pos.IsLong ? tk.BestBid : tk.BestAsk;
            var gross = pos.IsLong
                ? (mark - pos.Entry) * pos.Qty
                : (pos.Entry - mark) * pos.Qty;
            var fees = pos.Entry * pos.Qty * pos.FeePercent / 100m * 2m;
            var net = gross - fees;
            pos.UnrealizedUsd = net;

            var obi = TouchImbalance(tk);
            var flipped = pos.IsLong ? obi < -0.15m : obi > 0.15m;
            var age = (DateTime.UtcNow - pos.OpenedAt).TotalSeconds;
            var target = Math.Max(0.08m, pos.Entry * pos.Qty * 0.0006m);
            var stop = -Math.Max(0.25m, target * 1.4m);

            string? why = null;
            if (net >= target) why = "target";
            else if (flipped && age >= 2) why = "imbalance died";
            else if (net <= stop) why = "stop";
            else if (age >= 45 && net > 0) why = "time lock";
            else if (age >= 90) why = "time flat";

            if (why == null) continue;
            _ = CloseAsync(pos, mark, net, why);
        }
    }

    private async Task CloseAsync(ScalpPosition pos, decimal mark, decimal net, string why)
    {
        if (pos.Live)
        {
            var closed = await _orders.CloseScalpLegAsync(pos.Exchange, pos.Symbol, pos.IsLong, pos.Qty, CancellationToken.None)
                .ConfigureAwait(false);
            if (!closed.Ok)
            {
                _logger.LogWarning("SCALP close fail {Sym} {Ex}: {Err}", pos.Symbol, pos.Exchange, closed.Error);
                return;
            }
        }

        pos.Exit = mark;
        pos.ClosedAt = DateTime.UtcNow;
        pos.RealizedUsd = net;
        pos.Note = (pos.Note ?? "") + " | " + why;
        lock (_lock)
        {
            _open.RemoveAll(p => p.Id == pos.Id);
            _closed.Insert(0, pos);
            if (_closed.Count > 200) _closed.RemoveRange(200, _closed.Count - 200);
        }
        _logger.LogInformation("SCALP close {Side} {Sym} {Ex} {Why} pnl={Pnl:F2}",
            pos.Side, pos.Symbol, pos.Exchange, why, net);
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
        public decimal? Exit { get; set; }
        public DateTime OpenedAt { get; set; }
        public DateTime? ClosedAt { get; set; }
        public bool Live { get; set; }
        public string? OrderId { get; set; }
        public decimal FeePercent { get; set; }
        public decimal UnrealizedUsd { get; set; }
        public decimal? RealizedUsd { get; set; }
        public string? Note { get; set; }
    }
}
