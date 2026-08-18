using ArbitrageBot.Configuration;
using ArbitrageBot.Models;
using CryptoClients.Net.Interfaces;
using CryptoExchange.Net.SharedApis;
using Microsoft.Extensions.Options;

namespace ArbitrageBot.Services;

public class MarketDataService : IMarketDataService
{
    private readonly IExchangeRestClient _restClient;
    private readonly ArbitrageOptions _options;
    private readonly ILogger<MarketDataService> _logger;

    public MarketDataService(
        IExchangeRestClient restClient,
        IOptions<ArbitrageOptions> options,
        ILogger<MarketDataService> logger)
    {
        _restClient = restClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Dictionary<string, BookTicker>> GetBookTickersAsync(string symbol, CancellationToken ct = default)
    {
        var result = new Dictionary<string, BookTicker>(StringComparer.OrdinalIgnoreCase);
        var sharedSymbol = ParseSymbol(symbol);
        var exchanges = _options.Exchanges.ToArray();

        try
        {
            var tickers = await _restClient.GetBookTickersAsync(
                new GetBookTickerRequest(sharedSymbol),
                exchanges,
                ct);

            foreach (var tickerResult in tickers)
            {
                if (!tickerResult.Success || tickerResult.Data == null)
                {
                    _logger.LogWarning("Failed to get book ticker from {Exchange} for {Symbol}: {Error}",
                        tickerResult.Exchange, symbol, tickerResult.Error?.Message ?? "Unknown");
                    continue;
                }

                var data = tickerResult.Data;
                result[tickerResult.Exchange] = new BookTicker
                {
                    Exchange = tickerResult.Exchange,
                    Symbol = symbol,
                    BestBid = data.BestBidPrice,
                    BestAsk = data.BestAskPrice,
                    BidQuantity = data.BestBidQuantity,
                    AskQuantity = data.BestAskQuantity,
                    Timestamp = DateTime.UtcNow
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching book tickers for {Symbol}", symbol);
        }

        return result;
    }

    public async Task<IReadOnlyList<ArbitrageOpportunity>> ScanOpportunitiesAsync(CancellationToken ct = default)
    {
        var opportunities = new List<ArbitrageOpportunity>();

        foreach (var symbol in _options.Symbols)
        {
            if (ct.IsCancellationRequested) break;

            var tickers = await GetBookTickersAsync(symbol, ct);
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

        return opportunities
            .OrderByDescending(o => o.NetProfitPercent)
            .ToList();
    }

    private static SharedSymbol ParseSymbol(string symbol)
    {
        symbol = symbol.ToUpperInvariant();

        if (symbol.EndsWith("USDT"))
        {
            var baseAsset = symbol[..^4];
            return new SharedSymbol(TradingMode.Spot, baseAsset, "USDT");
        }
        if (symbol.EndsWith("USDC"))
        {
            var baseAsset = symbol[..^4];
            return new SharedSymbol(TradingMode.Spot, baseAsset, "USDC");
        }
        if (symbol.EndsWith("BTC"))
        {
            var baseAsset = symbol[..^3];
            return new SharedSymbol(TradingMode.Spot, baseAsset, "BTC");
        }

        throw new ArgumentException($"Unsupported symbol format: {symbol}. Use e.g. BTCUSDT");
    }
}
