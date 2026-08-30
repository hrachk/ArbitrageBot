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

namespace ArbitrageBot.Services;

/// <summary>
/// Phase 3: place/close dual-leg USDT-M hedges via shared PlaceFuturesOrderAsync.
/// Guard must allow CanPlaceOrders before any call.
/// </summary>
public sealed class LiveOrderEngine
{
    private readonly ISettingsStore _settings;
    private readonly LiveTradingGuard _guard;
    private readonly ArbitrageOptions _options;
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

    public LiveOrderEngine(
        ISettingsStore settings,
        LiveTradingGuard guard,
        IOptions<ArbitrageOptions> options,
        IWebHostEnvironment env,
        LiveSafetyService safety,
        ILogger<LiveOrderEngine> logger)
    {
        _settings = settings;
        _guard = guard;
        _options = options.Value;
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

        var refPrice = req.LongAsk ?? req.ShortBid ?? 0m;
        var roundedQty = RoundBaseQty(req.BaseQty, refPrice);

        // Hard cap: min(request notional, LiveMaxNotional, 50) — thin accounts can't take 180 USDT legs
        var maxNotional = _options.LiveMaxNotionalUsd > 0 ? _options.LiveMaxNotionalUsd : 50m;
        if (maxNotional > 50m) maxNotional = 50m; // safety ceiling until balances are proven
        if (req.NotionalUsd > 0 && req.NotionalUsd < maxNotional)
            maxNotional = req.NotionalUsd;
        if (refPrice > 0)
        {
            var maxBase = maxNotional / refPrice;
            if (roundedQty > maxBase)
                roundedQty = RoundBaseQty(maxBase, refPrice);
        }
        if (roundedQty <= 0)
            return new { ok = false, error = $"qty rounded to zero (raw={req.BaseQty})" };

        var longQty = QtyForExchange(req.LongExchange, roundedQty);
        var shortQty = QtyForExchange(req.ShortExchange, roundedQty);
        var clientId = "ab-" + Guid.NewGuid().ToString("N")[..16];
        var lev = req.Leverage > 0 ? req.Leverage : 3;

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
            var err = FailMsg(longResult.Error?.Message, longResult.OriginalData, "long order failed");
            if (IsBalanceError(err))
                MarkCooldown(req.LongExchange, err);
            _logger.LogError("LIVE LONG fail {Ex} {Sym}: {Err} detail={D}",
                req.LongExchange, req.Symbol, err, Trunc(longResult.OriginalData));
            return new { ok = false, error = "LONG failed: " + err, leg = "long", detail = Trunc(longResult.OriginalData) };
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
            var err = FailMsg(shortResult.Error?.Message, shortResult.OriginalData, "short order failed");
            if (IsBalanceError(err))
                MarkCooldown(req.ShortExchange, err);
            _logger.LogError("LIVE SHORT fail {Ex} {Sym}: {Err} — unwinding long detail={D}",
                req.ShortExchange, req.Symbol, err, Trunc(shortResult.OriginalData));
            await TryReduceAsync(longClient, req.LongExchange, sharedSym, longQty, SharedOrderSide.Sell, SharedPositionSide.Long, ct)
                .ConfigureAwait(false);
            return new { ok = false, error = "SHORT failed: " + err + " (long unwind attempted)", leg = "short", detail = Trunc(shortResult.OriginalData) };
        }

        var shortOrderId = shortResult.Data?.Id ?? "?";
        var shortAvg = req.ShortBid ?? 0;

        var pos = new LiveHedgePosition
        {
            Symbol = req.Symbol.ToUpperInvariant(),
            LongExchange = req.LongExchange,
            ShortExchange = req.ShortExchange,
            BaseQty = roundedQty,
            NotionalUsd = req.NotionalUsd,
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
        var longQty = QtyForExchange(pos.LongExchange, pos.BaseQty);
        var shortQty = QtyForExchange(pos.ShortExchange, pos.BaseQty);

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
        if (getMarks != null)
        {
            var m = getMarks(pos.Symbol, pos.LongExchange, pos.ShortExchange);
            if (m != null)
            {
                var (longBid, shortAsk) = m.Value;
                pnl = (pos.ShortEntry - shortAsk) * pos.BaseQty + (longBid - pos.LongEntry) * pos.BaseQty;
            }
        }

        lock (_lock)
        {
            _open.Remove(pos);
            pos.IsOpen = false;
            pos.ClosedAt = DateTime.UtcNow;
            pos.Status = closeLong && closeShort ? "Closed" : "ClosedPartial";
            pos.RealizedPnlUsd = pnl;
            pos.Message = $"close longOk={closeLong} shortOk={closeShort} pnl={pnl}";
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
            var legsPnl = (pos.ShortEntry - shortAsk) * pos.BaseQty + (longBid - pos.LongEntry) * pos.BaseQty;
            var timedOut = (DateTime.UtcNow - pos.OpenedAt).TotalMinutes >= maxHoldMinutes;
            var stop = stopLossUsd < 0 && legsPnl <= stopLossUsd;
            var converged = width <= closeBelow;
            // Do not flatten a live loser on the clock — only SL or green timeout
            var timeoutGreen = timedOut && legsPnl >= 0m;

            if (!converged && !timeoutGreen && !stop) continue;

            var r = await TryCloseAsync(pos.Id.ToString(), getMarks, ct).ConfigureAwait(false);
            closed++;
            _logger.LogWarning("LIVE auto-close {Sym} width={W:F3} pnl~{P:F2} stop={S} timeout={T}",
                pos.Symbol, width, legsPnl, stop, timedOut);
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
                _logger.LogError("LIVE reduce fail {Ex}: {Err}", exchange, result.Error?.Message);
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

        // OKX / Bitget require passphrase
        if ((name.Equals("OKX", StringComparison.OrdinalIgnoreCase)
             || name.Equals("Bitget", StringComparison.OrdinalIgnoreCase))
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
        else
            return null;

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
        return new ExchangeParameters();
    }

    /// <summary>
    /// OKX linear swaps size is in contracts; most alts have ctVal=1 so contracts ≈ base.
    /// Binance/Bybit/Bitget shared path accepts base asset quantity.
    /// </summary>
    private static SharedQuantity QtyForExchange(string exchange, decimal baseQty)
    {
        if (exchange.Equals("OKX", StringComparison.OrdinalIgnoreCase))
            return SharedQuantity.Contracts(baseQty);
        return SharedQuantity.Base(baseQty);
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
    internal static decimal RoundBaseQty(decimal qty, decimal refPrice = 0)
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
