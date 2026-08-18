using System.Collections.Concurrent;
using ArbitrageBot.Configuration;
using ArbitrageBot.Models;
using Microsoft.Extensions.Options;

namespace ArbitrageBot.Services;

public class PaperExecutionService : IPaperExecutionService
{
    private readonly ArbitrageOptions _options;
    private readonly ILogger<PaperExecutionService> _logger;
    private readonly object _lock = new();

    // exchange -> asset -> qty
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, decimal>> _balances =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<PaperTrade> _trades = [];
    private DateTime _lastTradeUtc = DateTime.MinValue;

    public decimal RealizedPnlQuote { get; private set; }
    public int TradeCount { get; private set; }
    public int SuccessCount { get; private set; }

    public PaperExecutionService(IOptions<ArbitrageOptions> options, ILogger<PaperExecutionService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public void Initialize(IEnumerable<string> exchanges, IEnumerable<string> symbols)
    {
        lock (_lock)
        {
            _balances.Clear();
            var startQuote = _options.PaperStartingQuote > 0 ? _options.PaperStartingQuote : 10_000m;

            foreach (var ex in exchanges)
            {
                var bag = new ConcurrentDictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
                {
                    ["USDT"] = startQuote
                };

                // Seed each base asset with PaperStartingBase units (e.g. 0.01 BTC, 0.1 ETH) if configured
                foreach (var symbol in symbols)
                {
                    var baseAsset = ExtractBase(symbol);
                    if (string.IsNullOrEmpty(baseAsset)) continue;
                    if (_options.PaperStartingBaseUnits.TryGetValue(baseAsset, out var units) && units > 0)
                        bag[baseAsset] = units;
                    else if (_options.PaperStartingBaseDefault > 0)
                        bag.TryAdd(baseAsset, _options.PaperStartingBaseDefault);
                }

                _balances[ex] = bag;
            }

            _trades.Clear();
            RealizedPnlQuote = 0;
            TradeCount = 0;
            SuccessCount = 0;
            _lastTradeUtc = DateTime.MinValue;
            _logger.LogInformation("Paper portfolio initialized. Quote={Quote} per exchange, exchanges={Count}",
                startQuote, _balances.Count);
        }
    }

    public void Reset(IEnumerable<string> exchanges, IEnumerable<string> symbols) =>
        Initialize(exchanges, symbols);

    public PaperTrade TryExecute(ArbitrageOpportunity opportunity)
    {
        lock (_lock)
        {
            TradeCount++;

            if (!_options.PaperTrading)
            {
                return Fail(opportunity, "PaperTrading disabled");
            }

            var cooldown = _options.PaperCooldownMs > 0 ? _options.PaperCooldownMs : 5000;
            if ((DateTime.UtcNow - _lastTradeUtc).TotalMilliseconds < cooldown)
            {
                return Fail(opportunity, $"Cooldown {cooldown}ms");
            }

            var baseAsset = ExtractBase(opportunity.Symbol);
            if (string.IsNullOrEmpty(baseAsset))
                return Fail(opportunity, "Cannot parse base asset");

            var baseQty = opportunity.FillBaseQty;
            if (baseQty <= 0)
                return Fail(opportunity, "Zero base qty");

            var buyEx = opportunity.BuyExchange;
            var sellEx = opportunity.SellExchange;

            if (!_balances.ContainsKey(buyEx) || !_balances.ContainsKey(sellEx))
                return Fail(opportunity, "Exchange not in paper portfolio");

            var buyVwap = opportunity.BuyPriceVwap;
            var sellVwap = opportunity.SellPriceVwap;
            if (buyVwap <= 0 || sellVwap <= 0)
                return Fail(opportunity, "Invalid VWAP");

            var quoteSpent = buyVwap * baseQty;
            var quoteReceived = sellVwap * baseQty;
            var buyFee = quoteSpent * (opportunity.BuyFeePercent / 100m);
            var sellFee = quoteReceived * (opportunity.SellFeePercent / 100m);
            var totalCost = quoteSpent + buyFee;

            // Need USDT on buy exchange
            var buyUsdt = Get(buyEx, "USDT");
            if (buyUsdt < totalCost)
                return Fail(opportunity, $"Insufficient USDT on {buyEx}: have {buyUsdt:F2}, need {totalCost:F2}");

            // Need base on sell exchange
            var sellBase = Get(sellEx, baseAsset);
            if (sellBase < baseQty)
                return Fail(opportunity, $"Insufficient {baseAsset} on {sellEx}: have {sellBase:F6}, need {baseQty:F6}");

            // Execute legs
            Set(buyEx, "USDT", buyUsdt - totalCost);
            Set(buyEx, baseAsset, Get(buyEx, baseAsset) + baseQty);

            Set(sellEx, baseAsset, sellBase - baseQty);
            var sellUsdt = Get(sellEx, "USDT");
            Set(sellEx, "USDT", sellUsdt + quoteReceived - sellFee);

            var netPnl = (quoteReceived - sellFee) - totalCost;
            RealizedPnlQuote += netPnl;
            SuccessCount++;
            _lastTradeUtc = DateTime.UtcNow;

            var trade = new PaperTrade
            {
                Symbol = opportunity.Symbol,
                BuyExchange = buyEx,
                SellExchange = sellEx,
                BaseQty = baseQty,
                BuyVwap = buyVwap,
                SellVwap = sellVwap,
                BuyFeeQuote = buyFee,
                SellFeeQuote = sellFee,
                QuoteSpent = totalCost,
                QuoteReceived = quoteReceived - sellFee,
                NetPnlQuote = netPnl,
                NetPnlPercent = totalCost > 0 ? netPnl / totalCost * 100m : 0,
                Success = true,
                Message = "Executed"
            };

            _trades.Insert(0, trade);
            if (_trades.Count > 200) _trades.RemoveRange(200, _trades.Count - 200);

            _logger.LogInformation(
                "PAPER FILL {Symbol} {Buy}->{Sell} qty={Qty:F6} pnl={Pnl:F4} USDT ({Pct:F3}%)",
                trade.Symbol, trade.BuyExchange, trade.SellExchange, trade.BaseQty, trade.NetPnlQuote, trade.NetPnlPercent);

            return trade;
        }
    }

    public IReadOnlyList<PaperTrade> GetRecentTrades(int take = 50)
    {
        lock (_lock) return _trades.Take(take).ToList();
    }

    public IReadOnlyDictionary<string, Dictionary<string, decimal>> GetBalances()
    {
        lock (_lock)
        {
            return _balances.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    private decimal Get(string exchange, string asset) =>
        _balances.TryGetValue(exchange, out var bag) && bag.TryGetValue(asset, out var v) ? v : 0m;

    private void Set(string exchange, string asset, decimal value)
    {
        var bag = _balances.GetOrAdd(exchange, _ => new ConcurrentDictionary<string, decimal>(StringComparer.OrdinalIgnoreCase));
        bag[asset] = value;
    }

    private PaperTrade Fail(ArbitrageOpportunity o, string message) => new()
    {
        Symbol = o.Symbol,
        BuyExchange = o.BuyExchange,
        SellExchange = o.SellExchange,
        BaseQty = o.FillBaseQty,
        BuyVwap = o.BuyPriceVwap,
        SellVwap = o.SellPriceVwap,
        Success = false,
        Message = message,
        NetPnlQuote = 0,
        NetPnlPercent = 0
    };

    private static string ExtractBase(string symbol)
    {
        symbol = symbol.ToUpperInvariant();
        if (symbol.EndsWith("USDT")) return symbol[..^4];
        if (symbol.EndsWith("USDC")) return symbol[..^4];
        if (symbol.EndsWith("BTC") && symbol.Length > 3) return symbol[..^3];
        return symbol;
    }
}
