using System.Collections.Concurrent;
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
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, decimal> _margin = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FuturesPaperPosition> _positions = [];
    private readonly List<FuturesPaperTrade> _trades = [];
    private DateTime _lastOpenUtc = DateTime.MinValue;

    public decimal RealizedPnlUsd { get; private set; }
    public decimal UnrealizedHintUsd { get; set; }
    public decimal DailyRealizedPnlUsd { get; private set; }
    private DateTime _dayUtc = DateTime.UtcNow.Date;
    public int OpenCount { get { lock (_lock) return _positions.Count; } }
    public int TradeAttempts { get; private set; }

    public FuturesPaperService(
        IOptions<ArbitrageOptions> options,
        ILogger<FuturesPaperService> logger,
        IPaperAnalyticsStore analytics)
    {
        _options = options.Value;
        _logger = logger;
        _analytics = analytics;
    }

    public void Initialize(IEnumerable<string> exchanges) => Reset(exchanges);

    public void Reset(IEnumerable<string> exchanges)
    {
        lock (_lock)
        {
            _margin.Clear();
            var start = _options.PaperStartingQuote > 0 ? _options.PaperStartingQuote : 10_000m;
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
            _logger.LogInformation("Futures paper margin initialized: {Start} USDT x {N} exchanges", start, _margin.Count);
        }
    }

    public FuturesPaperTrade? TryOpen(FuturesOpportunity opp)
    {
        lock (_lock)
        {
            TradeAttempts++;
            if (!_options.PaperTrading) return Fail(opp, "Paper disabled");

            var cooldown = _options.PaperCooldownMs > 0 ? _options.PaperCooldownMs : 8000;
            if ((DateTime.UtcNow - _lastOpenUtc).TotalMilliseconds < cooldown)
                return Fail(opp, $"Cooldown {cooldown}ms");

            if (_options.PaperRequireFullFill && !opp.FullyFilled)
                return Fail(opp, "Require full fill");

            var maxPos = _options.FuturesMaxOpenPositions > 0 ? _options.FuturesMaxOpenPositions : 3;
            if (_positions.Count >= maxPos)
                return Fail(opp, $"Max open positions {maxPos}");

            // One position per symbol at a time
            if (_positions.Any(p => p.Symbol.Equals(opp.Symbol, StringComparison.OrdinalIgnoreCase)))
                return Fail(opp, "Already open on symbol");

            // Day rollover for daily loss limit
            if (DateTime.UtcNow.Date != _dayUtc)
            {
                _dayUtc = DateTime.UtcNow.Date;
                DailyRealizedPnlUsd = 0;
            }

            var dayLimit = _options.FuturesDailyLossLimitUsd;
            if (dayLimit < 0 && DailyRealizedPnlUsd <= dayLimit)
                return Fail(opp, $"Daily loss limit {dayLimit:F0} USDT hit ({DailyRealizedPnlUsd:F2})");

            var leverage = _options.FuturesPaperLeverage > 0 ? _options.FuturesPaperLeverage : 5m;
            if (leverage > 10m) leverage = 10m; // hard cap for paper safety

            var notionalCap = _options.FuturesMaxNotionalUsd > 0 ? _options.FuturesMaxNotionalUsd : 2500m;
            if (opp.NotionalUsd > notionalCap)
                return Fail(opp, $"Notional {opp.NotionalUsd:F0} > max {notionalCap:F0}");

            var marginEach = opp.NotionalUsd / leverage;
            if (marginEach <= 0) marginEach = opp.NotionalUsd;

            if (!_margin.TryGetValue(opp.LongExchange, out var longBal) || longBal < marginEach)
                return Fail(opp, $"Low margin on {opp.LongExchange}");
            if (!_margin.TryGetValue(opp.ShortExchange, out var shortBal) || shortBal < marginEach)
                return Fail(opp, $"Low margin on {opp.ShortExchange}");

            // Per-venue usage cap: do not lock more than X% of current free margin in one hedge leg
            var usage = _options.FuturesMaxMarginUsagePercent > 0 ? _options.FuturesMaxMarginUsagePercent : 0.25m;
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
                Leverage = leverage
            });

            _trades.Insert(0, trade);
            Trim();
            _lastOpenUtc = DateTime.UtcNow;
            _logger.LogInformation("FUT PAPER OPEN {Sym} L:{L} S:{S} qty={Q:F6} lev={Lev}x margin={M:F2} edge={E:F3}%",
                opp.Symbol, opp.LongExchange, opp.ShortExchange, opp.BaseQty, leverage, marginEach, opp.NetSpreadPercent);
            return trade;
        }
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
                if (marks == null) continue;
                var (longBid, shortAsk) = marks.Value;
                if (longBid <= 0 || shortAsk <= 0) continue;

                // Close: sell long at bid, buy back short at ask
                var exitSpreadPct = (longBid - shortAsk) / shortAsk * 100m; // usually negative when converged
                // Entry locked edge roughly (shortEntry - longEntry); exit cost is crossing
                var longFee = _options.EstimatedTakerFees.GetValueOrDefault(pos.LongExchange, 0.05m);
                var shortFee = _options.EstimatedTakerFees.GetValueOrDefault(pos.ShortExchange, 0.05m);
                var closeFees = longBid * pos.BaseQty * (longFee / 100m) + shortAsk * pos.BaseQty * (shortFee / 100m);
                var pnl = (pos.ShortEntry - shortAsk) * pos.BaseQty
                          + (longBid - pos.LongEntry) * pos.BaseQty
                          - closeFees;

                var currentWidth = (shortAsk > 0 && longBid > 0)
                    ? (shortAsk - longBid) / longBid * 100m
                    : 0m;

                var holdMin = _options.FuturesMaxHoldMinutes > 0 ? _options.FuturesMaxHoldMinutes : 30;
                var timedOut = (DateTime.UtcNow - pos.OpenedAt).TotalMinutes >= holdMin;

                // Converged: width below threshold OR shrunk to <= 40% of entry width
                var threshold = closeWhenNetBelowPercent;
                var shrunkALot = pos.EntryWidthPercent > 0 && currentWidth <= pos.EntryWidthPercent * 0.4m;
                var belowAbs = currentWidth <= threshold;
                var converged = belowAbs || shrunkALot;

                // Risk: force close on stop-loss (unrealized)
                pos.UnrealizedPnlUsd = pnl; // approximate mark with current exit prices
                var stop = _options.FuturesStopLossUsd;
                var stopHit = stop < 0 && pnl <= stop;

                if (!converged && !timedOut && !stopHit) continue;

                var reason = stopHit ? "stop-loss" : timedOut ? "timeout" : "converge";
                var marginEach = pos.LockedMarginUsd > 0
                    ? pos.LockedMarginUsd
                    : pos.LongEntry * pos.BaseQty / (pos.Leverage > 0 ? pos.Leverage : 5m);

                _margin.AddOrUpdate(pos.LongExchange, marginEach + pnl / 2, (_, v) => v + marginEach + pnl / 2);
                _margin.AddOrUpdate(pos.ShortExchange, marginEach + pnl / 2, (_, v) => v + marginEach + pnl / 2);

                RealizedPnlUsd += pnl;
                if (DateTime.UtcNow.Date != _dayUtc)
                {
                    _dayUtc = DateTime.UtcNow.Date;
                    DailyRealizedPnlUsd = 0;
                }
                DailyRealizedPnlUsd += pnl;
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
                            "timeout" => "Closed(timeout)",
                            _ => "Closed(converge)"
                        },
                        Message = $"PnL {pnl:F4} USD | width {currentWidth:F3}% | {reason}"
                    };
                    _trades[idx] = closedTrade;
                    _analytics.RecordClose(closedTrade);
                }

                closed++;
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
                var longFee = _options.EstimatedTakerFees.GetValueOrDefault(pos.LongExchange, 0.05m) / 100m;
                var shortFee = _options.EstimatedTakerFees.GetValueOrDefault(pos.ShortExchange, 0.05m) / 100m;
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
}
