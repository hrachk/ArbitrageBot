using System.Collections.Concurrent;
using System.Text.Json;
using ArbitrageBot.Configuration;
using ArbitrageBot.Models;
using Binance.Net;
using Bitget.Net;
using Bybit.Net;
using CryptoClients.Net;
using CryptoClients.Net.Models;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.SharedApis;
using Microsoft.Extensions.Options;
using OKX.Net;
using GateIo.Net;
using Kucoin.Net;

namespace ArbitrageBot.Services;

/// <summary>
/// Phase 3: place/close dual-leg USDT-M hedges via shared PlaceFuturesOrderAsync.
/// Guard must allow CanPlaceOrders before any call.
/// </summary>
public sealed class LiveOrderEngine
{
    private readonly ISettingsStore _settings;
    private readonly LiveTradingGuard _guard;
    private readonly RuntimeRiskConfig _runtime;
    private readonly ILogger<LiveOrderEngine> _logger;
    private readonly LiveSafetyService _safety;
    private readonly IWebHostEnvironment _env;
    private readonly object _lock = new();
    private readonly List<LiveHedgePosition> _open = [];
    private readonly List<LiveHedgePosition> _closed = [];
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>Exchange → UTC until which we skip live orders (balance / hard rejects).</summary>
    private readonly ConcurrentDictionary<string, DateTime> _venueCooldownUntil = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan BalanceCooldown = TimeSpan.FromMinutes(3);

    private string LedgerPath => Path.Combine(_env.ContentRootPath, "data", "live", "trades-ledger.json");
    private ArbitrageOptions Opt => _runtime.Snapshot;

    public LiveOrderEngine(
        ISettingsStore settings,
        LiveTradingGuard guard,
        RuntimeRiskConfig runtime,
        IWebHostEnvironment env,
        LiveSafetyService safety,
        ILogger<LiveOrderEngine> logger)
    {
        _settings = settings;
        _guard = guard;
        _runtime = runtime;
        _env = env;
        _safety = safety;
        _logger = logger;
        TryLoad();
    }

    public IReadOnlyList<LiveHedgePosition> GetOpen() { lock (_lock) return _open.ToList(); }
    public IReadOnlyList<LiveHedgePosition> GetClosed(int take = 40)
    {
        lock (_lock) return _closed.Take(take).ToList();
    }

    public async Task<object> TryOpenAsync(LiveHedgeRequest req, CancellationToken ct)
    {
        var check = _guard.CheckOpenAllowed(_open.Count, req.NotionalUsd);
        if (!check.ok)
            return new { ok = false, error = check.reason };

        var safety = _safety.CanOpenLive(req.Symbol, req.LongExchange, req.ShortExchange, req.NotionalUsd);
        if (!safety.ok)
            return new { ok = false, error = "safety: " + safety.reason };

        if (req.BaseQty <= 0 || string.IsNullOrWhiteSpace(req.Symbol))
            return new { ok = false, error = "invalid qty/symbol" };

        // Same symbol already open
        lock (_lock)
        {
            if (_open.Any(p => p.Symbol.Equals(req.Symbol, StringComparison.OrdinalIgnoreCase)))
                return new { ok = false, error = "already open on symbol" };
        }

        // Venue cooldown after balance / hard rejects — stop spam open→fail→unwind
        if (IsCoolingDown(req.LongExchange, out var longCd))
            return new { ok = false, error = $"cooldown {req.LongExchange} {longCd.TotalSeconds:F0}s" };
        if (IsCoolingDown(req.ShortExchange, out var shortCd))
            return new { ok = false, error = $"cooldown {req.ShortExchange} {shortCd.TotalSeconds:F0}s" };
        if (IsSymbolCooling(req.Symbol, out var symCd))
            return new { ok = false, error = $"cooldown {req.Symbol} {symCd.TotalSeconds:F0}s" };

        // Both legs must be ready BEFORE any order (never open long without short client)
        var longClient = CreateClient(req.LongExchange);
        if (longClient == null)
            return new { ok = false, error = "no credentials for " + req.LongExchange };
        var shortClient = CreateClient(req.ShortExchange);
        if (shortClient == null)
            return new { ok = false, error = "no credentials for " + req.ShortExchange };

        var baseAsset = req.Symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
            ? req.Symbol[..^4] : req.Symbol;
        var sharedSym = new SharedSymbol(TradingMode.PerpetualLinear, baseAsset, "USDT");

        var refPrice  = req.LongAsk ?? req.ShortBid ?? 0m;
        var scanQty = RoundBaseQty(req.BaseQty, refPrice);
        // Use live-specific leverage (separate from paper leverage)
        var lev = req.Leverage > 0 ? req.Leverage
                : (Opt.LivePaperLeverage > 0 ? Opt.LivePaperLeverage : 3m);

        // Size from the real per-venue deposit. The scan qty is only a depth probe
        // (often the top-of-book size, e.g. 0.378 QNT ≈ $15) and must not be the order.
        var opt = Opt;
        var equity = opt.LiveEquityPerExchangeUsd > 0 ? opt.LiveEquityPerExchangeUsd : 100m;
        var usage  = opt.LiveMarginUsageFraction  > 0 ? opt.LiveMarginUsageFraction  : 0.25m;
        usage = Math.Clamp(usage, 0.15m, 0.50m);
        var target = equity * lev * usage;
        if (opt.LiveMaxNotionalUsd > 0 && target > opt.LiveMaxNotionalUsd)
            target = opt.LiveMaxNotionalUsd;
        var absCap = equity * lev * 0.50m;
        if (target > absCap) target = absCap;
        // A real scan ticket may shrink the order. A dust probe (< $20) must not.
        if (req.NotionalUsd >= 20m && req.NotionalUsd < target)
            target = req.NotionalUsd;
        if (target < 20m)
        {
            var why = $"live target {target:F2}$ below $20 — raise Live max notional / equity";
            return SkipSymbol(req.Symbol, why, TimeSpan.FromMinutes(2));
        }

        var roundedQty = refPrice > 0 ? RoundBaseQty(target / refPrice, refPrice) : scanQty;
        if (roundedQty <= 0)
            return new { ok = false, error = $"qty rounded to zero (raw={req.BaseQty} target={target:F2})" };

        var okxLeg = IsOkx(req.LongExchange) || IsOkx(req.ShortExchange);
        if (okxLeg)
        {
            await EnsureOkxLotsAsync(ct).ConfigureAwait(false);
            var aligned = AlignOkxBase(req.Symbol, roundedQty, out var lotWhy);
            if (aligned <= 0)
                return SkipSymbol(req.Symbol, lotWhy ?? "OKX lot reject", RejectCooldown);
            roundedQty = aligned;
        }

        var estNotional = refPrice > 0 ? roundedQty * refPrice : target;
        if (estNotional < 20m)
            return SkipSymbol(req.Symbol, $"notional {estNotional:F2}$ below $20 after lot step — not sent", RejectCooldown);

        // Do not open a hedge that is already red after both taker legs (open + close).
        if (req.LongAsk is > 0 && req.ShortBid is > 0 && req.ShortBid > req.LongAsk)
        {
            var grossOpen = (req.ShortBid.Value - req.LongAsk.Value) * roundedQty;
            var feesOpen = EstimateRoundTripFees(req.LongExchange, req.ShortExchange, req.LongAsk.Value, req.ShortBid.Value, roundedQty);
            var netOpen = grossOpen - feesOpen;
            var minTp = LiveMinNetUsd(estNotional);
            if (netOpen < minTp)
            {
                _logger.LogInformation(
                    "LIVE skip {Sym}: RT net {Net:F2}$ < min {Min:F2}$ (gross {G:F2} fees {F:F2})",
                    req.Symbol, netOpen, minTp, grossOpen, feesOpen);
                return SkipSymbol(req.Symbol, $"RT net {netOpen:F2}$ < min take-profit {minTp:F2}$ after fees — skip", TimeSpan.FromSeconds(45));
            }
        }

        var estMargin = lev > 0 ? estNotional / lev : estNotional;
        _logger.LogInformation(
            "LIVE size {Sym} qty={Q} notional≈{N:F2}$ margin≈{M:F2}$ (equity={E}$ lev={L}x usage={U:P0} scanQty={Scan})",
            req.Symbol, roundedQty, estNotional, estMargin, equity, lev, usage, scanQty);

        var longQty = QtyForExchange(req.LongExchange, req.Symbol, roundedQty);
        var shortQty = QtyForExchange(req.ShortExchange, req.Symbol, roundedQty);
        var clientId = "ab-" + Guid.NewGuid().ToString("N")[..16];

        // 1) LONG — market, hedge mode: no reduceOnly / no timeInForce
        var longReq = new PlaceFuturesOrderRequest(
            sharedSym,
            SharedOrderSide.Buy,
            SharedOrderType.Market,
            longQty,
            price: null,
            reduceOnly: null,
            leverage: lev,
            timeInForce: null,
            positionSide: SharedPositionSide.Long,
            marginMode: SharedMarginMode.Cross,
            clientOrderId: clientId + "-L",
            exchangeParameters: ExParams(req.LongExchange, isClose: false));

        var longResult = await longClient.PlaceFuturesOrderAsync(req.LongExchange, longReq, ct).ConfigureAwait(false);
        if (!longResult.Success)
        {
            var err = DescribeFail(longResult.Error, longResult.OriginalData, "long order failed");
            if (IsBalanceError(err))
                MarkCooldown(req.LongExchange, err);
            _logger.LogError("LIVE LONG fail {Ex} {Sym}: {Err}",
                req.LongExchange, req.Symbol, err);
            return SkipSymbol(req.Symbol, "LONG failed: " + err, RejectCooldown);
        }

        var longOrderId = longResult.Data?.Id ?? "?";
        var longAvg = req.LongAsk ?? 0;

        // 2) SHORT — same size; on fail always unwind long
        var shortReq = new PlaceFuturesOrderRequest(
            sharedSym,
            SharedOrderSide.Sell,
            SharedOrderType.Market,
            shortQty,
            price: null,
            reduceOnly: null,
            leverage: lev,
            timeInForce: null,
            positionSide: SharedPositionSide.Short,
            marginMode: SharedMarginMode.Cross,
            clientOrderId: clientId + "-S",
            exchangeParameters: ExParams(req.ShortExchange, isClose: false));

        var shortResult = await shortClient.PlaceFuturesOrderAsync(req.ShortExchange, shortReq, ct).ConfigureAwait(false);
        if (!shortResult.Success)
        {
            var err = DescribeFail(shortResult.Error, shortResult.OriginalData, "short order failed");
            if (IsBalanceError(err))
                MarkCooldown(req.ShortExchange, err);
            _logger.LogError("LIVE SHORT fail {Ex} {Sym}: {Err} — unwinding long",
                req.ShortExchange, req.Symbol, err);
            await TryReduceAsync(longClient, req.LongExchange, sharedSym, longQty, SharedOrderSide.Sell, SharedPositionSide.Long, ct)
                .ConfigureAwait(false);
            return SkipSymbol(req.Symbol, "SHORT failed: " + err + " (long unwind attempted)", RejectCooldown);
        }

        var shortOrderId = shortResult.Data?.Id ?? "?";
        var shortAvg = req.ShortBid ?? 0;

        var pos = new LiveHedgePosition
        {
            Symbol = req.Symbol.ToUpperInvariant(),
            LongExchange = req.LongExchange,
            ShortExchange = req.ShortExchange,
            BaseQty = roundedQty,
            NotionalUsd = estNotional,
            LongEntry = longAvg,
            ShortEntry = shortAvg,
            LongOrderId = longOrderId?.ToString(),
            ShortOrderId = shortOrderId?.ToString(),
            Status = "Open",
            Message = $"LIVE hedge L@{longAvg} S@{shortAvg}"
        };

        lock (_lock)
        {
            _open.Add(pos);
            SaveUnlocked();
        }

        _safety.MarkOrderSent(pos.LongExchange, pos.ShortExchange);
        _ = _safety.AlertAsync("LIVE OPEN",
            $"{pos.Symbol} L={pos.LongExchange} S={pos.ShortExchange} qty={pos.BaseQty}",
            CancellationToken.None);

        _logger.LogWarning("LIVE OPEN {Sym} L:{L} S:{S} qty={Q} longOid={Lo} shortOid={So}",
            pos.Symbol, pos.LongExchange, pos.ShortExchange, pos.BaseQty, pos.LongOrderId, pos.ShortOrderId);

        return new
        {
            ok = true,
            phase = 3,
            tradeId = pos.Id,
            pos.Symbol,
            pos.LongExchange,
            pos.ShortExchange,
            pos.BaseQty,
            pos.LongEntry,
            pos.ShortEntry,
            pos.LongOrderId,
            pos.ShortOrderId
        };
    }

    public async Task<object> TryCloseAsync(string tradeId, Func<string, string, string, (decimal longBid, decimal shortAsk)?>? getMarks, CancellationToken ct)
    {
        if (!_guard.CanPlaceOrders && !_guard.IsKilled)
        {
            // allow close even if disabled after enable, but not if never enabled - still try if we have open
        }

        LiveHedgePosition? pos;
        lock (_lock)
            pos = _open.FirstOrDefault(p => p.Id.ToString().Equals(tradeId, StringComparison.OrdinalIgnoreCase));

        if (pos == null)
            return new { ok = false, error = "trade not found" };

        var baseAsset = pos.Symbol.EndsWith("USDT") ? pos.Symbol[..^4] : pos.Symbol;
        var sharedSym = new SharedSymbol(TradingMode.PerpetualLinear, baseAsset, "USDT");
        if (IsOkx(pos.LongExchange) || IsOkx(pos.ShortExchange))
            await EnsureOkxLotsAsync(ct).ConfigureAwait(false);
        var longQty = QtyForExchange(pos.LongExchange, pos.Symbol, pos.BaseQty);
        var shortQty = QtyForExchange(pos.ShortExchange, pos.Symbol, pos.BaseQty);

        var longClient = CreateClient(pos.LongExchange);
        var shortClient = CreateClient(pos.ShortExchange);
        if (longClient == null || shortClient == null)
            return new { ok = false, error = "missing credentials to close" };

        // Close long: sell reduce
        var closeLong = await TryReduceAsync(longClient, pos.LongExchange, sharedSym, longQty, SharedOrderSide.Sell, SharedPositionSide.Long, ct)
            .ConfigureAwait(false);
        // Close short: buy reduce
        var closeShort = await TryReduceAsync(shortClient, pos.ShortExchange, sharedSym, shortQty, SharedOrderSide.Buy, SharedPositionSide.Short, ct)
            .ConfigureAwait(false);

        decimal? pnl = null;
        var feeNote = "";
        if (getMarks != null)
        {
            var m = getMarks(pos.Symbol, pos.LongExchange, pos.ShortExchange);
            if (m != null)
            {
                var (longBid, shortAsk) = m.Value;
                var gross = (pos.ShortEntry - shortAsk) * pos.BaseQty + (longBid - pos.LongEntry) * pos.BaseQty;
                var fees = RoundTripFees(pos, longBid, shortAsk);
                pnl = gross - fees;
                feeNote = $" gross={gross:F4} fees={fees:F4}";
            }
        }

        lock (_lock)
        {
            _open.Remove(pos);
            pos.IsOpen = false;
            pos.ClosedAt = DateTime.UtcNow;
            pos.Status = closeLong && closeShort ? "Closed" : "ClosedPartial";
            pos.RealizedPnlUsd = pnl;
            pos.Message = $"close longOk={closeLong} shortOk={closeShort} pnl={pnl}{feeNote}";
            _closed.Insert(0, pos);
            if (_closed.Count > 500) _closed.RemoveRange(500, _closed.Count - 500);
            SaveUnlocked();
        }

        if (pnl.HasValue)
            _guard.RecordRealized(pnl.Value);

        return new { ok = closeLong && closeShort, tradeId, pnl, status = pos.Status, message = pos.Message };
    }

    public async Task<int> TryCloseConvergedAsync(
        Func<string, string, string, (decimal longBid, decimal shortAsk)?> getMarks,
        decimal closeBelow,
        int maxHoldMinutes,
        decimal stopLossUsd,
        CancellationToken ct)
    {
        List<LiveHedgePosition> snapshot;
        lock (_lock) snapshot = _open.ToList();
        var closed = 0;
        foreach (var pos in snapshot)
        {
            var marks = getMarks(pos.Symbol, pos.LongExchange, pos.ShortExchange);
            if (marks == null) continue;
            var (longBid, shortAsk) = marks.Value;
            if (longBid <= 0 || shortAsk <= 0) continue;

            var width = (shortAsk - longBid) / longBid * 100m;
            var entryWidth = pos.LongEntry > 0 ? (pos.ShortEntry - pos.LongEntry) / pos.LongEntry * 100m : 0m;
            var gross = (pos.ShortEntry - shortAsk) * pos.BaseQty + (longBid - pos.LongEntry) * pos.BaseQty;
            var fees = RoundTripFees(pos, longBid, shortAsk);
            var net = gross - fees;
            var notional = pos.LongEntry > 0 ? pos.LongEntry * pos.BaseQty : pos.NotionalUsd;
            var minTp = LiveMinNetUsd(notional);
            // A normal exit must be green AFTER taker fees. Only the stop may book a loss.
            var stop = stopLossUsd < 0 && net <= stopLossUsd;
            var converged = width <= closeBelow || (entryWidth > 0 && width <= entryWidth * 0.35m);
            var shrunk = entryWidth > 0 && width <= entryWidth * 0.55m;
            var green = net >= minTp;
            var takeProfit = green && (converged || shrunk);
            var timedOut = maxHoldMinutes > 0 && (DateTime.UtcNow - pos.OpenedAt).TotalMinutes >= maxHoldMinutes;
            var timeoutHarvest = timedOut && green;

            if (!stop && !takeProfit && !timeoutHarvest) continue;

            var r = await TryCloseAsync(pos.Id.ToString(), getMarks, ct).ConfigureAwait(false);
            closed++;
            _logger.LogWarning(
                "LIVE auto-close {Sym} width={W:F3} net={N:F2} gross={G:F2} fees={F:F2} min={M:F2} stop={S} tp={T} timeout={H}",
                pos.Symbol, width, net, gross, fees, minTp, stop, takeProfit, timeoutHarvest);
        }
        return closed;
    }

    private async Task<bool> TryReduceAsync(
        ExchangeRestClient client,
        string exchange,
        SharedSymbol symbol,
        SharedQuantity qty,
        SharedOrderSide side,
        SharedPositionSide posSide,
        CancellationToken ct)
    {
        try
        {
            // Hedge mode (positionSide Long/Short): do NOT send reduceOnly.
            // Binance/Bitget reject reduceOnly in dual-side/hedge mode (-1106 / param errors).
            // Closing is expressed by opposite order side + same positionSide (+ Bitget tradeSide=close).
            var req = new PlaceFuturesOrderRequest(
                symbol,
                side,
                SharedOrderType.Market,
                qty,
                price: null,
                reduceOnly: null,
                leverage: null,
                timeInForce: null, // MARKET: no TIF (Binance rejects timeInForce on market)
                positionSide: posSide,
                marginMode: SharedMarginMode.Cross,
                clientOrderId: "ab-c-" + Guid.NewGuid().ToString("N")[..12],
                exchangeParameters: ExParams(exchange, isClose: true));

            var result = await client.PlaceFuturesOrderAsync(exchange, req, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                _logger.LogError("LIVE reduce fail {Ex}: {Err}", exchange, DescribeFail(result.Error, result.OriginalData, "reduce failed"));
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LIVE reduce exception {Ex}", exchange);
            return false;
        }
    }

    private ExchangeRestClient? CreateClient(string exchange)
    {
        var cred = _settings.GetCredential(exchange);
        if (cred == null || string.IsNullOrWhiteSpace(cred.ApiKey) || string.IsNullOrWhiteSpace(cred.ApiSecret))
            return null;

        var key = cred.ApiKey!.Trim();
        var secret = cred.ApiSecret!.Trim();
        var pass = (cred.Passphrase ?? "").Trim();
        var rest = new ExchangeRestClient();
        var bag = new ExchangeCredentials();
        var name = exchange.Trim();

        // OKX / Bitget / Kucoin require passphrase
        if ((name.Equals("OKX", StringComparison.OrdinalIgnoreCase)
             || name.Equals("Bitget", StringComparison.OrdinalIgnoreCase)
             || name.Equals("Kucoin", StringComparison.OrdinalIgnoreCase)
             || name.Equals("KuCoin", StringComparison.OrdinalIgnoreCase))
            && string.IsNullOrWhiteSpace(pass))
        {
            _logger.LogWarning("LIVE CreateClient {Ex}: passphrase required", name);
            return null;
        }

        if (name.Equals("Binance", StringComparison.OrdinalIgnoreCase))
        {
            bag.Binance = new BinanceCredentials(key, secret);
            rest.SetApiCredentials(bag);
            rest.Binance.SetApiCredentials(new BinanceCredentials(key, secret));
        }
        else if (name.Equals("Bybit", StringComparison.OrdinalIgnoreCase))
        {
            bag.Bybit = new BybitCredentials(key, secret);
            rest.SetApiCredentials(bag);
            rest.Bybit.SetApiCredentials(new BybitCredentials(key, secret));
        }
        else if (name.Equals("Bitget", StringComparison.OrdinalIgnoreCase))
        {
            bag.Bitget = new BitgetCredentials(key, secret, pass);
            rest.SetApiCredentials(bag);
            rest.Bitget.SetApiCredentials(new BitgetCredentials(key, secret, pass));
        }
        else if (name.Equals("OKX", StringComparison.OrdinalIgnoreCase))
        {
            bag.OKX = new OKXCredentials(key, secret, pass);
            rest.SetApiCredentials(bag);
            rest.OKX.SetApiCredentials(new OKXCredentials(key, secret, pass));
        }
        else if (name.Equals("GateIo", StringComparison.OrdinalIgnoreCase)
                 || name.Equals("GateIO", StringComparison.OrdinalIgnoreCase))
        {
            bag.GateIo = new GateIoCredentials(key, secret);
            rest.SetApiCredentials(bag);
            rest.GateIo.SetApiCredentials(new GateIoCredentials(key, secret));
        }
        else if (name.Equals("Kucoin", StringComparison.OrdinalIgnoreCase)
                 || name.Equals("KuCoin", StringComparison.OrdinalIgnoreCase))
        {
            bag.Kucoin = new KucoinCredentials(key, secret, pass);
            rest.SetApiCredentials(bag);
            rest.Kucoin.SetApiCredentials(new KucoinCredentials(key, secret, pass));
        }
        else
        {
            _logger.LogError("LIVE CreateClient: no order handler for {Ex}", name);
            return null;
        }

        return rest;
    }

    /// <summary>
    /// Exchange-specific parameters required by CryptoClients shared futures order API.
    /// Bitget requires ProductType + MarginAsset/marginCoin; in hedge mode also tradeSide open/close.
    /// </summary>
    private static ExchangeParameters ExParams(string exchange, bool isClose = false)
    {
        if (exchange.Equals("Bitget", StringComparison.OrdinalIgnoreCase))
        {
            // hedge-mode default on Bitget: tradeSide required (open|close); reduceOnly is one-way only
            return new ExchangeParameters(
                new ExchangeParameter("Bitget", "ProductType", "UsdtFutures"),
                new ExchangeParameter("Bitget", "MarginAsset", "USDT"),
                new ExchangeParameter("Bitget", "marginCoin", "USDT"),
                new ExchangeParameter("Bitget", "TradeSide", isClose ? "close" : "open"),
                new ExchangeParameter("Bitget", "tradeSide", isClose ? "close" : "open"));
        }
        if (exchange.Equals("GateIo", StringComparison.OrdinalIgnoreCase)
            || exchange.Equals("GateIO", StringComparison.OrdinalIgnoreCase))
        {
            return new ExchangeParameters(new ExchangeParameter("GateIo", "SettleAsset", "usdt"));
        }
        if (exchange.Equals("OKX", StringComparison.OrdinalIgnoreCase))
        {
            // Shared API requires MarginMode; TradeMode maps to tdMode=cross
            return new ExchangeParameters(
                new ExchangeParameter("OKX", "TradeMode", "cross"),
                new ExchangeParameter("OKX", "MarginMode", "Cross"),
                new ExchangeParameter("OKX", "tdMode", "cross"));
        }
        if (exchange.Equals("Kucoin", StringComparison.OrdinalIgnoreCase)
            || exchange.Equals("KuCoin", StringComparison.OrdinalIgnoreCase))
        {
            return new ExchangeParameters(
                new ExchangeParameter("Kucoin", "MarginMode", "Cross"));
        }
        return new ExchangeParameters();
    }

    /// <summary>Open fees at entry prices + close fees at the exit marks. No 0.85 discount.</summary>
    private decimal RoundTripFees(LiveHedgePosition pos, decimal longBid, decimal shortAsk)
    {
        var qty = pos.BaseQty;
        var open = pos.LongEntry * qty * TakerRate(pos.LongExchange) + pos.ShortEntry * qty * TakerRate(pos.ShortExchange);
        var close = longBid * qty * TakerRate(pos.LongExchange) + shortAsk * qty * TakerRate(pos.ShortExchange);
        return open + close;
    }

    private decimal EstimateRoundTripFees(string longEx, string shortEx, decimal longPx, decimal shortPx, decimal qty)
    {
        var open = longPx * qty * TakerRate(longEx) + shortPx * qty * TakerRate(shortEx);
        var close = longPx * qty * TakerRate(longEx) + shortPx * qty * TakerRate(shortEx);
        return open + close;
    }

    private decimal TakerRate(string ex)
    {
        var table = Opt.EstimatedTakerFees;
        if (table != null)
        {
            if (table.TryGetValue(ex, out var f) && f > 0) return f / 100m;
            foreach (var kv in table)
            {
                if (kv.Key.Equals(ex, StringComparison.OrdinalIgnoreCase) && kv.Value > 0)
                    return kv.Value / 100m;
            }
        }
        return 0.0005m;
    }

    /// <summary>Minimum net USD (after open+close taker) before a live hedge may be closed green.</summary>
    private decimal LiveMinNetUsd(decimal notional)
    {
        var floor = Opt.MinTakeProfitUsd > 0 ? Opt.MinTakeProfitUsd : 0.35m;
        var scaled = notional > 0 ? notional * 0.0015m : 0m;
        return Math.Max(floor, scaled);
    }

    /// <summary>
    /// OKX linear swaps are sized in contracts (sz = base / ctVal), stepped by lotSz.
    /// QNT-USDT-SWAP is ctVal=1, lotSz=0.1 — 0.378 is rejected with an empty client error.
    /// Binance/Bybit/Bitget shared path accepts base asset quantity.
    /// </summary>
    private static SharedQuantity QtyForExchange(string exchange, string symbol, decimal baseQty)
    {
        if (IsOkx(exchange) && OkxLots.TryGetValue(NormSym(symbol), out var lot) && lot.CtVal > 0)
            return SharedQuantity.Contracts(FloorStep(baseQty / lot.CtVal, lot.LotSz));
        if (IsOkx(exchange))
            return SharedQuantity.Contracts(baseQty);
        return SharedQuantity.Base(baseQty);
    }

    private object SkipSymbol(string symbol, string why, TimeSpan forHowLong)
    {
        MarkSymbolCooldown(symbol, why, forHowLong);
        _logger.LogInformation("LIVE skip {Sym}: {Why}", symbol, why);
        return new { ok = false, error = "cooldown " + why };
    }

    private static bool IsOkx(string exchange)
        => exchange.Equals("OKX", StringComparison.OrdinalIgnoreCase);

    private static string NormSym(string symbol)
        => symbol.Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal).ToUpperInvariant();

    private static decimal AlignOkxBase(string symbol, decimal baseQty, out string? why)
    {
        why = null;
        if (!OkxLots.TryGetValue(NormSym(symbol), out var lot))
        {
            why = "OKX contract spec not loaded for " + symbol + " — order not sent";
            return 0;
        }
        if (lot.CtVal <= 0 || lot.LotSz <= 0)
        {
            why = "OKX contract spec invalid for " + symbol;
            return 0;
        }
        var contracts = FloorStep(baseQty / lot.CtVal, lot.LotSz);
        if (contracts < lot.MinSz)
        {
            why = $"OKX {symbol} {contracts} contracts < min {lot.MinSz} (ctVal={lot.CtVal} lot={lot.LotSz})";
            return 0;
        }
        return contracts * lot.CtVal;
    }

    private static decimal FloorStep(decimal value, decimal step)
    {
        if (value <= 0 || step <= 0) return 0;
        var steps = Math.Floor(value / step);
        var v = steps * step;
        var scale = 0;
        var s = step;
        while (s < 1m && scale < 8)
        {
            s *= 10m;
            scale++;
        }
        return decimal.Round(v, scale, MidpointRounding.ToZero);
    }

    private async Task EnsureOkxLotsAsync(CancellationToken ct)
    {
        if (OkxLots.Count > 0 && DateTime.UtcNow - _okxLotsAt < TimeSpan.FromHours(6))
            return;
        await OkxLotLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (OkxLots.Count > 0 && DateTime.UtcNow - _okxLotsAt < TimeSpan.FromHours(6))
                return;
            string? body = null;
            foreach (var url in new[]
            {
                "https://www.okx.com/api/v5/public/instruments?instType=SWAP",
                "https://aws.okx.com/api/v5/public/instruments?instType=SWAP"
            })
            {
                try
                {
                    using var resp = await OkxHttp.GetAsync(url, ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("OKX instruments HTTP {Code} {Url}", (int)resp.StatusCode, url);
                        continue;
                    }
                    body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "OKX instruments fetch failed {Url}", url);
                }
            }
            if (string.IsNullOrEmpty(body))
                return;
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return;
            var n = 0;
            foreach (var el in data.EnumerateArray())
            {
                var id = el.TryGetProperty("instId", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrEmpty(id) || !id.EndsWith("-USDT-SWAP", StringComparison.OrdinalIgnoreCase))
                    continue;
                var ctVal = ReadDec(el, "ctVal", 0m);
                var lotSz = ReadDec(el, "lotSz", 0m);
                var minSz = ReadDec(el, "minSz", lotSz);
                if (ctVal <= 0 || lotSz <= 0) continue;
                if (minSz <= 0) minSz = lotSz;
                var baseCcy = el.TryGetProperty("ctValCcy", out var ccyEl) ? ccyEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(baseCcy))
                    baseCcy = id[..^"-USDT-SWAP".Length];
                OkxLots[NormSym(baseCcy + "USDT")] = new OkxLot(ctVal, lotSz, minSz);
                n++;
            }
            if (n > 0)
            {
                _okxLotsAt = DateTime.UtcNow;
                _logger.LogInformation("OKX swap specs loaded: {N}", n);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OKX instruments fetch failed");
        }
        finally
        {
            OkxLotLock.Release();
        }
    }

    private static decimal ReadDec(JsonElement el, string name, decimal fallback)
    {
        if (!el.TryGetProperty(name, out var p)) return fallback;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var n)) return n;
        if (p.ValueKind == JsonValueKind.String
            && decimal.TryParse(p.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var s))
            return s;
        return fallback;
    }

    private static string DescribeFail(CryptoExchange.Net.Objects.Error? error, string? originalData, string fallback)
    {
        string? msg = null;
        if (error != null)
        {
            msg = error.ToString();
            if (!string.IsNullOrWhiteSpace(error.ErrorCode) && (msg == null || !msg.Contains(error.ErrorCode, StringComparison.Ordinal)))
                msg = (msg ?? "") + " code=" + error.ErrorCode;
        }
        if (!string.IsNullOrWhiteSpace(originalData))
        {
            var t = originalData.Trim();
            if (t.Length > 240) t = t[..240] + "…";
            msg = string.IsNullOrWhiteSpace(msg) ? t : msg + " | " + t;
        }
        return string.IsNullOrWhiteSpace(msg) ? fallback : msg;
    }

    private bool IsSymbolCooling(string symbol, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (!_symbolCooldownUntil.TryGetValue(symbol, out var until))
            return false;
        var left = until - DateTime.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            _symbolCooldownUntil.TryRemove(symbol, out _);
            return false;
        }
        remaining = left;
        return true;
    }

    private void MarkSymbolCooldown(string symbol, string reason, TimeSpan forHowLong)
    {
        _symbolCooldownUntil[symbol] = DateTime.UtcNow.Add(forHowLong);
        _logger.LogWarning("LIVE symbol cooldown {Sym} for {Sec:F0}s — {Reason}",
            symbol, forHowLong.TotalSeconds, Trunc(reason, 160));
    }

    private bool IsCoolingDown(string exchange, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (!_venueCooldownUntil.TryGetValue(exchange, out var until))
            return false;
        var left = until - DateTime.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            _venueCooldownUntil.TryRemove(exchange, out _);
            return false;
        }
        remaining = left;
        return true;
    }

    private void MarkCooldown(string exchange, string reason)
    {
        var until = DateTime.UtcNow.Add(BalanceCooldown);
        _venueCooldownUntil[exchange] = until;
        _logger.LogWarning("LIVE venue cooldown {Ex} for {Sec:F0}s — {Reason}",
            exchange, BalanceCooldown.TotalSeconds, Trunc(reason, 120));
    }

    private static bool IsBalanceError(string err)
    {
        if (string.IsNullOrEmpty(err)) return false;
        return err.Contains("balance", StringComparison.OrdinalIgnoreCase)
               || err.Contains("not enough", StringComparison.OrdinalIgnoreCase)
               || err.Contains("Insufficient", StringComparison.OrdinalIgnoreCase)
               || err.Contains("margin", StringComparison.OrdinalIgnoreCase)
               || err.Contains("ab not enough", StringComparison.OrdinalIgnoreCase);
    }

    private static string FailMsg(string? message, string? originalData, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(message) && !message.Equals(fallback, StringComparison.OrdinalIgnoreCase))
            return message;
        if (!string.IsNullOrWhiteSpace(originalData))
        {
            var t = originalData.Trim();
            if (t.Length > 200) t = t[..200] + "…";
            return string.IsNullOrWhiteSpace(message) ? t : message + " | " + t;
        }
        return string.IsNullOrWhiteSpace(message) ? fallback : message;
    }

    /// <summary>
    /// Round base quantity to a precision safe for most USDT-M perps.
    /// Avoids Binance error -1111 "Precision is over the maximum defined for this asset"
    /// when fill-estimate produces long fractional tails (e.g. ZKPUSDT).
    /// Uses refPrice when available for tiered precision; floors (never rounds up).
    /// </summary>
    public static decimal RoundBaseQty(decimal qty, decimal refPrice = 0)
    {
        if (qty <= 0) return 0;

        int decimals;
        if (refPrice > 0)
        {
            if (refPrice < 0.01m) decimals = 0;
            else if (refPrice < 0.1m) decimals = 0;
            else if (refPrice < 1m) decimals = 1;
            else if (refPrice < 10m) decimals = 2;
            else if (refPrice < 100m) decimals = 3;
            else decimals = 4;
        }
        else
        {
            if (qty >= 100m) decimals = 0;
            else if (qty >= 10m) decimals = 1;
            else if (qty >= 1m) decimals = 2;
            else decimals = 4;
        }

        var factor = (decimal)Math.Pow(10, decimals);
        var rounded = Math.Floor(qty * factor) / factor;
        return rounded > 0 ? rounded : 0;
    }

    private void TryLoad()
    {
        try
        {
            if (!File.Exists(LedgerPath)) return;
            var file = JsonSerializer.Deserialize<LiveLedgerFile>(File.ReadAllText(LedgerPath), JsonOpts);
            if (file == null) return;
            lock (_lock)
            {
                _open.Clear();
                _open.AddRange(file.Positions.Where(p => p.IsOpen));
                _closed.Clear();
                _closed.AddRange(file.Closed);
            }
            _logger.LogInformation("Live ledger restored: {O} open, {C} closed", _open.Count, _closed.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live ledger load failed");
        }
    }

    private void SaveUnlocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LedgerPath)!);
            var file = new LiveLedgerFile { Positions = _open.ToList(), Closed = _closed.Take(200).ToList() };
            var tmp = LedgerPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, JsonOpts));
            File.Copy(tmp, LedgerPath, true);
            File.Delete(tmp);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live ledger save failed");
        }
    }

    private static string? Trunc(string? s, int n = 240)
        => string.IsNullOrEmpty(s) ? null : (s.Length <= n ? s : s[..n] + "…");
}
