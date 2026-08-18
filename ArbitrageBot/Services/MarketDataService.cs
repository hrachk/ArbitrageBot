using ArbitrageBot.Configuration;
using ArbitrageBot.Models;
using Microsoft.Extensions.Options;

namespace ArbitrageBot.Services;

public class MarketDataService : IMarketDataService
{
    private readonly IOrderBookService _orderBooks;
    private readonly ArbitrageOptions _options;
    private readonly ILogger<MarketDataService> _logger;

    public MarketDataService(
        IOrderBookService orderBooks,
        IOptions<ArbitrageOptions> options,
        ILogger<MarketDataService> logger)
    {
        _orderBooks = orderBooks;
        _options = options.Value;
        _logger = logger;
    }

    public Task<Dictionary<string, BookTicker>> GetBookTickersAsync(string symbol, CancellationToken ct = default)
        => Task.FromResult(_orderBooks.GetBookTickers(symbol));

    public Task<IReadOnlyList<ArbitrageOpportunity>> ScanOpportunitiesAsync(CancellationToken ct = default)
    {
        var opportunities = new List<ArbitrageOpportunity>();
        var quoteSize = _options.QuoteSize > 0 ? _options.QuoteSize : 500m;

        foreach (var symbol in _options.Symbols)
        {
            if (ct.IsCancellationRequested) break;

            var tickers = _orderBooks.GetBookTickers(symbol);
            if (tickers.Count < 2) continue;

            var exchanges = tickers.Keys.ToList();
            for (var i = 0; i < exchanges.Count; i++)
            {
                for (var j = 0; j < exchanges.Count; j++)
                {
                    if (i == j) continue;

                    var buyEx = exchanges[i];
                    var sellEx = exchanges[j];

                    // Depth-aware fills
                    var buyFill = _orderBooks.EstimateFill(symbol, buyEx, quoteSize, isBuy: true);
                    var sellFill = _orderBooks.EstimateFill(symbol, sellEx, quoteSize, isBuy: false);

                    if (!buyFill.Success || !sellFill.Success)
                        continue;

                    // Use the smaller base qty both sides can support
                    var baseQty = Math.Min(buyFill.FilledBaseQty, sellFill.FilledBaseQty);
                    if (baseQty <= 0) continue;

                    // Re-scale quote if one side was thinner
                    var buyQuote = buyFill.VwapPrice * baseQty;
                    var sellQuote = sellFill.VwapPrice * baseQty;

                    if (buyFill.TopOfBookPrice <= 0 || sellFill.TopOfBookPrice <= 0)
                        continue;
                    if (sellFill.VwapPrice <= buyFill.VwapPrice)
                        continue;

                    var grossTop = (sellFill.TopOfBookPrice - buyFill.TopOfBookPrice) / buyFill.TopOfBookPrice * 100m;
                    var grossVwap = (sellFill.VwapPrice - buyFill.VwapPrice) / buyFill.VwapPrice * 100m;

                    var buyFee = _options.EstimatedTakerFees.GetValueOrDefault(buyEx, 0.1m);
                    var sellFee = _options.EstimatedTakerFees.GetValueOrDefault(sellEx, 0.1m);

                    // Fees on notional
                    var feeCostQuote = buyQuote * (buyFee / 100m) + sellQuote * (sellFee / 100m);
                    var grossPnlQuote = sellQuote - buyQuote;
                    var netPnlQuote = grossPnlQuote - feeCostQuote;
                    var netPct = buyQuote > 0 ? netPnlQuote / buyQuote * 100m : 0m;

                    if (netPct < _options.MinProfitPercent)
                        continue;

                    opportunities.Add(new ArbitrageOpportunity
                    {
                        Symbol = symbol,
                        BuyExchange = buyEx,
                        SellExchange = sellEx,
                        BuyPriceTop = buyFill.TopOfBookPrice,
                        SellPriceTop = sellFill.TopOfBookPrice,
                        BuyPriceVwap = buyFill.VwapPrice,
                        SellPriceVwap = sellFill.VwapPrice,
                        QuoteSize = quoteSize,
                        FillBaseQty = baseQty,
                        FullyFilled = buyFill.FullyFilled && sellFill.FullyFilled,
                        GrossSpreadTopPercent = grossTop,
                        GrossSpreadVwapPercent = grossVwap,
                        BuyFeePercent = buyFee,
                        SellFeePercent = sellFee,
                        NetProfitPercent = netPct,
                        NetProfitQuote = netPnlQuote,
                        BuySlippagePercent = buyFill.SlippagePercent,
                        SellSlippagePercent = sellFill.SlippagePercent
                    });
                }
            }
        }

        return Task.FromResult<IReadOnlyList<ArbitrageOpportunity>>(
            opportunities.OrderByDescending(o => o.NetProfitPercent).ToList());
    }
}
