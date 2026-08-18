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
    {
        var tickers = _orderBooks.GetBookTickers(symbol);
        return Task.FromResult(tickers);
    }

    public Task<IReadOnlyList<ArbitrageOpportunity>> ScanOpportunitiesAsync(CancellationToken ct = default)
    {
        var opportunities = new List<ArbitrageOpportunity>();

        foreach (var symbol in _options.Symbols)
        {
            if (ct.IsCancellationRequested) break;

            var tickers = _orderBooks.GetBookTickers(symbol);
            if (tickers.Count < 2) continue;

            var exchangeList = tickers.Keys.ToList();
            for (int i = 0; i < exchangeList.Count; i++)
            {
                for (int j = 0; j < exchangeList.Count; j++)
                {
                    if (i == j) continue;

                    var buyEx = exchangeList[i];
                    var sellEx = exchangeList[j];
                    var buyTicker = tickers[buyEx];
                    var sellTicker = tickers[sellEx];

                    if (buyTicker.BestAsk <= 0 || sellTicker.BestBid <= 0) continue;

                    var buyPrice = buyTicker.BestAsk;
                    var sellPrice = sellTicker.BestBid;
                    if (sellPrice <= buyPrice) continue;

                    var grossSpread = (sellPrice - buyPrice) / buyPrice * 100m;
                    var buyFee = _options.EstimatedTakerFees.GetValueOrDefault(buyEx, 0.1m);
                    var sellFee = _options.EstimatedTakerFees.GetValueOrDefault(sellEx, 0.1m);
                    var netProfit = grossSpread - buyFee - sellFee;

                    if (netProfit >= _options.MinProfitPercent)
                    {
                        opportunities.Add(new ArbitrageOpportunity
                        {
                            Symbol = symbol,
                            BuyExchange = buyEx,
                            SellExchange = sellEx,
                            BuyPrice = buyPrice,
                            SellPrice = sellPrice,
                            GrossSpreadPercent = grossSpread,
                            NetProfitPercent = netProfit,
                            BuyFeePercent = buyFee,
                            SellFeePercent = sellFee
                        });
                    }
                }
            }
        }

        return Task.FromResult<IReadOnlyList<ArbitrageOpportunity>>(
            opportunities.OrderByDescending(o => o.NetProfitPercent).ToList());
    }
}
