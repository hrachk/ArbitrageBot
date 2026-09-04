using System.Collections.Concurrent;
using System.Text.Json;
using ArbitrageBot.Configuration;
using ArbitrageBot.Models;
using Microsoft.Extensions.Options;

namespace ArbitrageBot.Services;

/// <summary>
/// Paper hedge engine: LONG cheap perp + SHORT rich perp using virtual USDT margin per exchange.
/// No asset transfers — only margin on each venue.
/// </summary>
public class FuturesPaperService : IFuturesPaperService
{
    private readonly ArbitrageOptions _options;
    private readonly ILogger<FuturesPaperService> _logger;
    private readonly IPaperAnalyticsStore _analytics;
    private readonly IWebHostEnvironment _env;
    private readonly RuntimeRiskConfig _runtime;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions SnapshotJson = new() { WriteIndented = true };
    private readonly ConcurrentDictionary<string, decimal> _margin = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FuturesPaperPosition> _positions = [];
    private readonly List<FuturesPaperTrade> _trades = [];
    private DateTime _lastOpenUtc;
    private DateTime _lastCloseUtc = DateTime.MinValue;

    public decimal RealizedPnlUsd { get; private set; }
    public decimal UnrealizedHintUsd { get; set; }
    public decimal DailyRealizedPnlUsd { get; private set; }
    private DateTime _dayUtc = DateTime.UtcNow.Date;
    public int OpenCount { get { lock (_lock) return _positions.Count; } }
    public int TradeAttempts { get; private set; }

    public FuturesPaperService(
        IOptions<ArbitrageOptions> options,
        ILogger<FuturesPaperService> logger,
        IPaperAnalyticsStore analytics,
        IWebHostEnvironment env,
        RuntimeRiskConfig runtime)
    {
        _options = options.Value;
        _logger = logger;
        _analytics = analytics;
        _env = env;
        _runtime = runtime;
    }

    private ArbitrageOptions R => _runtime.Snapshot;

    private string SnapshotPath => Path.Combine(_env.ContentRootPath, "data", "paper", "open-state.json");

    /// <summary>
    /// Start paper engine: restore open hedges from disk if present, else fresh balances.
    /// </summary>
    public void Initialize(IEnumerable<string> exchanges)
    {
        var list = exchanges.ToList();
        if (TryRestore(list))
            return;
        Reset(list);
    }

    public void Reset(IEnumerable<string> exchanges)
    {
        lock (_lock)
        {
            _margin.Clear();
            var start = R.PaperStartingQuote > 0 ? R.PaperStartingQuote : 10_000m;
            foreach (var ex in exchanges)
                _margin[ex] = start;
            _positions.Clear();
            _trades.Clear();
            RealizedPnlUsd = 0;
            UnrealizedHintUsd = 0;
            DailyRealizedPnlUsd = 0;
            _dayUtc = DateTime.UtcNow.Date;
            TradeAttempts = 0;
            _lastOpenUtc = DateTime.MinValue;
            _lastCloseUtc = DateTime.MinValue;
            ClearOpenStateFile();
            _logger.LogInformation("Futures paper margin initialized: {Start} USDT x {N} exchanges", start, _margin.Count);
        }
    }

    public FuturesPaperTrade? TryOpen(FuturesOpportunity opp)
    {
        lock (_lock)
        {
            TradeAttempts++;
            if (!R.PaperTrading) return Fail(opp, "Paper disabled");
            if (R.IsExcludedSymbol(opp.Symbol))
                return Fail(opp, $"Toxic/excluded {opp.Symbol}");

            var cooldown = R.PaperCooldownMs > 0 ? R.PaperCooldownMs : 8000;
            if ((DateTime.UtcNow - _lastOpenUtc).TotalMilliseconds < cooldown)
                return Fail(opp, $"Cooldown open {cooldown}ms");
            if ((DateTime.UtcNow - _lastCloseUtc).TotalMilliseconds < cooldown)
                return Fail(opp, $"Cooldown close {cooldown}ms");

            // QUALITY: reject if round-trip edge cannot cover close fees
            var minEdge = R.MinProfitPercent + (R.OpenEdgeBufferPercent > 0 ? R.OpenEdgeBufferPercent : 0m);
            if (opp.NetRoundTripPercent < minEdge)
                return Fail(opp, $"RT {opp.NetRoundTripPercent:F3}% < min {minEdge:F3}%");

            if (R.PaperRequireFullFill && !opp.FullyFilled)
                return Fail(opp, "Require full fill");

            var maxPos = R.FuturesMaxOpenPositions > 0 ? R.FuturesMaxOpenPositions : 3;
            if (_positions.Count >= maxPos)
                return Fail(opp, $"Max open positions {maxPos}");

            // One position per symbol at a time
            if (_positions.Any(p => p.Symbol.Equals(opp.Symbol, StringComparison.OrdinalIgnoreCase)))
                return Fail(opp, "Already open on symbol");

            // Cap concurrent legs per venue (inventory / margin concentration)
            var maxLegs = R.MaxLegsPerVenue > 0 ? R.MaxLegsPerVenue : 3;
            var longLegs = _positions.Count(p =>
                p.LongExchange.Equals(opp.LongExchange, StringComparison.OrdinalIgnoreCase) ||
                p.ShortExchange.Equals(opp.LongExchange, StringComparison.OrdinalIgnoreCase));
            var shortLegs = _positions.Count(p =>
                p.LongExchange.Equals(opp.ShortExchange, StringComparison.OrdinalIgnoreCase) ||
                p.ShortExchange.Equals(opp.ShortExchange, StringComparison.OrdinalIgnoreCase));
            if (longLegs >= maxLegs)
                return Fail(opp, $"Max legs on venue {opp.LongExchange} ({maxLegs})");
            if (shortLegs >= maxLegs)
                return Fail(opp, $"Max legs on venue {opp.ShortExchange} ({maxLegs})");

            // Day rollover for daily loss limit
            if (DateTime.UtcNow.Date != _dayUtc)
            {
                _dayUtc = DateTime.UtcNow.Date;
                DailyRealizedPnlUsd = 0;
            }

            var dayLimit = R.FuturesDailyLossLimitUsd;
            if (dayLimit < 0 && DailyRealizedPnlUsd <= dayLimit)
                return Fail(opp, $"Daily loss limit {dayLimit:F0} USDT hit ({DailyRealizedPnlUsd:F2})");

            var leverage = R.FuturesPaperLeverage > 0 ? R.FuturesPaperLeverage : 5m;
            if (leverage > 10m) leverage = 10m; // hard cap for paper safety

            var notionalCap = R.FuturesMaxNotionalUsd > 0 ? R.FuturesMaxNotionalUsd : 2500m;
            if (opp.NotionalUsd > notionalCap)
                return Fail(opp, $"Notional {opp.NotionalUsd:F0} > max {notionalCap:F0}");

            var marginEach = opp.NotionalUsd / leverage;
            if (marginEach <= 0) marginEach = opp.NotionalUsd;

            if (!_margin.TryGetValue(opp.LongExchange, out var longBal) || longBal < marginEach)
                return Fail(opp, $"Low margin on {opp.LongExchange}");
            if (!_margin.TryGetValue(opp.ShortExchange, out var shortBal) || shortBal < marginEach)
                return Fail(opp, $"Low margin on {opp.ShortExchange}");

            // Per-venue usage cap: do not lock more than X% of current free margin in one hedge leg
            var usage = R.FuturesMaxMarginUsagePercent > 0 ? R.FuturesMaxMarginUsagePercent : 0.25m;
            if (usage > 1m) usage = 1m;
            if (marginEach > longBal * usage)
                return Fail(opp, $"Margin leg > {usage:P0} free on {opp.LongExchange}");
            if (marginEach > shortBal * usage)
                return Fail(opp, $"Margin leg > {usage:P0} free on {opp.ShortExchange}");

            var openFees = opp.LongAskVwap * opp.BaseQty * (opp.LongFeePercent / 100m)
                           + opp.ShortBidVwap * opp.BaseQty * (opp.ShortFeePercent / 100m);

            _margin[opp.LongExchange] = longBal - marginEach - openFees / 2;
            _margin[opp.ShortExchange] = shortBal - marginEach - openFees / 2;

            var trade = new FuturesPaperTrade
            {
                Symbol = opp.Symbol,
                LongExchange = opp.LongExchange,
                ShortExchange = opp.ShortExchange,
                BaseQty = opp.BaseQty,
                LongEntry = opp.LongAskVwap,
                ShortEntry = opp.ShortBidVwap,
                OpenFeesUsd = openFees,
                IsOpen = true,
                Status = "Open",
                Message = $"Hedge opened | open {opp.NetSpreadPercent:F3}% RT {opp.NetRoundTripPercent:F3}%"
            };

            _analytics.RecordOpen(trade, opp);

            // Determine position type based on funding delta at entry
            var entryDelta = opp.ShortFundingRate.HasValue && opp.LongFundingRate.HasValue
                ? opp.ShortFundingRate.Value - opp.LongFundingRate.Value : 0m;
            var posType = entryDelta > 0.0001m ? "FundingArb" : "Spatial";

            _positions.Add(new FuturesPaperPosition
            {
                Symbol = opp.Symbol,
                LongExchange = opp.LongExchange,
                ShortExchange = opp.ShortExchange,
                BaseQty = opp.BaseQty,
                LongEntry = opp.LongAskVwap,
                ShortEntry = opp.ShortBidVwap,
                OpenedAt = trade.OpenedAt,
                TradeId = trade.Id,
                EntryWidthPercent = opp.LongAskVwap > 0
                    ? (opp.ShortBidVwap - opp.LongAskVwap) / opp.LongAskVwap * 100m
                    : 0m,
                LockedMarginUsd = marginEach,
                Leverage = leverage,
                EntryFundingDeltaRate = entryDelta,
                PositionType = posType
            });

            _trades.Insert(0, trade);
            Trim();
            _lastOpenUtc = DateTime.UtcNow;
            SaveOpenState();
            _logger.LogInformation("FUT PAPER OPEN {Sym} L:{L} S:{S} qty={Q:F6} lev={Lev}x margin={M:F2} edge={E:F3}%",
                opp.Symbol, opp.LongExchange, opp.ShortExchange, opp.BaseQty, leverage, marginEach, opp.NetSpreadPercent);
            return trade;
        }
    }


    public FuturesPaperTrade? ForceClose(
        Guid tradeId,
        Func<string, string, string, (decimal longBid, decimal shortAsk)?> getMarks)
    {
        lock (_lock)
        {
            var pos = _positions.FirstOrDefault(p => p.TradeId == tradeId);
            if (pos == null) return null;
            // Always allow close: if books gone (symbol left universe), use entry as marks
            var marks = getMarks(pos.Symbol, pos.LongExchange, pos.ShortExchange);
            decimal longBid, shortAsk;
            if (marks == null || marks.Value.longBid <= 0 || marks.Value.shortAsk <= 0)
            {
                longBid = pos.LongEntry > 0 ? pos.LongEntry : 1m;
                shortAsk = pos.ShortEntry > 0 ? pos.ShortEntry : 1m;
                _logger.LogWarning(
                    "ForceClose {Sym} without live marks — using entry L={L} S={S}",
                    pos.Symbol, longBid, shortAsk);
            }
            else
            {
                (longBid, shortAsk) = marks.Value;
            }

            var longFee = R.EstimatedTakerFees.GetValueOrDefault(pos.LongExchange, 0.05m);
            var shortFee = R.EstimatedTakerFees.GetValueOrDefault(pos.ShortExchange, 0.05m);
            var closeFactor = R.PaperCloseFeeFactor > 0 && R.PaperCloseFeeFactor <= 1m ? R.PaperCloseFeeFactor : 1m;
                // Scalp/realistic: partial maker on exit reduces close fee drag
                var closeFees = (longBid * pos.BaseQty * (longFee / 100m) + shortAsk * pos.BaseQty * (shortFee / 100m)) * closeFactor;
            var legsPnl = (pos.ShortEntry - shortAsk) * pos.BaseQty
                          + (longBid - pos.LongEntry) * pos.BaseQty;
            var tradeForFees = _trades.FirstOrDefault(x => x.Id == pos.TradeId);
            var openFees = tradeForFees?.OpenFeesUsd ?? 0m;
            var pnl = legsPnl - openFees - closeFees;
            var marginEach = pos.LockedMarginUsd > 0
                ? pos.LockedMarginUsd
                : pos.LongEntry * pos.BaseQty / (pos.Leverage > 0 ? pos.Leverage : 5m);

            var walletCredit = marginEach + (legsPnl - closeFees) / 2m;
            _margin.AddOrUpdate(pos.LongExchange, walletCredit, (_, v) => v + walletCredit);
            _margin.AddOrUpdate(pos.ShortExchange, walletCredit, (_, v) => v + walletCredit);
            RealizedPnlUsd += pnl;
            if (DateTime.UtcNow.Date != _dayUtc) { _dayUtc = DateTime.UtcNow.Date; DailyRealizedPnlUsd = 0; }
            DailyRealizedPnlUsd += pnl;
            _positions.Remove(pos);

            FuturesPaperTrade? closedTrade = null;
            var trade = _trades.FirstOrDefault(x => x.Id == pos.TradeId);
            if (trade != null)
            {
                var idx = _trades.IndexOf(trade);
                closedTrade = trade with
                {
                    ClosedAt = DateTime.UtcNow,
                    LongExit = longBid,
                    ShortExit = shortAsk,
                    CloseFeesUsd = closeFees,
                    RealizedPnlUsd = pnl,
                    IsOpen = false,
                    Status = "Closed(manual)",
                    Message = $"Manual close | PnL {pnl:F4} USD"
                };
                _trades[idx] = closedTrade;
                _analytics.RecordClose(closedTrade);
            }
            SaveOpenState();
            _logger.LogInformation("FUT PAPER MANUAL CLOSE {Sym} pnl={Pnl:F4}", pos.Symbol, pnl);
            return closedTrade;
        }

        FuturesPaperTrade FailClose(string msg) => new()
        {
            Symbol = "?",
            LongExchange = "?",
            ShortExchange = "?",
            Status = "Skipped",
            Message = msg,
            IsOpen = false
        };
    }

    public int TryCloseConverged(
        Func<string, string, string, (decimal longBid, decimal shortAsk)?> getMarks,
        decimal closeWhenNetBelowPercent)
    {
        lock (_lock)
        {
            var closed = 0;
            foreach (var pos in _positions.ToList())
            {
                var marks = getMarks(pos.Symbol, pos.LongExchange, pos.ShortExchange);
                if (marks == null || marks.Value.longBid <= 0 || marks.Value.shortAsk <= 0)
                {
                    // No live book → cannot manage risk. After 8 minutes orphan the row
                    // (return locked margin, no fictional fill PnL) so UI stops lying "4 open".
                    if ((DateTime.UtcNow - pos.OpenedAt).TotalMinutes >= 8)
                    {
                        var marginEachOrphan = pos.LockedMarginUsd > 0
                            ? pos.LockedMarginUsd
                            : pos.LongEntry * pos.BaseQty / (pos.Leverage > 0 ? pos.Leverage : 5m);
                        _margin.AddOrUpdate(pos.LongExchange, marginEachOrphan, (_, v) => v + marginEachOrphan);
                        _margin.AddOrUpdate(pos.ShortExchange, marginEachOrphan, (_, v) => v + marginEachOrphan);
                        var tr = _trades.FirstOrDefault(x => x.Id == pos.TradeId);
                        if (tr != null)
                        {
                            var idx = _trades.IndexOf(tr);
                            _trades[idx] = tr with
                            {
                                ClosedAt = DateTime.UtcNow,
                                IsOpen = false,
                                Status = "Closed(stale-no-book)",
                                Message = "Orphaned: no marks on both legs for 8m — margin returned, no exit fill",
                                RealizedPnlUsd = 0
                            };
                        }
                        _positions.Remove(pos);
                        closed++;
                        _logger.LogWarning("Paper orphan close {Sym} {L}/{S} — no book marks",
                            pos.Symbol, pos.LongExchange, pos.ShortExchange);
                    }
                    continue;
                }
                var (longBid, shortAsk) = marks.Value;

                // Close: sell long at bid, buy back short at ask
                var exitSpreadPct = (longBid - shortAsk) / shortAsk * 100m; // usually negative when converged
                // Entry locked edge roughly (shortEntry - longEntry); exit cost is crossing
                var longFee = R.EstimatedTakerFees.GetValueOrDefault(pos.LongExchange, 0.05m);
                var shortFee = R.EstimatedTakerFees.GetValueOrDefault(pos.ShortExchange, 0.05m);
                var closeFactor = R.PaperCloseFeeFactor > 0 && R.PaperCloseFeeFactor <= 1m ? R.PaperCloseFeeFactor : 1m;
                // Scalp/realistic: partial maker on exit reduces close fee drag
                var closeFees = (longBid * pos.BaseQty * (longFee / 100m) + shortAsk * pos.BaseQty * (shortFee / 100m)) * closeFactor;
                // Gross mark-to-market of both legs before fees
                var legsPnl = (pos.ShortEntry - shortAsk) * pos.BaseQty
                              + (longBid - pos.LongEntry) * pos.BaseQty;
                // Open fees already left the wallet at entry — must be in realized
                var openFees = 0m;
                var existingTrade = _trades.FirstOrDefault(x => x.Id == pos.TradeId);
                if (existingTrade != null)
                    openFees = existingTrade.OpenFeesUsd;

                // Economic PnL = legs − open fees − close fees (matches wallet equity change)
                var pnl = legsPnl - openFees - closeFees;

                var currentWidth = (shortAsk > 0 && longBid > 0)
                    ? (shortAsk - longBid) / longBid * 100m
                    : 0m;

                // Economic projected PnL (already includes open+close fees)
                pos.UnrealizedPnlUsd = pnl;
                pos.CurrentWidthPercent = currentWidth;

                var stop = R.FuturesStopLossUsd;
                var stopHit = stop < 0 && pnl <= stop;

                // ── PROFESSIONAL EXIT (spatial arb) ─────────────────────────
                // NEVER close on a pure timer into red — that is forced -EV.
                // Exit only when: (1) stop-loss, (2) take-profit / width converged in profit.
                // Optional soft timer: ONLY if PnL >= scaled min TP (harvest green, not bleed).
                // Hard max-hold: DISABLED unless FuturesHardMaxHoldMinutes > 0 explicitly.

                var threshold = closeWhenNetBelowPercent;
                var shrunkALot = pos.EntryWidthPercent > 0 && currentWidth <= pos.EntryWidthPercent * 0.35m;
                var belowAbs = currentWidth <= threshold;
                var widthConverged = belowAbs || shrunkALot;

                var notional = pos.LongEntry * pos.BaseQty;
                var minTp = R.MinTakeProfitUsd > 0 ? R.MinTakeProfitUsd : 0.30m;
                // Need at least ~0.15% of notional after fees to cover noise
                var minTpScaled = Math.Max(minTp, notional * 0.0015m);
                var takeProfit = pnl >= minTpScaled;
                var convergeProfit = widthConverged && pnl >= minTpScaled * 0.85m;
                var earlyTp = takeProfit && currentWidth <= pos.EntryWidthPercent * 0.55m;

                var timedOut = false;
                if (R.FuturesMaxHoldSeconds > 0)
                    timedOut = (DateTime.UtcNow - pos.OpenedAt).TotalSeconds >= R.FuturesMaxHoldSeconds;
                else if (R.FuturesMaxHoldMinutes > 0)
                    timedOut = (DateTime.UtcNow - pos.OpenedAt).TotalMinutes >= R.FuturesMaxHoldMinutes;

                // Soft clock: only exit if already at real TP — never "timeout at 0"
                var timeoutHarvest = timedOut && pnl >= minTpScaled;

                // Hard clock: off by default (0). If set, still require not worse than -tiny fee dust
                // unless stop already hit. We do NOT force red exits.
                var hardTimedOut = R.FuturesHardMaxHoldMinutes > 0
                    && (DateTime.UtcNow - pos.OpenedAt).TotalMinutes >= R.FuturesHardMaxHoldMinutes;
                // Even hard hold only flattens if flat-or-green OR stop — never forced red dump
                var hardExit = hardTimedOut && pnl >= 0m;

                if (!stopHit && !convergeProfit && !earlyTp && !timeoutHarvest && !hardExit)
                    continue;

                var reason = stopHit ? "stop-loss"
                    : earlyTp || (takeProfit && widthConverged) ? "take-profit"
                    : convergeProfit ? "converge"
                    : timeoutHarvest || hardExit ? "timeout-harvest"
                    : "converge";
                var marginEach = pos.LockedMarginUsd > 0
                    ? pos.LockedMarginUsd
                    : pos.LongEntry * pos.BaseQty / (pos.Leverage > 0 ? pos.Leverage : 5m);

                // Wallet: return locked margin; legs+close already netted in pnl; open fees were taken at open
                // free += marginEach + (legsPnl - closeFees)/2  per side
                // Equivalent: free += marginEach + (pnl + openFees)/2  since pnl = legs - open - close
                var walletCredit = marginEach + (legsPnl - closeFees) / 2m;
                _margin.AddOrUpdate(pos.LongExchange, walletCredit, (_, v) => v + walletCredit);
                _margin.AddOrUpdate(pos.ShortExchange, walletCredit, (_, v) => v + walletCredit);

                RealizedPnlUsd += pnl;
                if (DateTime.UtcNow.Date != _dayUtc)
                {
                    _dayUtc = DateTime.UtcNow.Date;
                    DailyRealizedPnlUsd = 0;
                }
                DailyRealizedPnlUsd += pnl;
                _lastCloseUtc = DateTime.UtcNow;
                _positions.Remove(pos);

                var trade = _trades.FirstOrDefault(t => t.Id == pos.TradeId);
                if (trade != null)
                {
                    var idx = _trades.IndexOf(trade);
                    var closedTrade = trade with
                    {
                        ClosedAt = DateTime.UtcNow,
                        LongExit = longBid,
                        ShortExit = shortAsk,
                        CloseFeesUsd = closeFees,
                        RealizedPnlUsd = pnl,
                        IsOpen = false,
                        Status = reason switch
                        {
                            "stop-loss" => "Closed(stop)",
                            "take-profit" => "Closed(tp)",
                            "timeout-hard" => "Closed(timeout-hard)",
                            "timeout" => "Closed(timeout)",
                            "timeout-harvest" => "Closed(timeout-harvest)",
                            _ => "Closed(converge)"
                        },
                        Message = $"PnL {pnl:F4} (legs-fees) openFee={openFees:F2} closeFee={closeFees:F2} | {reason}"
                    };
                    _trades[idx] = closedTrade;
                    _analytics.RecordClose(closedTrade);
                }

                closed++;
                SaveOpenState();
                _logger.LogInformation("FUT PAPER CLOSE {Sym} pnl={Pnl:F4} reason={R} lev={Lev}x",
                    pos.Symbol, pnl, reason, pos.Leverage > 0 ? pos.Leverage : 5m);
            }
            return closed;
        }
    }

    public IReadOnlyList<FuturesPaperTrade> GetTrades(int take = 40)
    {
        lock (_lock) return _trades.Take(take).ToList();
    }


    /// <summary>Close positions whose symbol left the active universe.</summary>
    public int PruneOrphanPositions(IReadOnlyCollection<string> activeSymbols)
    {
        var set = new HashSet<string>(activeSymbols ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        List<Guid> ids;
        lock (_lock)
        {
            ids = set.Count == 0
                ? []
                : _positions.Where(p => !set.Contains(p.Symbol)).Select(p => p.TradeId).ToList();
        }
        var n = 0;
        foreach (var id in ids)
        {
            if (ForceClose(id, (_, __, ___) => null) != null)
                n++;
        }
        if (n > 0)
            _logger.LogInformation("Pruned {N} orphan paper positions (not in current universe)", n);
        return n;
    }

    /// <summary>Force-close every open paper hedge (UI cleanup).</summary>
    public int ForceCloseAll()
    {
        var ids = GetOpenPositions().Select(p => p.TradeId).ToList();
        var n = 0;
        foreach (var id in ids)
        {
            if (ForceClose(id, (_, __, ___) => null) != null)
                n++;
        }
        return n;
    }

    public IReadOnlyList<FuturesPaperPosition> GetOpenPositions()
    {
        lock (_lock) return _positions.ToList();
    }

    public IReadOnlyDictionary<string, decimal> GetMarginBalances()
    {
        lock (_lock) return _margin.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }


    public void UpdateMarkToMarket(Func<string, string, string, (decimal longBid, decimal shortAsk)?> getMarks)
    {
        lock (_lock)
        {
            decimal sum = 0;
            foreach (var pos in _positions)
            {
                var marks = getMarks(pos.Symbol, pos.LongExchange, pos.ShortExchange);
                if (marks == null) continue;
                var (longBid, shortAsk) = marks.Value;
                if (longBid <= 0 || shortAsk <= 0) continue;
                // Close long at bid, cover short at ask
                var longFee = R.EstimatedTakerFees.GetValueOrDefault(pos.LongExchange, 0.05m) / 100m;
                var shortFee = R.EstimatedTakerFees.GetValueOrDefault(pos.ShortExchange, 0.05m) / 100m;
                var longPnl = (longBid - pos.LongEntry) * pos.BaseQty - pos.LongEntry * pos.BaseQty * longFee - longBid * pos.BaseQty * longFee;
                var shortPnl = (pos.ShortEntry - shortAsk) * pos.BaseQty - pos.ShortEntry * pos.BaseQty * shortFee - shortAsk * pos.BaseQty * shortFee;
                pos.UnrealizedPnlUsd = longPnl + shortPnl;
                pos.CurrentWidthPercent = (shortAsk - longBid) / longBid * 100m;
                sum += pos.UnrealizedPnlUsd;
            }
            UnrealizedHintUsd = sum;
        }
    }


    private void Trim()
    {
        if (_trades.Count > 200) _trades.RemoveRange(200, _trades.Count - 200);
    }

    private FuturesPaperTrade Fail(FuturesOpportunity opp, string message)
    {
        _analytics.RecordSkip(opp, message);
        return new FuturesPaperTrade
        {
            Symbol = opp.Symbol,
            LongExchange = opp.LongExchange,
            ShortExchange = opp.ShortExchange,
            BaseQty = opp.BaseQty,
            LongEntry = opp.LongAskVwap,
            ShortEntry = opp.ShortBidVwap,
            IsOpen = false,
            Status = "Skipped",
            Message = message
        };
    }


    private bool TryRestore(IReadOnlyList<string> exchanges)
    {
        try
        {
            if (!File.Exists(SnapshotPath))
            {
                // Still try ledger-only history for UI
                return TryLoadLedgerHistoryOnly(exchanges);
            }

            var json = File.ReadAllText(SnapshotPath);
            var snap = JsonSerializer.Deserialize<PaperOpenSnapshot>(json, SnapshotJson);
            if (snap == null)
                return TryLoadLedgerHistoryOnly(exchanges);

            var hasPos = snap.Positions is { Count: > 0 };
            var hasTrades = snap.Trades is { Count: > 0 };
            var hasPnl = snap.RealizedPnlUsd != 0 || snap.DailyRealizedPnlUsd != 0;
            if (!hasPos && !hasTrades && !hasPnl)
                return TryLoadLedgerHistoryOnly(exchanges);

            lock (_lock)
            {
                _margin.Clear();
                var startBal = R.PaperStartingQuote > 0 ? R.PaperStartingQuote : 10_000m;
                foreach (var ex in exchanges)
                    _margin[ex] = startBal;
                if (snap.Margin != null)
                {
                    foreach (var kv in snap.Margin)
                        _margin[kv.Key] = kv.Value;
                    // ensure all configured exchanges present
                    foreach (var ex in exchanges)
                        if (!_margin.ContainsKey(ex))
                            _margin[ex] = startBal;
                }

                _positions.Clear();
                _trades.Clear();

                if (snap.Trades is { Count: > 0 })
                {
                    foreach (var tr in snap.Trades.OrderByDescending(x => x.OpenedAt).Take(200))
                        _trades.Add(tr);
                }

                if (snap.Positions is { Count: > 0 })
                {
                    foreach (var pos in snap.Positions)
                    {
                        _positions.Add(pos);
                        // ensure matching open trade exists
                        if (!_trades.Any(t => t.Id == pos.TradeId))
                        {
                            var id = pos.TradeId == Guid.Empty ? Guid.NewGuid() : pos.TradeId;
                            pos.TradeId = id;
                            _trades.Insert(0, new FuturesPaperTrade
                            {
                                Id = id,
                                OpenedAt = pos.OpenedAt,
                                Symbol = pos.Symbol,
                                LongExchange = pos.LongExchange,
                                ShortExchange = pos.ShortExchange,
                                BaseQty = pos.BaseQty,
                                LongEntry = pos.LongEntry,
                                ShortEntry = pos.ShortEntry,
                                IsOpen = true,
                                Status = "Open",
                                Message = "Restored after restart"
                            });
                        }
                    }
                }

                // Supplement from ledger file if trade list thin
                MergeLedgerIntoTradesUnlocked();

                RealizedPnlUsd = snap.RealizedPnlUsd;
                DailyRealizedPnlUsd = snap.DailyRealizedPnlUsd;
                _dayUtc = snap.DayUtc == default ? DateTime.UtcNow.Date : snap.DayUtc.Date;
                if (_dayUtc.Date != DateTime.UtcNow.Date)
                    DailyRealizedPnlUsd = 0;
                TradeAttempts = snap.TradeAttempts;
                _lastOpenUtc = snap.LastOpenUtc;
            }

            _logger.LogInformation(
                "Paper state restored: {Open} open, {Trades} trades in memory, realized={R:F2} from {Path}",
                _positions.Count, _trades.Count, RealizedPnlUsd, SnapshotPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not restore paper open-state — trying ledger only");
            return TryLoadLedgerHistoryOnly(exchanges);
        }
    }

    private bool TryLoadLedgerHistoryOnly(IReadOnlyList<string> exchanges)
    {
        try
        {
            var ledgerPath = Path.Combine(_env.ContentRootPath, "data", "paper", "trades-ledger.json");
            if (!File.Exists(ledgerPath)) return false;
            var list = JsonSerializer.Deserialize<List<JsonElement>>(File.ReadAllText(ledgerPath));
            if (list == null || list.Count == 0) return false;

            lock (_lock)
            {
                // fresh margin but keep history visible
                _margin.Clear();
                var startBal = R.PaperStartingQuote > 0 ? R.PaperStartingQuote : 10_000m;
                foreach (var ex in exchanges)
                    _margin[ex] = startBal;
                _positions.Clear();
                _trades.Clear();
                decimal realized = 0;
                foreach (var el in list.Take(200))
                {
                    var tr = TradeFromLedgerElement(el);
                    if (tr == null) continue;
                    _trades.Add(tr);
                    if (tr.RealizedPnlUsd is decimal p) realized += p;
                }
                RealizedPnlUsd = realized;
                DailyRealizedPnlUsd = 0;
                _dayUtc = DateTime.UtcNow.Date;
            }
            _logger.LogInformation("Loaded {N} trades from ledger (no open-state snapshot)", _trades.Count);
            SaveOpenState(); // write combined snapshot for next restart
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ledger-only restore failed");
            return false;
        }
    }

    private void MergeLedgerIntoTradesUnlocked()
    {
        try
        {
            var ledgerPath = Path.Combine(_env.ContentRootPath, "data", "paper", "trades-ledger.json");
            if (!File.Exists(ledgerPath)) return;
            var list = JsonSerializer.Deserialize<List<JsonElement>>(File.ReadAllText(ledgerPath));
            if (list == null) return;
            var existing = _trades.Select(t => t.Id).ToHashSet();
            foreach (var el in list)
            {
                var tr = TradeFromLedgerElement(el);
                if (tr == null || existing.Contains(tr.Id)) continue;
                _trades.Add(tr);
                existing.Add(tr.Id);
            }
            _trades.Sort((a, b) => b.OpenedAt.CompareTo(a.OpenedAt));
            if (_trades.Count > 200) _trades.RemoveRange(200, _trades.Count - 200);
        }
        catch { /* ignore */ }
    }

    private static FuturesPaperTrade? TradeFromLedgerElement(JsonElement el)
    {
        try
        {
            Guid id = Guid.Empty;
            if (el.TryGetProperty("Id", out var idEl) || el.TryGetProperty("id", out idEl))
                Guid.TryParse(idEl.ToString(), out id);
            if (id == Guid.Empty) id = Guid.NewGuid();

            string Sym(string a, string b) =>
                el.TryGetProperty(a, out var x) ? x.GetString() ?? "" :
                el.TryGetProperty(b, out var y) ? y.GetString() ?? "" : "";

            decimal Dec(params string[] names)
            {
                foreach (var n in names)
                    if (el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number)
                        return v.GetDecimal();
                return 0;
            }

            DateTime opened = DateTime.UtcNow;
            if (el.TryGetProperty("OpenedAt", out var oa) || el.TryGetProperty("openedAt", out oa))
                DateTime.TryParse(oa.ToString(), out opened);
            DateTime? closed = null;
            if (el.TryGetProperty("ClosedAt", out var ca) || el.TryGetProperty("closedAt", out ca))
            {
                if (DateTime.TryParse(ca.ToString(), out var c) && c.Year > 2000)
                    closed = c;
            }

            var status = Sym("Status", "status");
            if (string.IsNullOrEmpty(status)) status = closed != null ? "Closed" : "Open";
            decimal? pnl = null;
            if (el.TryGetProperty("realizedPnlUsd", out var rp) || el.TryGetProperty("RealizedPnlUsd", out rp))
            {
                if (rp.ValueKind == JsonValueKind.Number) pnl = rp.GetDecimal();
            }

            return new FuturesPaperTrade
            {
                Id = id,
                OpenedAt = opened,
                ClosedAt = closed,
                Symbol = Sym("Symbol", "symbol"),
                LongExchange = Sym("LongExchange", "longExchange"),
                ShortExchange = Sym("ShortExchange", "shortExchange"),
                BaseQty = Dec("BaseQty", "baseQty"),
                LongEntry = Dec("LongEntry", "longEntry"),
                ShortEntry = Dec("ShortEntry", "shortEntry"),
                RealizedPnlUsd = pnl,
                IsOpen = status.Equals("Open", StringComparison.OrdinalIgnoreCase),
                Status = status,
                Message = Sym("Message", "message")
            };
        }
        catch { return null; }
    }

    private void SaveOpenState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath)!);
            PaperOpenSnapshot snap;
            lock (_lock)
            {
                snap = new PaperOpenSnapshot
                {
                    SavedUtc = DateTime.UtcNow,
                    RealizedPnlUsd = RealizedPnlUsd,
                    DailyRealizedPnlUsd = DailyRealizedPnlUsd,
                    DayUtc = _dayUtc,
                    TradeAttempts = TradeAttempts,
                    LastOpenUtc = _lastOpenUtc,
                    Margin = _margin.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
                    Positions = _positions.Select(p => new FuturesPaperPosition
                    {
                        Symbol = p.Symbol,
                        LongExchange = p.LongExchange,
                        ShortExchange = p.ShortExchange,
                        BaseQty = p.BaseQty,
                        LongEntry = p.LongEntry,
                        ShortEntry = p.ShortEntry,
                        OpenedAt = p.OpenedAt,
                        TradeId = p.TradeId,
                        EntryWidthPercent = p.EntryWidthPercent,
                        LockedMarginUsd = p.LockedMarginUsd,
                        Leverage = p.Leverage,
                        UnrealizedPnlUsd = p.UnrealizedPnlUsd,
                        CurrentWidthPercent = p.CurrentWidthPercent
                    }).ToList(),
                    Trades = _trades.Take(150).ToList()
                };
            }
            var tmp = SnapshotPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snap, SnapshotJson));
            File.Copy(tmp, SnapshotPath, overwrite: true);
            File.Delete(tmp);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save paper open-state");
        }
    }

    private void ClearOpenStateFile()
    {
        try
        {
            if (File.Exists(SnapshotPath))
                File.Delete(SnapshotPath);
        }
        catch { /* ignore */ }
    }

    /// <summary>Flush disk state (call on shutdown).</summary>
    public void PersistNow() => SaveOpenState();

    private sealed class PaperOpenSnapshot
    {
        public DateTime SavedUtc { get; set; }
        public decimal RealizedPnlUsd { get; set; }
        public decimal DailyRealizedPnlUsd { get; set; }
        public DateTime DayUtc { get; set; }
        public int TradeAttempts { get; set; }
        public DateTime LastOpenUtc { get; set; }
        public Dictionary<string, decimal>? Margin { get; set; }
        public List<FuturesPaperPosition>? Positions { get; set; }
        public List<FuturesPaperTrade>? Trades { get; set; }
    }
}
