using System.Collections.Concurrent;
using BinanceEnums = Binance.Net.Enums;
using Binance.Net;
using CryptoClients.Net;
using CryptoClients.Net.Models;

namespace ArbitrageBot.Services;

/// <summary>State of one real order as the exchange reports it.</summary>
public sealed record MakerOrderState(bool Ok, string? Error, long OrderId, bool Open, bool Filled, decimal FilledQty, decimal AveragePrice);

/// <summary>
/// Real Binance USDⓈ-M orders for the density scalper: post-only (GTX) resting limits, cancel,
/// status, reduce-only market exit and an exchange-side STOP_MARKET (algo service) that closes the
/// position even if the bot or the home connection dies. Binance only; uses the native client
/// because the shared API has no post-only or close-position stop.
///
/// Hedge vs one-way account mode is read once: in hedge mode every order carries positionSide and
/// never reduceOnly (Binance rejects it); in one-way mode exits are reduceOnly.
/// </summary>
public sealed class BinanceMakerGateway
{
    private readonly ISettingsStore _settings;
    private readonly ILogger<BinanceMakerGateway> _logger;
    private bool? _hedgeMode;
    private DateTime _hedgeCheckedUtc;
    private static readonly ConcurrentDictionary<string, decimal> LeverageSet = new(StringComparer.OrdinalIgnoreCase);

    public BinanceMakerGateway(ISettingsStore settings, ILogger<BinanceMakerGateway> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool HasKeys()
    {
        var c = _settings.GetCredential("Binance");
        return c != null && c.Enabled && string.Equals(c.Permission, "trade", StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(c.ApiKey) && !string.IsNullOrWhiteSpace(c.ApiSecret);
    }

    private ExchangeRestClient? Client()
    {
        if (!HasKeys()) return null;
        var c = _settings.GetCredential("Binance")!;
        var rest = new ExchangeRestClient();
        var creds = new BinanceCredentials(c.ApiKey!.Trim(), c.ApiSecret!.Trim());
        rest.SetApiCredentials(new ExchangeCredentials { Binance = creds });
        rest.Binance.SetApiCredentials(creds);
        return rest;
    }

    private async Task<bool> HedgeModeAsync(ExchangeRestClient client, CancellationToken ct)
    {
        if (_hedgeMode is { } h && DateTime.UtcNow - _hedgeCheckedUtc < TimeSpan.FromMinutes(10)) return h;
        var r = await client.Binance.UsdFuturesApi.Account.GetPositionModeAsync(ct: ct).ConfigureAwait(false);
        if (r.Success) { _hedgeMode = r.Data.IsHedgeMode; _hedgeCheckedUtc = DateTime.UtcNow; }
        return _hedgeMode ?? false;
    }

    private static BinanceEnums.PositionSide? Side(bool hedge, bool isLong) =>
        hedge ? (isLong ? BinanceEnums.PositionSide.Long : BinanceEnums.PositionSide.Short) : null;

    public async Task EnsureLeverageAsync(string symbol, int leverage, CancellationToken ct)
    {
        if (LeverageSet.TryGetValue(symbol, out var l) && l == leverage) return;
        var client = Client();
        if (client == null) return;
        var r = await client.Binance.UsdFuturesApi.Account.ChangeInitialLeverageAsync(symbol, leverage, ct: ct).ConfigureAwait(false);
        if (r.Success) LeverageSet[symbol] = leverage;
        else _logger.LogWarning("DENSITY LIVE leverage {Sym} {L}x not set: {Err}", symbol, leverage, r.Error?.Message);
    }

    /// <summary>Post-only limit. Rejected by Binance instead of filling as taker when the price is already through.</summary>
    public async Task<MakerOrderState> PlaceMakerAsync(string symbol, bool isBuy, bool positionIsLong, decimal qty, decimal price,
        bool reduce, string clientId, CancellationToken ct)
    {
        var client = Client();
        if (client == null) return new(false, "no Binance trade key", 0, false, false, 0, 0);
        var hedge = await HedgeModeAsync(client, ct).ConfigureAwait(false);
        var r = await client.Binance.UsdFuturesApi.Trading.PlaceOrderAsync(
            symbol, isBuy ? BinanceEnums.OrderSide.Buy : BinanceEnums.OrderSide.Sell, BinanceEnums.FuturesOrderType.Limit,
            quantity: qty, price: price, positionSide: Side(hedge, positionIsLong),
            timeInForce: BinanceEnums.TimeInForce.GoodTillCrossing,
            reduceOnly: reduce && !hedge ? true : null, newClientOrderId: clientId, ct: ct).ConfigureAwait(false);
        if (!r.Success) return new(false, $"{r.Error?.Code} {r.Error?.Message}", 0, false, false, 0, 0);
        var o = r.Data;
        return new(true, null, o.Id, IsOpen(o.Status), o.Status == BinanceEnums.OrderStatus.Filled, o.QuantityFilled, o.AveragePrice);
    }

    public async Task<MakerOrderState> GetAsync(string symbol, long orderId, CancellationToken ct)
    {
        var client = Client();
        if (client == null) return new(false, "no Binance trade key", orderId, false, false, 0, 0);
        var r = await client.Binance.UsdFuturesApi.Trading.GetOrderAsync(symbol, orderId: orderId, ct: ct).ConfigureAwait(false);
        if (!r.Success) return new(false, $"{r.Error?.Code} {r.Error?.Message}", orderId, true, false, 0, 0);
        var o = r.Data;
        return new(true, null, o.Id, IsOpen(o.Status), o.Status == BinanceEnums.OrderStatus.Filled, o.QuantityFilled, o.AveragePrice);
    }

    /// <summary>Cancel, then read the final state: a cancel can race a fill, the fill wins.</summary>
    public async Task<MakerOrderState> CancelAsync(string symbol, long orderId, CancellationToken ct)
    {
        var client = Client();
        if (client == null) return new(false, "no Binance trade key", orderId, true, false, 0, 0);
        var c = await client.Binance.UsdFuturesApi.Trading.CancelOrderAsync(symbol, orderId: orderId, ct: ct).ConfigureAwait(false);
        if (!c.Success) _logger.LogInformation("DENSITY LIVE cancel {Sym} {Id}: {Err} (checking state)", symbol, orderId, c.Error?.Message);
        return await GetAsync(symbol, orderId, ct).ConfigureAwait(false);
    }

    /// <summary>Close (part of) the position at market, reduce-only.</summary>
    public async Task<MakerOrderState> MarketCloseAsync(string symbol, bool positionIsLong, decimal qty, CancellationToken ct)
    {
        var client = Client();
        if (client == null) return new(false, "no Binance trade key", 0, false, false, 0, 0);
        var hedge = await HedgeModeAsync(client, ct).ConfigureAwait(false);
        var r = await client.Binance.UsdFuturesApi.Trading.PlaceOrderAsync(
            symbol, positionIsLong ? BinanceEnums.OrderSide.Sell : BinanceEnums.OrderSide.Buy, BinanceEnums.FuturesOrderType.Market,
            quantity: qty, positionSide: Side(hedge, positionIsLong), reduceOnly: hedge ? null : true,
            newClientOrderId: "ab-dx-" + Guid.NewGuid().ToString("N")[..12], ct: ct).ConfigureAwait(false);
        if (!r.Success) return new(false, $"{r.Error?.Code} {r.Error?.Message}", 0, false, false, 0, 0);
        // A market order may answer before the fill is reported; read it back.
        var st = await GetAsync(symbol, r.Data.Id, ct).ConfigureAwait(false);
        return st.Ok ? st : new(true, null, r.Data.Id, false, true, qty, 0);
    }

    /// <summary>Exchange-side protective stop (STOP_MARKET, close position, mark price). Returns the algo id.</summary>
    public async Task<(bool Ok, long AlgoId, string? Error)> PlaceStopAsync(string symbol, bool positionIsLong, decimal stopPrice, CancellationToken ct)
    {
        var client = Client();
        if (client == null) return (false, 0, "no Binance trade key");
        var hedge = await HedgeModeAsync(client, ct).ConfigureAwait(false);
        var r = await client.Binance.UsdFuturesApi.Trading.PlaceConditionalOrderAsync(
            symbol, positionIsLong ? BinanceEnums.OrderSide.Sell : BinanceEnums.OrderSide.Buy, BinanceEnums.ConditionalOrderType.StopMarket,
            quantity: null, positionSide: Side(hedge, positionIsLong), triggerPrice: stopPrice,
            workingType: BinanceEnums.WorkingType.Mark, closePosition: true,
            clientOrderId: "ab-ds-" + Guid.NewGuid().ToString("N")[..12], ct: ct).ConfigureAwait(false);
        if (!r.Success) return (false, 0, $"{r.Error?.Code} {r.Error?.Message}");
        return (true, r.Data.Id, null);
    }

    public async Task CancelStopAsync(long algoId, CancellationToken ct)
    {
        if (algoId <= 0) return;
        var client = Client();
        if (client == null) return;
        var r = await client.Binance.UsdFuturesApi.Trading.CancelConditionalOrderAsync(orderId: algoId, ct: ct).ConfigureAwait(false);
        if (!r.Success) _logger.LogInformation("DENSITY LIVE stop cancel {Id}: {Err}", algoId, r.Error?.Message);
    }

    /// <summary>
    /// Remove every open conditional (algo) order on the symbol and return how many there were.
    /// A single-id cancel can answer "Unknown order" while the stop is still listed, so after a
    /// close we sweep by symbol; the density scalper is the only place that sets conditional orders.
    /// </summary>
    public async Task<int?> SweepStopsAsync(string symbol, CancellationToken ct)
    {
        var client = Client();
        if (client == null) return null;
        var open = await client.Binance.UsdFuturesApi.Trading.GetOpenConditionalOrdersAsync(symbol, ct: ct).ConfigureAwait(false);
        if (!open.Success) { _logger.LogWarning("DENSITY LIVE stop list {Sym}: {Err}", symbol, open.Error?.Message); return null; }
        var n = open.Data.Count();
        if (n == 0) return 0;
        var r = await client.Binance.UsdFuturesApi.Trading.CancelAllConditionalOrdersAsync(symbol, ct: ct).ConfigureAwait(false);
        if (r.Success) _logger.LogWarning("DENSITY LIVE removed {N} leftover stop order(s) on {Sym}", n, symbol);
        else _logger.LogWarning("DENSITY LIVE leftover stops on {Sym} ({N}) not removed: {Err}", symbol, n, r.Error?.Message);
        return n;
    }

    /// <summary>Signed position size for the side we hold (0 when flat), or null when it cannot be read.</summary>
    public async Task<decimal?> PositionQtyAsync(string symbol, bool positionIsLong, CancellationToken ct)
    {
        var client = Client();
        if (client == null) return null;
        var r = await client.Binance.UsdFuturesApi.Account.GetPositionInformationAsync(symbol, ct: ct).ConfigureAwait(false);
        if (!r.Success) return null;
        decimal q = 0;
        foreach (var p in r.Data)
        {
            if (!string.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase)) continue;
            if (p.PositionSide == BinanceEnums.PositionSide.Both) q += positionIsLong ? Math.Max(0, p.Quantity) : Math.Max(0, -p.Quantity);
            else if (p.PositionSide == (positionIsLong ? BinanceEnums.PositionSide.Long : BinanceEnums.PositionSide.Short)) q += Math.Abs(p.Quantity);
        }
        return q;
    }

    private static bool IsOpen(BinanceEnums.OrderStatus s) =>
        s is BinanceEnums.OrderStatus.New or BinanceEnums.OrderStatus.PartiallyFilled or BinanceEnums.OrderStatus.PendingNew;
}
