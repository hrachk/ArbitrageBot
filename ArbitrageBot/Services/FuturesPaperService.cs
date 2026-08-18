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
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, decimal> _margin = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FuturesPaperPosition> _positions = [];
    private readonly List<FuturesPaperTrade> _trades = [];
    private DateTime _lastOpenUtc = DateTime.MinValue;

    public decimal RealizedPnlUsd { get; private set; }
    public decimal UnrealizedHintUsd { get; set; }
    public int OpenCount { get { lock (_lock) return _positions.Count; } }
    public int TradeAttempts { get; private set; }

    public FuturesPaperService(IOptions<ArbitrageOptions> options, ILogger<FuturesPaperService> logger)
    {
        _options = options.Value;
        _logger = logger;
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

            var leverage = _options.FuturesPaperLeverage > 0 ? _options.FuturesPaperLeverage : 2m;
            var marginEach = opp.NotionalUsd / leverage;
            if (marginEach <= 0) marginEach = opp.NotionalUsd;

            if (!_margin.TryGetValue(opp.LongExchange, out var longBal) || longBal < marginEach)
                return Fail(opp, $"Low margin on {opp.LongExchange}");
            if (!_margin.TryGetValue(opp.ShortExchange, out var shortBal) || shortBal < marginEach)
                return Fail(opp, $"Low margin on {opp.ShortExchange}");

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
                Message = $"Hedge opened | net edge {opp.NetSpreadPercent:F3}%"
            };

            _positions.Add(new FuturesPaperPosition
            {
                Symbol = opp.Symbol,
                LongExchange = opp.LongExchange,
                ShortExchange = opp.ShortExchange,
                BaseQty = opp.BaseQty,
                LongEntry = opp.LongAskVwap,
                ShortEntry = opp.ShortBidVwap,
                OpenedAt = trade.OpenedAt,
                TradeId = trade.Id
            });

            _trades.Insert(0, trade);
            Trim();
            _lastOpenUtc = DateTime.UtcNow;
            _logger.LogInformation("FUT PAPER OPEN {Sym} L:{L} S:{S} qty={Q:F6} edge={E:F3}%",
                opp.Symbol, opp.LongExchange, opp.ShortExchange, opp.BaseQty, opp.NetSpreadPercent);
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
                var feeRate = 0.05m; // approx; detailed fees on close
                var closeFees = longBid * pos.BaseQty * (feeRate / 100m) + shortAsk * pos.BaseQty * (feeRate / 100m);
                var pnl = (pos.ShortEntry - shortAsk) * pos.BaseQty
                          + (longBid - pos.LongEntry) * pos.BaseQty
                          - closeFees;

                // Close if spread collapsed (short no longer premium) or max hold
                var stillWide = (pos.ShortEntry - pos.LongEntry) / pos.LongEntry * 100m;
                var currentWidth = (shortAsk > 0 && longBid > 0)
                    ? (shortAsk - longBid) / longBid * 100m
                    : 0m;

                var holdMin = _options.FuturesMaxHoldMinutes > 0 ? _options.FuturesMaxHoldMinutes : 30;
                var timedOut = (DateTime.UtcNow - pos.OpenedAt).TotalMinutes >= holdMin;
                var converged = currentWidth <= closeWhenNetBelowPercent;

                if (!converged && !timedOut) continue;

                var leverage = _options.FuturesPaperLeverage > 0 ? _options.FuturesPaperLeverage : 2m;
                var marginEach = pos.LongEntry * pos.BaseQty / leverage;

                _margin.AddOrUpdate(pos.LongExchange, marginEach + pnl / 2, (_, v) => v + marginEach + pnl / 2);
                _margin.AddOrUpdate(pos.ShortExchange, marginEach + pnl / 2, (_, v) => v + marginEach + pnl / 2);

                RealizedPnlUsd += pnl;
                _positions.Remove(pos);

                var trade = _trades.FirstOrDefault(t => t.Id == pos.TradeId);
                if (trade != null)
                {
                    var idx = _trades.IndexOf(trade);
                    _trades[idx] = trade with
                    {
                        ClosedAt = DateTime.UtcNow,
                        LongExit = longBid,
                        ShortExit = shortAsk,
                        CloseFeesUsd = closeFees,
                        RealizedPnlUsd = pnl,
                        IsOpen = false,
                        Status = timedOut ? "Closed(timeout)" : "Closed(converge)",
                        Message = $"PnL {pnl:F4} USD | width {currentWidth:F3}%"
                    };
                }

                closed++;
                _logger.LogInformation("FUT PAPER CLOSE {Sym} pnl={Pnl:F4} reason={R}",
                    pos.Symbol, pnl, timedOut ? "timeout" : "converge");
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

    private FuturesPaperTrade Fail(FuturesOpportunity opp, string msg) => new()
    {
        Symbol = opp.Symbol,
        LongExchange = opp.LongExchange,
        ShortExchange = opp.ShortExchange,
        BaseQty = opp.BaseQty,
        LongEntry = opp.LongAskVwap,
        ShortEntry = opp.ShortBidVwap,
        IsOpen = false,
        Status = "Skipped",
        Message = msg
    };

    private void Trim()
    {
        if (_trades.Count > 200) _trades.RemoveRange(200, _trades.Count - 200);
    }
}
