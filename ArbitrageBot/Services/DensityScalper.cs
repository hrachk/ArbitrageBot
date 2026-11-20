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
/// Paper uses a pessimistic fill model: an entry or take-profit fills only when the market trades
/// through our price (a touch is not a fill), orders become active only after the configured order
/// latency, and market exits are priced at the book one latency later.
///
/// Live (Binance only, when live orders and "scalp with real orders" are on): the entry is a
/// post-only limit, the take-profit a post-only reduce-only limit, and an exchange-side STOP_MARKET
/// (close position, mark price) protects the position even if the bot or the connection dies.
/// Wall-gone / time exits cancel the take-profit and close at market. Orders are polled, a cancel
/// that races a fill keeps the fill, and an open live trade survives a restart (state file).
/// </summary>
public sealed class DensityScalper
{
    private readonly IFuturesMarketService _market;
    private readonly ActiveMarketContext _markets;
    private readonly RuntimeRiskConfig _runtime;
    private readonly LiveTradingGuard _guard;
    private readonly LiveSafetyService _safety;
    private readonly LiveOrderEngine _orders;
    private readonly BinanceMakerGateway _gw;
    private readonly ExposureRegistry _exposure;
    private readonly ILogger<DensityScalper> _logger;
    private readonly string _ledgerPath;
    private readonly string _statePath;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, WallTrack> _tracks = new(StringComparer.OrdinalIgnoreCase);
    private Resting? _resting;
    private Position? _pos;
    private readonly List<Trade> _closed = [];
    private DateTime _lastCloseUtc = DateTime.MinValue;
    private DateTime _nextPollUtc = DateTime.MinValue;
    private DateTime _nextPositionCheckUtc = DateTime.MinValue;
    private string _lastNote = "";
    private int _placed, _filled, _cancelled;
    private readonly Dictionary<string, int> _cancelReasons = new();
    private const string LiveVenue = "Binance";
    private bool _sweptAfterStart;

    public DensityScalper(IFuturesMarketService market, ActiveMarketContext markets, RuntimeRiskConfig runtime,
        LiveTradingGuard guard, LiveSafetyService safety, LiveOrderEngine orders, BinanceMakerGateway gw, ExposureRegistry exposure,
        IWebHostEnvironment env, ILogger<DensityScalper> logger)
    {
        _market = market;
        _markets = markets;
        _runtime = runtime;
        _guard = guard;
        _safety = safety;
        _orders = orders;
        _gw = gw;
        _exposure = exposure;
        _logger = logger;
        _ledgerPath = Path.Combine(env.ContentRootPath, "data", "paper", "density-ledger.json");
        _statePath = Path.Combine(env.ContentRootPath, "data", "live", "density-state.json");
        Load();
        LoadState();
    }

    public bool Active => _runtime.Snapshot.SpatialScalpMode && _runtime.Snapshot.ScalpStrategy == "density";

    /// <summary>Real orders: live orders on, "scalp with real orders" on, Binance trade key present.</summary>
    public bool LiveMode => _guard.CanPlaceOrders && _runtime.Snapshot.ScalpLive && _gw.HasKeys();

    /// <summary>True while a real density order or position exists on this symbol (the hedge skips it).</summary>
    public bool HasLiveExposure(string symbol)
    {
        lock (_lock)
            return (_resting is { Live: true } r && r.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
                || (_pos is { Live: true } p && p.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ tick

    public async Task TickAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            var snap = _runtime.Snapshot;
            var now = DateTime.UtcNow;
            var latency = TimeSpan.FromMilliseconds(Math.Max(0, snap.PaperFillLatencyMs));
            Resting? r; Position? p;
            lock (_lock)
            {
                UpdateWalls(snap, now);
                r = _resting; p = _pos;
            }

            if (p != null)
            {
                if (p.Live) await ManageLivePositionAsync(p, snap, now, ct).ConfigureAwait(false);
                else lock (_lock) ManagePosition(snap, now, latency);
                return;
            }
            if (r != null)
            {
                if (r.Live) await ManageLiveRestingAsync(r, snap, now, ct).ConfigureAwait(false);
                else lock (_lock) ManageResting(snap, now);
                return;
            }
            if (!Active) return;

            var live = LiveMode;
            if (live && !_sweptAfterStart)
            {
                _sweptAfterStart = true;
                // Clean stops left by an earlier run on symbols we traded live and do not hold now.
                List<string> syms;
                lock (_lock) syms = _closed.Where(x => x.Live).Select(x => x.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();
                foreach (var s in syms) await _gw.SweepStopsAsync(s, ct).ConfigureAwait(false);
            }
            WallTrack? best; decimal notional;
            lock (_lock)
            {
                // Risk gates, per mode: paper results never offset real losses.
                var today = now.Date;
                var dayPnl = _closed.Where(c => c.Live == live && c.ClosedAt.Date == today).Sum(c => c.PnlUsd);
                var lossCap = Math.Abs(snap.ScalpDailyLossUsd) > 0 ? Math.Abs(snap.ScalpDailyLossUsd) : 3m;
                if (dayPnl <= -lossCap) { _lastNote = $"дневной лимит убытка {lossCap:F2} достигнут"; return; }
                if ((now - _lastCloseUtc).TotalSeconds < Math.Max(0, snap.ScalpCooldownSeconds)) return;
                notional = Math.Max(0m, snap.ScalpDepositUsd) * Math.Clamp(snap.ScalpLeverage, 1m, 5m);
                if (notional < 6m) { _lastNote = "депозит скальпа слишком мал"; return; }
                best = PickWall(snap, now, notional, live);
            }
            if (best == null) return;

            var bk = _market.BookSides(best.Exchange, best.Symbol);
            if (bk is not { } bb) return;
            var tick = DensityMath.InferTick(bb.Bids, bb.Asks);
            if (tick <= 0) return;
            var limit = DensityMath.FrontOf(new BookWall(best.IsBid, best.Price, best.Qty, best.Notional, best.Multiple, best.DistancePercent), tick);
            // Our price must still be away from the touch, or it would be a taker order.
            if (best.IsBid ? limit >= bb.Bids[0].Price : limit <= bb.Asks[0].Price) return;

            if (live) { await PlaceLiveEntryAsync(best, limit, notional, snap, now, ct).ConfigureAwait(false); return; }
            lock (_lock)
            {
                _resting = new Resting
                {
                    Exchange = best.Exchange, Symbol = best.Symbol, IsLong = best.IsBid, Limit = limit, WallPrice = best.Price,
                    WallRefQty = best.Qty, Qty = notional / limit, PlacedAt = now, ActiveAt = now + latency
                };
                _placed++;
                _lastNote = $"{best.Symbol} {(best.IsBid ? "лонг" : "шорт")} лимит {limit} перед плотностью {best.Price} (${best.Notional:N0}, ×{best.Multiple:F0})";
            }
            _logger.LogInformation("DENSITY rest {Sym} {Side} @ {Px} in front of wall {Wall} ${N:N0} x{M:F0}",
                best.Symbol, best.IsBid ? "L" : "S", limit, best.Price, best.Notional, best.Multiple);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Density tick failed");
        }
        finally { _gate.Release(); }
    }

    private void UpdateWalls(ArbitrageOptions snap, DateTime now)
    {
        var venues = Venues.AllowSet(snap.ScalpVenues)?.ToList() ?? [LiveVenue];
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
    }

    private WallTrack? PickWall(ArbitrageOptions snap, DateTime now, decimal notional, bool live)
    {
        WallTrack? best = null;
        foreach (var t in _tracks.Values)
        {
            if (live && !t.Exchange.Equals(LiveVenue, StringComparison.OrdinalIgnoreCase)) continue;
            if (live && _exposure.IsArbReserved(t.Symbol)) continue;
            if ((now - t.FirstSeen).TotalSeconds < snap.DensityMinWallAgeSeconds) continue;
            if (t.DistancePercent > snap.DensityApproachPercent) continue;
            if (t.Notional < notional * 3m) continue; // our order must be small next to the wall
            if (best == null || t.Multiple > best.Multiple) best = t;
        }
        return best;
    }

    // ------------------------------------------------------------------ paper

    private void ManageResting(ArbitrageOptions snap, DateTime now)
    {
        var r = _resting!;
        var book = _market.BookSides(r.Exchange, r.Symbol);
        if (book is not { } b) { Cancel("нет живого стакана"); return; }
        var bid = b.Bids[0].Price; var ask = b.Asks[0].Price;
        var crossed = DensityMath.RestingFilled(r.IsLong, r.Limit, bid, ask);
        if (now < r.ActiveAt)
        {
            // The order is still on its way; if price is already through, it would land as a taker. Skip.
            if (crossed) Cancel("цена прошла уровень раньше, чем дошёл ордер");
            return;
        }
        var wallHolds = DensityMath.WallHolds(r.IsLong ? b.Bids : b.Asks, r.WallPrice, r.WallRefQty, 0.6m);
        if (crossed)
        {
            _filled++;
            OpenPositionUnlocked(r, r.Qty, r.Limit, now, live: false);
            if (!wallHolds) { _pos!.ExitTriggeredAt = now; _pos.ExitReason = "wall-gone"; } // no wall behind us: leave at once
            return;
        }
        var why = RestingCancelReason(r, b, snap, now);
        if (why != null) Cancel(why);
    }

    /// <summary>Why a resting entry should be withdrawn now, or null to keep it.</summary>
    private string? RestingCancelReason(Resting r, (IReadOnlyList<(decimal Price, decimal Quantity)> Bids, IReadOnlyList<(decimal Price, decimal Quantity)> Asks) b,
        ArbitrageOptions snap, DateTime now)
    {
        var mid = (b.Bids[0].Price + b.Asks[0].Price) / 2m;
        if (!DensityMath.WallHolds(r.IsLong ? b.Bids : b.Asks, r.WallPrice, r.WallRefQty, 0.6m)) return "плотность съели или сняли";
        if (Math.Abs(mid - r.WallPrice) / mid * 100m > snap.DensityApproachPercent * 2m) return "цена ушла от плотности";
        if ((now - r.PlacedAt).TotalSeconds > 60) return "за 60 с цена не дошла";
        return null;
    }

    private void OpenPositionUnlocked(Resting r, decimal qty, decimal entry, DateTime now, bool live)
    {
        var snap = _runtime.Snapshot;
        var tp = r.IsLong ? entry * (1m + snap.DensityTakeProfitPercent / 100m) : entry * (1m - snap.DensityTakeProfitPercent / 100m);
        var stop = r.IsLong ? r.WallPrice * (1m - snap.DensityStopBeyondPercent / 100m) : r.WallPrice * (1m + snap.DensityStopBeyondPercent / 100m);
        _pos = new Position
        {
            Exchange = r.Exchange, Symbol = r.Symbol, IsLong = r.IsLong, Qty = qty, Entry = entry, WallPrice = r.WallPrice,
            WallRefQty = r.WallRefQty, TakeProfit = tp, Stop = stop, OpenedAt = now, Live = live
        };
        _resting = null;
        _lastNote = $"{r.Symbol} вход {(r.IsLong ? "лонг" : "шорт")} {entry}{(live ? " (реально)" : "")}, тейк {tp:G6}, стоп за плотностью {stop:G6}";
        _logger.LogInformation("DENSITY {Mode} fill {Sym} {Side} {Qty} @ {Px} tp {Tp} stop {Stop} wall {Wall}",
            live ? "LIVE" : "paper", r.Symbol, r.IsLong ? "L" : "S", qty, entry, tp, stop, r.WallPrice);
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
                CloseUnlocked(p.TakeProfit, snap.DensityMakerFeePercent, "take-profit", now);
                return;
            }
            p.ExitReason = ExitReason(p, b, snap, now);
            if (p.ExitReason != null) p.ExitTriggeredAt = now;
            return;
        }
        // Market exit priced one latency after the trigger, walking the book for our size.
        if (now - p.ExitTriggeredAt < latency) return;
        var px = DepthMath.VwapForBaseQty(p.IsLong ? b.Bids : b.Asks, p.Qty) ?? (p.IsLong ? bid : ask);
        CloseUnlocked(px, snap.DensityTakerFeePercent, p.ExitReason ?? "exit", now);
    }

    /// <summary>Bot-side exit: past the stop, wall eaten or pulled (our reason to be in), or time up.</summary>
    private static string? ExitReason(Position p, (IReadOnlyList<(decimal Price, decimal Quantity)> Bids, IReadOnlyList<(decimal Price, decimal Quantity)> Asks) b,
        ArbitrageOptions snap, DateTime now)
    {
        var bid = b.Bids[0].Price; var ask = b.Asks[0].Price;
        if (p.IsLong ? bid <= p.Stop : ask >= p.Stop) return "stop";
        if (!DensityMath.WallHolds(p.IsLong ? b.Bids : b.Asks, p.WallPrice, p.WallRefQty, 0.4m)) return "wall-gone";
        if ((now - p.OpenedAt).TotalSeconds > snap.DensityMaxHoldSeconds) return "time";
        return null;
    }

    private void CloseUnlocked(decimal exit, decimal exitFeePct, string reason, DateTime now, decimal? feesOverride = null)
    {
        var p = _pos!;
        var snap = _runtime.Snapshot;
        var gross = (p.IsLong ? exit - p.Entry : p.Entry - exit) * p.Qty;
        var fees = feesOverride ?? (p.Entry * p.Qty * snap.DensityMakerFeePercent / 100m + exit * p.Qty * exitFeePct / 100m);
        var t = new Trade(p.Exchange, p.Symbol, p.IsLong, p.Qty, p.Entry, exit, p.WallPrice, p.OpenedAt, now, gross - fees, fees, reason, p.Live);
        _closed.Insert(0, t);
        if (_closed.Count > 2000) _closed.RemoveRange(2000, _closed.Count - 2000);
        _pos = null;
        _lastCloseUtc = now;
        _lastNote = $"{t.Symbol} закрыт{(t.Live ? " (реально)" : "")}: {ReasonRu(reason)}, {(t.PnlUsd >= 0 ? "+" : "")}{t.PnlUsd:F2}$";
        _logger.LogInformation("DENSITY {Mode} close {Sym} {Side} {Entry} -> {Exit} {Reason} pnl={Pnl:F2}",
            t.Live ? "LIVE" : "paper", t.Symbol, t.IsLong ? "L" : "S", t.Entry, t.Exit, reason, t.PnlUsd);
        Save();
        if (t.Live) SaveState();
    }

    private static string ReasonRu(string r) => r switch
    {
        "take-profit" => "тейк",
        "take-profit-market" => "тейк по рынку",
        "stop" => "стоп за плотностью",
        "exchange-stop" => "стоп на бирже",
        "wall-gone" => "плотность съели или сняли",
        "time" => "время вышло",
        "no-book" => "пропал стакан",
        "no-stop" => "не удалось поставить стоп",
        _ => r
    };

    // ------------------------------------------------------------------ live (Binance)

    private async Task PlaceLiveEntryAsync(WallTrack w, decimal limit, decimal notional, ArbitrageOptions snap, DateTime now, CancellationToken ct)
    {
        var f = await _orders.GetSymbolFiltersAsync(LiveVenue, w.Symbol, ct).ConfigureAwait(false);
        if (f is not { } fl) { Note($"{w.Symbol}: нет правил контракта Binance"); return; }
        var isLong = w.IsBid;
        var price = OrderRules.SnapPrice(limit, fl.PriceStep, fl.PriceDecimals, up: !isLong);
        var qty = OrderRules.FloorQty(notional / price, fl.QtyStep, fl.QtyDecimals);
        if (qty <= 0 || qty < fl.MinQty || (fl.MinNotional > 0 && qty * price < fl.MinNotional))
        { Note($"{w.Symbol}: позиция меньше минимума Binance"); return; }
        var lev = Math.Clamp(snap.ScalpLeverage, 1m, 5m);
        var reserve = _safety.TryReserveScalpOrderAttempt(w.Symbol, LiveVenue, qty * price, Math.Max(0m, snap.ScalpDepositUsd) * lev * 1.05m);
        if (!reserve.ok) { Note($"{w.Symbol}: {reserve.reason}"); return; }
        await _gw.EnsureLeverageAsync(w.Symbol, (int)lev, ct).ConfigureAwait(false);
        var st = await _gw.PlaceMakerAsync(w.Symbol, isBuy: isLong, positionIsLong: isLong, qty, price, reduce: false,
            "ab-de-" + Guid.NewGuid().ToString("N")[..12], ct).ConfigureAwait(false);
        if (!st.Ok)
        {
            lock (_lock) { _lastCloseUtc = now; } // short pause before the next try
            Note($"{w.Symbol}: Binance не принял заявку — {st.Error}");
            _logger.LogWarning("DENSITY LIVE entry rejected {Sym} {Side} {Qty} @ {Px}: {Err}", w.Symbol, isLong ? "L" : "S", qty, price, st.Error);
            return;
        }
        lock (_lock)
        {
            _resting = new Resting
            {
                Exchange = LiveVenue, Symbol = w.Symbol, IsLong = isLong, Limit = price, WallPrice = w.Price, WallRefQty = w.Qty,
                Qty = qty, PlacedAt = now, ActiveAt = now, Live = true, OrderId = st.OrderId
            };
            _placed++;
            _lastNote = $"{w.Symbol} {(isLong ? "лонг" : "шорт")} реальная лимитка {price} перед плотностью {w.Price} (${w.Notional:N0}, ×{w.Multiple:F0})";
            SaveState();
        }
        _logger.LogInformation("DENSITY LIVE rest {Sym} {Side} {Qty} @ {Px} id={Id} wall {Wall} ${N:N0} x{M:F0}",
            w.Symbol, isLong ? "L" : "S", qty, price, st.OrderId, w.Price, w.Notional, w.Multiple);
    }

    private async Task ManageLiveRestingAsync(Resting r, ArbitrageOptions snap, DateTime now, CancellationToken ct)
    {
        if (now < _nextPollUtc) return;
        _nextPollUtc = now.AddMilliseconds(400);
        var st = await _gw.GetAsync(r.Symbol, r.OrderId, ct).ConfigureAwait(false);
        if (!st.Ok) { Note($"{r.Symbol}: статус заявки не прочитан — {st.Error}"); return; }
        if (st.Filled) { await OpenLiveAsync(r, st.FilledQty, st.AveragePrice, now, ct).ConfigureAwait(false); return; }
        if (!st.Open)
        {
            // Expired or cancelled by the exchange (a post-only that would have crossed is rejected this way).
            if (st.FilledQty > 0) { await OpenLiveAsync(r, st.FilledQty, st.AveragePrice, now, ct).ConfigureAwait(false); return; }
            lock (_lock) { Cancel("биржа сняла заявку"); SaveState(); }
            return;
        }
        string? why;
        if (!_guard.CanPlaceOrders) why = "live-ордера выключены";
        else
        {
            var book = _market.BookSides(r.Exchange, r.Symbol);
            why = book is { } b ? RestingCancelReason(r, b, snap, now) : "нет живого стакана";
        }
        if (why == null) return;
        var c = await _gw.CancelAsync(r.Symbol, r.OrderId, ct).ConfigureAwait(false);
        if (c.Ok && c.FilledQty > 0)
        {
            // The cancel raced a (partial) fill: we hold what was filled.
            await OpenLiveAsync(r, c.FilledQty, c.AveragePrice, now, ct).ConfigureAwait(false);
            return;
        }
        if (c.Ok && !c.Open)
        {
            _logger.LogInformation("DENSITY LIVE withdrawn {Sym} id={Id}: {Why}", r.Symbol, r.OrderId, why);
            lock (_lock) { Cancel(why); SaveState(); }
            return;
        }
        Note($"{r.Symbol}: заявку не удалось снять — {c.Error}, повторим");
    }

    private async Task OpenLiveAsync(Resting r, decimal qty, decimal avg, DateTime now, CancellationToken ct)
    {
        Position p;
        lock (_lock)
        {
            _filled++;
            OpenPositionUnlocked(r, qty, avg > 0 ? avg : r.Limit, now, live: true);
            p = _pos!;
            SaveState();
        }
        var f = await _orders.GetSymbolFiltersAsync(LiveVenue, p.Symbol, ct).ConfigureAwait(false);
        var stopPx = OrderRules.SnapPrice(p.Stop, f?.PriceStep, f?.PriceDecimals, up: !p.IsLong);
        var tpPx = OrderRules.SnapPrice(p.TakeProfit, f?.PriceStep, f?.PriceDecimals, up: p.IsLong);

        // 1) Exchange-side stop first: it is the protection if anything below fails or the bot dies.
        var stop = await _gw.PlaceStopAsync(p.Symbol, p.IsLong, stopPx, ct).ConfigureAwait(false);
        if (stop.Ok) p.StopAlgoId = stop.AlgoId;
        else
        {
            _logger.LogError("DENSITY LIVE stop not placed {Sym}: {Err} — closing at market", p.Symbol, stop.Error);
            lock (_lock) { p.ExitTriggeredAt = now; p.ExitReason = "no-stop"; SaveState(); }
            return;
        }
        // 2) Take-profit as a post-only reduce-only limit. Rejected = price already past it: take it at market.
        var tp = await _gw.PlaceMakerAsync(p.Symbol, isBuy: !p.IsLong, positionIsLong: p.IsLong, p.Qty, tpPx, reduce: true,
            "ab-dt-" + Guid.NewGuid().ToString("N")[..12], ct).ConfigureAwait(false);
        lock (_lock)
        {
            if (tp.Ok) p.TpOrderId = tp.OrderId;
            else { p.ExitTriggeredAt = now; p.ExitReason = "take-profit-market"; }
            SaveState();
        }
        _logger.LogInformation("DENSITY LIVE protect {Sym}: stop {Stop} (algo {Algo}), tp {Tp} {TpRes}",
            p.Symbol, stopPx, p.StopAlgoId, tpPx, tp.Ok ? $"id={tp.OrderId}" : $"rejected: {tp.Error}");
    }

    private async Task ManageLivePositionAsync(Position p, ArbitrageOptions snap, DateTime now, CancellationToken ct)
    {
        var book = _market.BookSides(p.Exchange, p.Symbol);
        if (book is { } bk) lock (_lock) p.Mark = p.IsLong ? bk.Bids[0].Price : bk.Asks[0].Price;
        if (now < _nextPollUtc) return;
        _nextPollUtc = now.AddMilliseconds(400);

        if (p.ExitTriggeredAt == null)
        {
            if (p.TpOrderId > 0)
            {
                var tp = await _gw.GetAsync(p.Symbol, p.TpOrderId, ct).ConfigureAwait(false);
                if (tp.Ok && tp.Filled)
                {
                    await _gw.CancelStopAsync(p.StopAlgoId, ct).ConfigureAwait(false);
                    await _gw.SweepStopsAsync(p.Symbol, ct).ConfigureAwait(false);
                    lock (_lock) CloseUnlocked(tp.AveragePrice > 0 ? tp.AveragePrice : p.TakeProfit, snap.DensityMakerFeePercent, "take-profit", now);
                    return;
                }
            }
            // Flat on the exchange without our take-profit: the exchange stop (or a manual close) did it.
            if (now >= _nextPositionCheckUtc)
            {
                _nextPositionCheckUtc = now.AddSeconds(2);
                var q = await _gw.PositionQtyAsync(p.Symbol, p.IsLong, ct).ConfigureAwait(false);
                if (q is 0m)
                {
                    if (p.TpOrderId > 0) await _gw.CancelAsync(p.Symbol, p.TpOrderId, ct).ConfigureAwait(false);
                    lock (_lock) CloseUnlocked(p.Stop, snap.DensityTakerFeePercent, "exchange-stop", now);
                    return;
                }
            }
            if (book is { } b)
            {
                var why = ExitReason(p, b, snap, now);
                if (why != null) lock (_lock) { p.ExitTriggeredAt = now; p.ExitReason = why; SaveState(); }
            }
            if (p.ExitTriggeredAt == null) return;
        }

        // Bot-side exit: withdraw the take-profit (keep what it filled), close the rest at market, drop the stop.
        decimal tpQty = 0, tpAvg = 0;
        if (p.TpOrderId > 0)
        {
            var c = await _gw.CancelAsync(p.Symbol, p.TpOrderId, ct).ConfigureAwait(false);
            if (c.Ok) { tpQty = c.FilledQty; tpAvg = c.AveragePrice; }
            if (c.Ok && !c.Open) p.TpOrderId = 0;
        }
        var remaining = p.Qty - tpQty;
        decimal mAvg = 0;
        if (remaining > 0)
        {
            var m = await _gw.MarketCloseAsync(p.Symbol, p.IsLong, remaining, ct).ConfigureAwait(false);
            if (!m.Ok)
            {
                var q = await _gw.PositionQtyAsync(p.Symbol, p.IsLong, ct).ConfigureAwait(false);
                if (q is not 0m)
                {
                    p.ExitRetries++;
                    _logger.LogError("DENSITY LIVE market close failed {Sym} (try {N}): {Err}", p.Symbol, p.ExitRetries, m.Error);
                    Note($"{p.Symbol}: не удалось закрыть по рынку — {m.Error}. Стоп на бирже остаётся.");
                    return; // retry next poll; the exchange stop still protects the position
                }
            }
            mAvg = m.AveragePrice > 0 ? m.AveragePrice : (p.Mark > 0 ? p.Mark : p.Entry);
        }
        await _gw.CancelStopAsync(p.StopAlgoId, ct).ConfigureAwait(false);
        await _gw.SweepStopsAsync(p.Symbol, ct).ConfigureAwait(false);
        var exit = p.Qty > 0 ? (tpQty * tpAvg + remaining * mAvg) / p.Qty : mAvg;
        var fees = p.Entry * p.Qty * snap.DensityMakerFeePercent / 100m
                   + tpQty * tpAvg * snap.DensityMakerFeePercent / 100m
                   + remaining * mAvg * snap.DensityTakerFeePercent / 100m;
        lock (_lock) CloseUnlocked(exit, snap.DensityTakerFeePercent, p.ExitReason ?? "exit", now, fees);
    }

    private void Note(string s) { lock (_lock) _lastNote = s; }

    // ------------------------------------------------------------------ snapshot

    public object Snapshot()
    {
        var snap = _runtime.Snapshot;
        var now = DateTime.UtcNow;
        var live = LiveMode;
        lock (_lock)
        {
            var today = now.Date;
            object Stats(bool isLive)
            {
                var t = _closed.Where(x => x.Live == isLive).ToList();
                return new
                {
                    trades = t.Count,
                    wins = t.Count(x => x.PnlUsd > 0),
                    realizedUsd = t.Sum(x => x.PnlUsd),
                    feesUsd = t.Sum(x => x.FeesUsd),
                    todayUsd = t.Where(x => x.ClosedAt.Date == today).Sum(x => x.PnlUsd),
                    tradesToday = t.Count(x => x.ClosedAt.Date == today)
                };
            }
            return new
            {
                active = Active,
                mode = live ? "live" : "paper",
                live,
                liveRequested = snap.ScalpLive,
                liveOrdersOn = _guard.CanPlaceOrders,
                binanceKey = _gw.HasKeys(),
                venues = live ? new[] { LiveVenue } : Venues.AllowSet(snap.ScalpVenues)?.ToArray() ?? [LiveVenue],
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
                resting = _resting is { } r ? new { r.Symbol, r.Exchange, side = r.IsLong ? "Long" : "Short", r.Limit, r.WallPrice, r.PlacedAt, r.Live, r.Qty } : null,
                position = _pos is { } p ? new
                {
                    p.Symbol, p.Exchange, side = p.IsLong ? "Long" : "Short", p.Entry, p.TakeProfit, p.Stop, p.WallPrice, p.Mark, p.Qty,
                    notionalUsd = p.Entry * p.Qty,
                    unrealizedUsd = p.Mark > 0 ? (p.IsLong ? p.Mark - p.Entry : p.Entry - p.Mark) * p.Qty : 0m,
                    p.OpenedAt, exiting = p.ExitReason, p.Live, exchangeStop = p.StopAlgoId > 0
                } : null,
                stats = new
                {
                    current = Stats(live),
                    other = Stats(!live),
                    placed = _placed, filled = _filled, cancelled = _cancelled,
                    cancelReasons = _cancelReasons.OrderByDescending(kv => kv.Value).Select(kv => new { reason = kv.Key, count = kv.Value })
                },
                closed = _closed.Take(60)
            };
        }
    }

    public int ResetHistory()
    {
        lock (_lock)
        {
            var n = _closed.RemoveAll(x => !x.Live);
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
            _logger.LogInformation("Density ledger: {N} trades", _closed.Count);
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

    /// <summary>A real order or position must survive a restart: its ids are saved and management resumes.</summary>
    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var state = new LiveState(_resting is { Live: true } ? _resting : null, _pos is { Live: true } ? _pos : null);
            File.WriteAllText(_statePath, JsonSerializer.Serialize(state, Json));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Density live state save failed"); }
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            var st = JsonSerializer.Deserialize<LiveState>(File.ReadAllText(_statePath), Json);
            if (st?.Position is { Live: true } p) { _pos = p; _logger.LogWarning("DENSITY LIVE position restored {Sym} {Qty} @ {Px} — resuming management", p.Symbol, p.Qty, p.Entry); }
            else if (st?.Resting is { Live: true } r) { _resting = r; _logger.LogWarning("DENSITY LIVE resting order restored {Sym} id={Id} — will be checked and withdrawn if stale", r.Symbol, r.OrderId); }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Density live state unreadable"); }
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

    public sealed class Resting
    {
        public string Exchange { get; set; } = "";
        public string Symbol { get; set; } = "";
        public bool IsLong { get; set; }
        public decimal Limit { get; set; }
        public decimal WallPrice { get; set; }
        public decimal WallRefQty { get; set; }
        public decimal Qty { get; set; }
        public DateTime PlacedAt { get; set; }
        public DateTime ActiveAt { get; set; }
        public bool Live { get; set; }
        public long OrderId { get; set; }
    }

    public sealed class Position
    {
        public string Exchange { get; set; } = "";
        public string Symbol { get; set; } = "";
        public bool IsLong { get; set; }
        public decimal Qty { get; set; }
        public decimal Entry { get; set; }
        public decimal WallPrice { get; set; }
        public decimal WallRefQty { get; set; }
        public decimal TakeProfit { get; set; }
        public decimal Stop { get; set; }
        public DateTime OpenedAt { get; set; }
        public decimal Mark { get; set; }
        public DateTime? ExitTriggeredAt { get; set; }
        public string? ExitReason { get; set; }
        public DateTime? NoBookSince { get; set; }
        public bool Live { get; set; }
        public long TpOrderId { get; set; }
        public long StopAlgoId { get; set; }
        public int ExitRetries { get; set; }
    }

    private sealed record LiveState(Resting? Resting, Position? Position);

    public sealed record Trade(string Exchange, string Symbol, bool IsLong, decimal Qty, decimal Entry, decimal Exit, decimal WallPrice,
        DateTime OpenedAt, DateTime ClosedAt, decimal PnlUsd, decimal FeesUsd, string Reason, bool Live = false);
}
