using ArbitrageBot.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using ArbitrageBot.Configuration;

namespace ArbitrageBot.Services;

/// <summary>
/// Pushes live book/ticker/status to the UI over SignalR between full scan cycles.
/// Exchange data itself comes from WebSocket order books / book-tickers (FuturesMarketService).
/// </summary>
public sealed class RealtimeBroadcastService : BackgroundService
{
    private readonly IFuturesMarketService _futures;
    private readonly IHubContext<ArbitrageHub> _hub;
    private readonly ArbitrageState _state;
    private readonly ActiveMarketContext _markets;
    private readonly ArbitrageOptions _options;
    private readonly ILogger<RealtimeBroadcastService> _logger;

    public RealtimeBroadcastService(
        IFuturesMarketService futures,
        IHubContext<ArbitrageHub> hub,
        ArbitrageState state,
        ActiveMarketContext markets,
        IOptions<ArbitrageOptions> options,
        ILogger<RealtimeBroadcastService> logger)
    {
        _futures = futures;
        _hub = hub;
        _state = state;
        _markets = markets;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Realtime broadcast started (WS books → SignalR every ~300ms)");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_options.IsFuturesCross && _futures.IsReady)
                {
                    // Keep state depth/status fresh for snapshot consumers
                    var status = _futures.ConnectionStatus;
                    _state.SetConnectionStatus(status);

                    var symbols = _markets.Symbols.Take(12).ToList();
                    var depthMap = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    var tickers = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);

                    foreach (var sym in symbols)
                    {
                        depthMap[sym] = _futures.GetDepth(sym, 14);
                        var books = _futures.GetBookTickers(sym);
                        tickers[sym] = books.ToDictionary(
                            kv => kv.Key,
                            kv => (object)new
                            {
                                kv.Value.BestBid,
                                kv.Value.BestAsk,
                                kv.Value.BidQuantity,
                                kv.Value.AskQuantity,
                                kv.Value.Timestamp
                            },
                            StringComparer.OrdinalIgnoreCase);
                    }

                    _state.OrderBookDepth = depthMap;

                    var live = status.Count(kv =>
                        kv.Value.Contains("Synced", StringComparison.OrdinalIgnoreCase) ||
                        kv.Value.Contains("book-ticker", StringComparison.OrdinalIgnoreCase));

                    await _hub.Clients.All.SendAsync("MarketTick", new
                    {
                        utc = DateTime.UtcNow,
                        transport = "exchange-ws → signalr",
                        streamsLive = live,
                        streamsTotal = status.Count,
                        connectionStatus = status,
                        bookTickers = tickers,
                        orderBookDepth = depthMap
                    }, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Realtime tick failed");
            }

            await Task.Delay(300, stoppingToken);
        }
    }
}
