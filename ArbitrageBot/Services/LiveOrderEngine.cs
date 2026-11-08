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

public sealed record LiveLegOrderResult(
    bool Ok, string Error, string? Id, bool Accepted, bool Verified,
    decimal FilledBaseQty, decimal AveragePrice);

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
    private readonly IFuturesMarketService _market;
    private readonly IWebHostEnvironment _env;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _orderGate = new(1, 1);
    private readonly List<LiveHedgePosition> _open = [];
    private readonly List<LiveHedgePosition> _closed = [];
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>Exchange → UTC until which we skip live orders (balance / hard rejects).</summary>
    private readonly ConcurrentDictionary<string, DateTime> _venueCooldownUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _symbolCooldownUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, int> _adverseMarks = new();
    private readonly ConcurrentDictionary<Guid, decimal> _peakPnl = new();
    private readonly ConcurrentDictionary<Guid, int> _profitMarks = new();
    private static readonly TimeSpan BalanceCooldown = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan RejectCooldown = TimeSpan.FromSeconds(90);

    private sealed record OkxLot(decimal CtVal, decimal LotSz, decimal MinSz);
    private sealed record OrderExecution(
        bool Ok, string Error, string? Id, bool Accepted, bool Verified,
        decimal FilledBaseQty, decimal AveragePrice, string? Status = null);
    private static readonly ConcurrentDictionary<string, OkxLot> OkxLots = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _okxLotsAt = DateTime.MinValue;
    private static readonly SemaphoreSlim OkxLotLock = new(1, 1);
    private static readonly HttpClient OkxHttp = new() { Timeout = TimeSpan.FromSeconds(8) };

    private string LedgerPath => Path.Combine(_env.ContentRootPath, "data", "live", "trades-ledger.json");
    private ArbitrageOptions Opt => _runtime.Snapshot;

    public LiveOrderEngine(
        ISettingsStore settings,
        LiveTradingGuard guard,
        RuntimeRiskConfig runtime,
        IWebHostEnvironment env,
        LiveSafetyService safety,
        IFuturesMarketService market,
        ILogger<LiveOrderEngine> logger)
    {
        _settings = settings;
        _guard = guard;
        _runtime = runtime;
        _env = env;
        _safety = safety;
        _market = market;
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
        await _orderGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await TryOpenCoreAsync(req, ct).ConfigureAwait(false); }
        finally { _orderGate.Release(); }
    }

    private async Task<object> TryOpenCoreAsync(LiveHedgeRequest req, CancellationToken ct)
    {
        var check = _guard.CheckOpenAllowed(_open.Count + _guard.AdditionalOpenPositions, req.NotionalUsd);
        if (!check.ok)
            return new { ok = false, error = check.reason };

        var safety = _safety.CanOpenLive(req.Symbol, req.LongExchange, req.ShortExchange, req.NotionalUsd);
        if (!safety.ok)
            return new { ok = false, error = "safety: " + safety.reason };

        if (req.BaseQty <= 0 || string.IsNullOrWhiteSpace(req.Symbol))
            return new { ok = false, error = "invalid qty/symbol" };

        var liveBooks = _market.GetBookTickers(req.Symbol);
        if (!liveBooks.TryGetValue(req.LongExchange, out var longBook)
            || !liveBooks.TryGetValue(req.ShortExchange, out var shortBook)
            || longBook.BestAsk <= 0 || longBook.BestBid <= 0
            || shortBook.BestBid <= 0 || shortBook.BestAsk <= 0)
            return new { ok = false, error = "fresh live books are unavailable for both requested legs" };
        req.LongAsk = longBook.BestAsk;
        req.ShortBid = shortBook.BestBid;
        req.ExitCrossPercent = ArbEconomics.ExitCrossPercent(
            longBook.BestBid, longBook.BestAsk, shortBook.BestBid, shortBook.BestAsk);
        var minTouchUsd = req.NotionalUsd * 0.25m;
        if ((longBook.AskQuantity <= 0 || longBook.AskQuantity * longBook.BestAsk < minTouchUsd)
            || (shortBook.BidQuantity <= 0 || shortBook.BidQuantity * shortBook.BestBid < minTouchUsd))
            return new { ok = false, error = "top-of-book liquidity is below 25% of requested notional" };

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
        var lev = Math.Clamp(
            req.Leverage > 0 ? req.Leverage : (Opt.LivePaperLeverage > 0 ? Opt.LivePaperLeverage : 3m),
            1m, 10m);

        // The settings size (scan notional) is the order. Do not shrink it to equity × usage.
        // LiveMaxNotional is only a ceiling, and Save raises it to the size.
        var opt = Opt;
        var equity = opt.LiveEquityPerExchangeUsd > 0 ? opt.LiveEquityPerExchangeUsd : 100m;
        var usage  = opt.LiveMarginUsageFraction  > 0 ? opt.LiveMarginUsageFraction  : 0.25m;
        usage = Math.Clamp(usage, 0.15m, 0.50m);
        if (req.NotionalUsd <= 0)
            return new { ok = false, error = "notional must be positive" };
        var target = req.NotionalUsd;
        if (opt.LiveMaxNotionalUsd > 0 && target > opt.LiveMaxNotionalUsd)
            target = opt.LiveMaxNotionalUsd;
        if (target < 20m)
        {
            var why = $"live target {target:F2}$ below $20 — raise size";
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

        if (TradeBan.IsRouteBanned(req.Symbol, req.LongExchange, req.ShortExchange))
            return SkipSymbol(req.Symbol, "banned after stop, dead book, or structural basis", TimeSpan.FromMinutes(30));

        // Spread must still be there at send time. A vanished cross is not a hedge, it is two market bets.
        if (req.LongAsk is not > 0 || req.ShortBid is not > 0 || req.ShortBid <= req.LongAsk)
            return SkipSymbol(req.Symbol, "spread gone before send", TimeSpan.FromSeconds(20));

        var grossOpen = (req.ShortBid.Value - req.LongAsk.Value) * roundedQty;
        var grossPct = req.LongAsk.Value > 0
            ? (req.ShortBid.Value - req.LongAsk.Value) / req.LongAsk.Value * 100m
            : 0m;
        if (grossPct > ArbEconomics.MaxSaneGrossPercent)
            return SkipSymbol(req.Symbol, $"gross {grossPct:F2}% is a broken feed, not an arb", TimeSpan.FromMinutes(10));
        var feesOpen = EstimateRoundTripFees(req.LongExchange, req.ShortExchange, req.LongAsk.Value, req.ShortBid.Value, roundedQty);
        var reserveUsd = estNotional * ArbEconomics.ExecutionReservePercent(opt.OpenEdgeBufferPercent) / 100m;
        // Fair basis is not profit. Only the excess over it can revert.
        var basisUsd = estNotional * (req.FairBasisPercent > 0 ? req.FairBasisPercent : 0m) / 100m;
        var exitCrossUsd = estNotional * Math.Max(0m, req.ExitCrossPercent) / 100m;
        var netOpen = grossOpen - basisUsd - feesOpen - reserveUsd - exitCrossUsd;
        var minTp = ArbEconomics.RequiredTakeProfitUsd(opt.MinTakeProfitUsd, estNotional);
        var minEdgeUsd = estNotional * ArbEconomics.RequiredNetPercent(opt.MinProfitPercent) / 100m;
        var need = Math.Max(minTp, minEdgeUsd);
        if (netOpen < need)
        {
            _logger.LogInformation(
                "LIVE skip {Sym}: RT net {Net:F2}$ < min {Min:F2}$ (gross {G:F2} fees {F:F2} exit {X:F2} reserve {S:F2})",
                req.Symbol, netOpen, need, grossOpen, feesOpen, exitCrossUsd, reserveUsd);
            return SkipSymbol(req.Symbol, $"RT net {netOpen:F2}$ < min {need:F2}$ after fees, exit cross and reserve", TimeSpan.FromSeconds(45));
        }

        var estMargin = lev > 0 ? estNotional / lev : estNotional;
        var configuredMarginLimit = equity * usage;
        if (estMargin > configuredMarginLimit)
            return SkipSymbol(req.Symbol,
                $"estimated margin {estMargin:F2}$ exceeds configured per-venue budget {configuredMarginLimit:F2}$",
                TimeSpan.FromMinutes(2));
        // A venue we already know is empty must not be tried, and must not
        // freeze the symbol: Binance↔Bybit still trades if the other four are at 0.
        if (VenueTooPoor(req.LongExchange, estMargin, out var poorLong))
            return new { ok = false, error = poorLong };
        if (VenueTooPoor(req.ShortExchange, estMargin, out var poorShort))
            return new { ok = false, error = poorShort };

        _logger.LogInformation(
            "LIVE size {Sym} qty={Q} notional≈{N:F2}$ margin≈{M:F2}$ (equity={E}$ lev={L}x usage={U:P0} scanQty={Scan})",
            req.Symbol, roundedQty, estNotional, estMargin, equity, lev, usage, scanQty);

        // Reserve the global and per-venue interval before the first leg, including
        // rejected/partially filled attempts, so repeated errors cannot order-spam.
        var longQty = QtyForExchange(req.LongExchange, req.Symbol, roundedQty);
        var shortQty = QtyForExchange(req.ShortExchange, req.Symbol, roundedQty);
        var clientId = "ab-" + Guid.NewGuid().ToString("N")[..16];
        var pendingPosition = new LiveHedgePosition
        {
            Symbol = req.Symbol.ToUpperInvariant(),
            LongExchange = req.LongExchange,
            ShortExchange = req.ShortExchange,
            BaseQty = roundedQty,
            LongBaseQty = roundedQty,
            ShortBaseQty = roundedQty,
            HasPerLegQuantities = true,
            NotionalUsd = estNotional,
            LongEntry = req.LongAsk.Value,
            ShortEntry = req.ShortBid.Value,
            IsOpen = true,
            NeedsReconciliation = true,
            Status = "Opening",
            Message = "Opening intent persisted before first live order"
        };
        lock (_lock)
        {
            _open.Add(pendingPosition);
            SaveUnlocked();
        }
        var reserved = _safety.TryReserveOrderAttempt(req.Symbol, req.LongExchange, req.ShortExchange, estNotional);
        if (!reserved.ok)
        {
            RemoveOpeningIntent(pendingPosition);
            return new { ok = false, error = "safety: " + reserved.reason };
        }

        // 1) LONG — hedge first; if the account is one-way, SendOrderAsync retries without positionSide.
        var longResult = await SendOrderAsync(
            longClient, req.LongExchange, sharedSym, SharedOrderSide.Buy, longQty, lev,
            SharedPositionSide.Long, isClose: false, clientId + "-L", ct,
            limitPrice: SnapLimit(req.LongAsk.Value, req.LongAsk.Value * 1.0004m, up: true),
            expectedBaseQty: roundedQty).ConfigureAwait(false);
        if (!longResult.Ok)
        {
            if (longResult.Accepted && !longResult.Verified)
            {
                QuarantineUnknownOrder(req, roundedQty, "long", longResult.Error);
                return new { ok = false, error = "long order accepted but fill could not be verified; live killed for reconciliation" };
            }
            if (longResult.Verified && longResult.FilledBaseQty > 0)
            {
                var unwind = await TryReduceAsync(longClient, req.LongExchange, sharedSym,
                    QtyForExchange(req.LongExchange, req.Symbol, longResult.FilledBaseQty),
                    SharedOrderSide.Sell, SharedPositionSide.Long, ct, longResult.FilledBaseQty).ConfigureAwait(false);
                if (!unwind.Ok)
                {
                    QuarantineUnknownOrder(req, longResult.FilledBaseQty, "long partial fill unwind", unwind.Error);
                    return new { ok = false, error = "partial long fill could not be safely unwound" };
                }
            }
            RemoveOpeningIntent(pendingPosition);
            if (IsBalanceError(longResult.Error))
            {
                NoteVenueUsdt(req.LongExchange, 0);
                MarkCooldown(req.LongExchange, longResult.Error);
                _logger.LogError("LIVE LONG fail {Ex} {Sym}: {Err}",
                    req.LongExchange, req.Symbol, longResult.Error);
                return new { ok = false, error = "no margin on " + req.LongExchange };
            }
            _logger.LogError("LIVE LONG fail {Ex} {Sym}: {Err}",
                req.LongExchange, req.Symbol, longResult.Error);
            return SkipSymbol(req.Symbol, "LONG failed: " + longResult.Error, RejectCooldown);
        }

        var longOrderId = longResult.Id ?? "?";
        var longAvg = longResult.AveragePrice > 0 ? longResult.AveragePrice : req.LongAsk ?? 0;
        pendingPosition.LongBaseQty = longResult.FilledBaseQty;
        pendingPosition.ShortBaseQty = 0m;
        pendingPosition.BaseQty = longResult.FilledBaseQty;
        pendingPosition.LongEntry = longAvg;
        pendingPosition.LongOrderId = longOrderId;
        pendingPosition.Status = "OpeningShort";
        pendingPosition.Message = "Long fill confirmed; short leg is being submitted";
        lock (_lock) SaveUnlocked();
        if (longResult.AveragePrice <= 0)
        {
            var unwind = await TryReduceAsync(longClient, req.LongExchange, sharedSym,
                QtyForExchange(req.LongExchange, req.Symbol, longResult.FilledBaseQty),
                SharedOrderSide.Sell, SharedPositionSide.Long, ct, longResult.FilledBaseQty).ConfigureAwait(false);
            if (!unwind.Ok) QuarantineUnknownOrder(req, longResult.FilledBaseQty, "long fill price missing; unwind failed", unwind.Error);
            else RemoveOpeningIntent(pendingPosition);
            return new { ok = false, error = "long fill confirmed but average price missing; hedge unwound or reconciliation required" };
        }

        // 2) SHORT — same size; on fail always unwind long
        var shortResult = await SendOrderAsync(
            shortClient, req.ShortExchange, sharedSym, SharedOrderSide.Sell, shortQty, lev,
            SharedPositionSide.Short, isClose: false, clientId + "-S", ct,
            limitPrice: SnapLimit(req.ShortBid.Value, req.ShortBid.Value * 0.9996m, up: false),
            expectedBaseQty: roundedQty).ConfigureAwait(false);
        if (shortResult.Verified && shortResult.FilledBaseQty > 0)
        {
            pendingPosition.ShortBaseQty = shortResult.FilledBaseQty;
            pendingPosition.ShortOrderId = shortResult.Id;
            pendingPosition.ShortEntry = shortResult.AveragePrice;
            pendingPosition.BaseQty = Math.Max(pendingPosition.LongBaseQty, pendingPosition.ShortBaseQty);
            pendingPosition.Status = "OpeningReconcile";
            pendingPosition.Message = "Both leg fill state is being reconciled";
            lock (_lock) SaveUnlocked();
        }
        if (!shortResult.Ok)
        {
            if (IsBalanceError(shortResult.Error))
            {
                NoteVenueUsdt(req.ShortExchange, 0);
                MarkCooldown(req.ShortExchange, shortResult.Error);
            }
            _logger.LogError("LIVE SHORT fail {Ex} {Sym}: {Err} — unwinding long",
                req.ShortExchange, req.Symbol, shortResult.Error);
            if (shortResult.Accepted && !shortResult.Verified)
            {
                QuarantineUnknownOrder(req, roundedQty, "short", shortResult.Error);
                return new { ok = false, error = "short order accepted but fill could not be verified; live killed for reconciliation" };
            }
            if (shortResult.Verified && shortResult.FilledBaseQty > 0)
            {
                var closeShort = await TryReduceAsync(shortClient, req.ShortExchange, sharedSym,
                    QtyForExchange(req.ShortExchange, req.Symbol, shortResult.FilledBaseQty),
                    SharedOrderSide.Buy, SharedPositionSide.Short, ct, shortResult.FilledBaseQty).ConfigureAwait(false);
                if (!closeShort.Ok)
                {
                    QuarantineUnknownOrder(req, shortResult.FilledBaseQty, "short partial fill unwind", closeShort.Error);
                    return new { ok = false, error = "partial short fill could not be safely unwound" };
                }
            }
            var unwindLong = await TryReduceAsync(longClient, req.LongExchange, sharedSym,
                QtyForExchange(req.LongExchange, req.Symbol, longResult.FilledBaseQty),
                SharedOrderSide.Sell, SharedPositionSide.Long, ct, longResult.FilledBaseQty).ConfigureAwait(false);
            if (!unwindLong.Ok)
            {
                QuarantineUnknownOrder(req, longResult.FilledBaseQty, "long unwind", unwindLong.Error);
                return new { ok = false, error = "long fill could not be safely unwound" };
            }
            RemoveOpeningIntent(pendingPosition);
            if (IsBalanceError(shortResult.Error))
                return new { ok = false, error = "no margin on " + req.ShortExchange + " (long unwind attempted)" };
            return SkipSymbol(req.Symbol, "SHORT failed: " + shortResult.Error + " (long unwind attempted)", RejectCooldown);
        }

        var shortOrderId = shortResult.Id ?? "?";
        var shortAvg = shortResult.AveragePrice > 0 ? shortResult.AveragePrice : req.ShortBid ?? 0;
        if (shortResult.AveragePrice <= 0)
        {
            var unwindShort = await TryReduceAsync(shortClient, req.ShortExchange, sharedSym,
                QtyForExchange(req.ShortExchange, req.Symbol, shortResult.FilledBaseQty),
                SharedOrderSide.Buy, SharedPositionSide.Short, ct, shortResult.FilledBaseQty).ConfigureAwait(false);
            var unwindLong = await TryReduceAsync(longClient, req.LongExchange, sharedSym,
                QtyForExchange(req.LongExchange, req.Symbol, longResult.FilledBaseQty),
                SharedOrderSide.Sell, SharedPositionSide.Long, ct, longResult.FilledBaseQty).ConfigureAwait(false);
            if (!unwindShort.Ok || !unwindLong.Ok)
                QuarantineUnknownOrder(req, Math.Max(longResult.FilledBaseQty, shortResult.FilledBaseQty),
                    "average fill price missing; unwind failed", $"long={unwindLong.Error}; short={unwindShort.Error}");
            else RemoveOpeningIntent(pendingPosition);
            return new { ok = false, error = "short fill confirmed but average price missing; hedge unwound or reconciliation required" };
        }

        var matchedQty = Math.Min(longResult.FilledBaseQty, shortResult.FilledBaseQty);
        var unmatchedLong = Math.Max(0m, longResult.FilledBaseQty - matchedQty);
        var unmatchedShort = Math.Max(0m, shortResult.FilledBaseQty - matchedQty);
        if (unmatchedLong > 0)
        {
            var result = await TryReduceAsync(longClient, req.LongExchange, sharedSym,
                QtyForExchange(req.LongExchange, req.Symbol, unmatchedLong), SharedOrderSide.Sell,
                SharedPositionSide.Long, ct, unmatchedLong).ConfigureAwait(false);
            if (!result.Ok)
            {
                QuarantineUnknownOrder(req, unmatchedLong, "long unmatched fill", result.Error);
                return new { ok = false, error = "unmatched long fill could not be safely unwound" };
            }
        }
        if (unmatchedShort > 0)
        {
            var result = await TryReduceAsync(shortClient, req.ShortExchange, sharedSym,
                QtyForExchange(req.ShortExchange, req.Symbol, unmatchedShort), SharedOrderSide.Buy,
                SharedPositionSide.Short, ct, unmatchedShort).ConfigureAwait(false);
            if (!result.Ok)
            {
                QuarantineUnknownOrder(req, unmatchedShort, "short unmatched fill", result.Error);
                return new { ok = false, error = "unmatched short fill could not be safely unwound" };
            }
        }
        if (matchedQty <= 0)
        {
            RemoveOpeningIntent(pendingPosition);
            return new { ok = false, error = "no matched filled quantity; any unmatched quantity was unwound" };
        }

        var pos = pendingPosition;
        pos.BaseQty = matchedQty;
        pos.LongBaseQty = matchedQty;
        pos.ShortBaseQty = matchedQty;
        pos.HasPerLegQuantities = true;
        pos.NotionalUsd = estNotional;
        pos.LongEntry = longAvg;
        pos.ShortEntry = shortAvg;
        pos.LongOrderId = longOrderId?.ToString();
        pos.ShortOrderId = shortOrderId?.ToString();
        pos.NeedsReconciliation = false;
        pos.Status = "Open";
        pos.Message = $"LIVE hedge filled L@{longAvg} S@{shortAvg}";
        lock (_lock)
        {
            SaveUnlocked();
        }

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
        await _orderGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await TryCloseCoreAsync(tradeId, getMarks, ct).ConfigureAwait(false); }
        finally { _orderGate.Release(); }
    }

    private async Task<object> TryCloseCoreAsync(string tradeId, Func<string, string, string, (decimal longBid, decimal shortAsk)?>? getMarks, CancellationToken ct)
    {
        LiveHedgePosition? pos;
        lock (_lock)
            pos = _open.FirstOrDefault(p => p.Id.ToString().Equals(tradeId, StringComparison.OrdinalIgnoreCase));

        if (pos == null)
            return new { ok = false, error = "trade not found" };

        var baseAsset = pos.Symbol.EndsWith("USDT") ? pos.Symbol[..^4] : pos.Symbol;
        var sharedSym = new SharedSymbol(TradingMode.PerpetualLinear, baseAsset, "USDT");
        if (IsOkx(pos.LongExchange) || IsOkx(pos.ShortExchange))
            await EnsureOkxLotsAsync(ct).ConfigureAwait(false);
        var longRemaining = pos.HasPerLegQuantities ? pos.LongBaseQty : pos.BaseQty;
        var shortRemaining = pos.HasPerLegQuantities ? pos.ShortBaseQty : pos.BaseQty;
        var longQty = QtyForExchange(pos.LongExchange, pos.Symbol, longRemaining);
        var shortQty = QtyForExchange(pos.ShortExchange, pos.Symbol, shortRemaining);

        var longClient = CreateClient(pos.LongExchange);
        var shortClient = CreateClient(pos.ShortExchange);
        if (longClient == null || shortClient == null)
            return new { ok = false, error = "missing credentials to close" };

        // Close long: sell reduce
        var closeLong = longRemaining > 0
            ? await TryReduceAsync(longClient, pos.LongExchange, sharedSym, longQty, SharedOrderSide.Sell, SharedPositionSide.Long, ct, longRemaining).ConfigureAwait(false)
            : new OrderExecution(true, "already flat", null, false, true, 0m, 0m, "Filled");
        // Close short: buy reduce
        var closeShort = shortRemaining > 0
            ? await TryReduceAsync(shortClient, pos.ShortExchange, sharedSym, shortQty, SharedOrderSide.Buy, SharedPositionSide.Short, ct, shortRemaining).ConfigureAwait(false)
            : new OrderExecution(true, "already flat", null, false, true, 0m, 0m, "Filled");

        if (closeLong.Accepted && !closeLong.Verified || closeShort.Accepted && !closeShort.Verified)
        {
            pos.NeedsReconciliation = true;
            pos.Status = "ReconciliationRequired";
            pos.Message = "Close order accepted but exchange fill could not be verified; reconcile positions before retrying";
            _guard.Kill(pos.Message);
            _ = _safety.AlertAsync("LIVE RECONCILIATION REQUIRED", $"{pos.Symbol}: {pos.Message}", CancellationToken.None);
            lock (_lock) SaveUnlocked();
            return new { ok = false, tradeId, status = pos.Status, message = pos.Message };
        }

        if ((closeLong.FilledBaseQty > 0 && closeLong.AveragePrice <= 0)
            || (closeShort.FilledBaseQty > 0 && closeShort.AveragePrice <= 0))
        {
            pos.NeedsReconciliation = true;
            pos.Status = "ReconciliationRequired";
            pos.Message = "Close fill quantity is known but average execution price is missing";
            _guard.Kill(pos.Message);
            _ = _safety.AlertAsync("LIVE RECONCILIATION REQUIRED", $"{pos.Symbol}: {pos.Message}", CancellationToken.None);
            lock (_lock) SaveUnlocked();
            return new { ok = false, tradeId, status = pos.Status, message = pos.Message };
        }

        var realizedDelta = 0m;
        if (closeLong.FilledBaseQty > 0)
            realizedDelta += ClosedLegNetPnl(true, pos.LongExchange, pos.LongEntry, closeLong.AveragePrice, closeLong.FilledBaseQty);
        if (closeShort.FilledBaseQty > 0)
            realizedDelta += ClosedLegNetPnl(false, pos.ShortExchange, pos.ShortEntry, closeShort.AveragePrice, closeShort.FilledBaseQty);
        pos.RealizedPnlUsd = (pos.RealizedPnlUsd ?? 0m) + realizedDelta;

        if (closeLong.Verified && closeLong.FilledBaseQty > 0)
            pos.LongBaseQty = Math.Max(0m, longRemaining - closeLong.FilledBaseQty);
        else
            pos.LongBaseQty = longRemaining;
        if (closeShort.Verified && closeShort.FilledBaseQty > 0)
            pos.ShortBaseQty = Math.Max(0m, shortRemaining - closeShort.FilledBaseQty);
        else
            pos.ShortBaseQty = shortRemaining;
        pos.HasPerLegQuantities = true;
        pos.BaseQty = Math.Max(pos.LongBaseQty, pos.ShortBaseQty);
        var flat = pos.LongBaseQty <= Math.Max(0.0000000001m, longRemaining * 0.000001m)
                   && pos.ShortBaseQty <= Math.Max(0.0000000001m, shortRemaining * 0.000001m);

        decimal? pnl = flat ? pos.RealizedPnlUsd : null;
        var feeNote = " based on confirmed fills and configured taker fees";

        lock (_lock)
        {
            if (flat) _open.Remove(pos);
            pos.IsOpen = !flat;
            pos.ClosedAt = flat ? DateTime.UtcNow : null;
            pos.Status = flat ? "Closed" : "ClosePartial";
            pos.RealizedPnlUsd = pnl;
            pos.Message = $"close longFilled={closeLong.FilledBaseQty} shortFilled={closeShort.FilledBaseQty} remainingL={pos.LongBaseQty} remainingS={pos.ShortBaseQty} pnl={pnl}{feeNote}";
            if (flat)
            {
                _closed.Insert(0, pos);
                if (_closed.Count > 500) _closed.RemoveRange(500, _closed.Count - 500);
            }
            SaveUnlocked();
        }

        if (flat && pnl.HasValue)
            _guard.RecordRealized(pnl.Value);

        return new { ok = flat, tradeId, pnl, status = pos.Status, message = pos.Message };
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
            if (pos.NeedsReconciliation) continue;
            var marks = getMarks(pos.Symbol, pos.LongExchange, pos.ShortExchange);
            if (marks == null) continue;
            var (longBid, shortAsk) = marks.Value;
            if (longBid <= 0 || shortAsk <= 0) continue;

            var width = (shortAsk - longBid) / longBid * 100m;
            var entryWidth = pos.LongEntry > 0 ? (pos.ShortEntry - pos.LongEntry) / pos.LongEntry * 100m : 0m;
            var longQty = pos.HasPerLegQuantities ? pos.LongBaseQty : pos.BaseQty;
            var shortQty = pos.HasPerLegQuantities ? pos.ShortBaseQty : pos.BaseQty;
            var gross = (pos.ShortEntry - shortAsk) * shortQty + (longBid - pos.LongEntry) * longQty;
            var fees = RoundTripFees(pos, longBid, shortAsk);
            var net = (pos.RealizedPnlUsd ?? 0m) + gross - fees;
            var notional = Math.Max(pos.LongEntry * longQty, pos.ShortEntry * shortQty);
            var minTp = ArbEconomics.RequiredTakeProfitUsd(LiveMinNetUsd(notional), notional);
            var stopLevel = ArbEconomics.EffectiveStopUsd(stopLossUsd, fees, minTp);
            var widened = entryWidth > 0 && width >= entryWidth + 0.30m;
            var disaster = net <= -Math.Max(8m, notional * 0.035m);
            var adverse = net <= stopLevel && (widened || disaster)
                ? _adverseMarks.AddOrUpdate(pos.Id, 1, (_, n) => n + 1)
                : ResetAdverse(pos.Id);
            var stop = (disaster && adverse >= 2) || adverse >= 6;
            var peak = net > 0m
                ? _peakPnl.AddOrUpdate(pos.Id, net, (_, p) => net > p ? net : p)
                : _peakPnl.GetOrAdd(pos.Id, 0m);
            var profitMarks = net >= minTp
                ? _profitMarks.AddOrUpdate(pos.Id, 1, (_, n) => n + 1)
                : ResetProfit(pos.Id);
            var converged = width <= closeBelow;
            var shrunk = entryWidth > 0 && width <= entryWidth * 0.40m;
            var locked = peak >= minTp;
            var trailFloor = Math.Max(0.05m, peak * 0.45m);
            var trailHit = locked && net > 0m && net <= trailFloor && profitMarks == 0;
            var takeProfit = profitMarks >= 3 || (net >= minTp && (converged || shrunk)) || trailHit;
            var timedOut = maxHoldMinutes > 0 && (DateTime.UtcNow - pos.OpenedAt).TotalMinutes >= maxHoldMinutes;
            var timeoutHarvest = timedOut && net >= minTp;
            var hardTimeoutMinutes = Opt.FuturesHardMaxHoldMinutes;
            var hardTimeout = hardTimeoutMinutes > 0
                && (DateTime.UtcNow - pos.OpenedAt).TotalMinutes >= hardTimeoutMinutes;

            if (!stop && !takeProfit && !timeoutHarvest && !hardTimeout) continue;
            if (stop) TradeBan.Ban(pos.Symbol, TimeSpan.FromHours(6));
            _adverseMarks.TryRemove(pos.Id, out _);
            _peakPnl.TryRemove(pos.Id, out _);
            _profitMarks.TryRemove(pos.Id, out _);

            var r = await TryCloseAsync(pos.Id.ToString(), getMarks, ct).ConfigureAwait(false);
            if (r.GetType().GetProperty("ok")?.GetValue(r) is true) closed++;
            _logger.LogWarning(
                "LIVE auto-close {Sym} width={W:F3} net={N:F2} gross={G:F2} fees={F:F2} min={M:F2} stop={S} tp={T} timeout={H} hardTimeout={Hard}",
                pos.Symbol, width, net, gross, fees, minTp, stop, takeProfit, timeoutHarvest, hardTimeout);
        }
        return closed;
    }

    private int ResetAdverse(Guid id)
    {
        _adverseMarks.TryRemove(id, out _);
        return 0;
    }

    private int ResetProfit(Guid id)
    {
        _profitMarks.TryRemove(id, out _);
        return 0;
    }

    private async Task<OrderExecution> TryReduceAsync(
        ExchangeRestClient client,
        string exchange,
        SharedSymbol symbol,
        SharedQuantity qty,
        SharedOrderSide side,
        SharedPositionSide posSide,
        CancellationToken ct,
        decimal expectedBaseQty)
    {
        try
        {
            // Hedge mode (positionSide Long/Short): do NOT send reduceOnly.
            // Binance/Bitget reject reduceOnly in dual-side/hedge mode (-1106 / param errors).
            // Closing is expressed by opposite order side + same positionSide (+ Bitget tradeSide=close).
            var sent = await SendOrderAsync(
                client, exchange, symbol, side, qty, leverage: null, posSide, isClose: true,
                "ab-c-" + Guid.NewGuid().ToString("N")[..12], ct,
                expectedBaseQty: expectedBaseQty).ConfigureAwait(false);
            if (!sent.Ok)
            {
                _logger.LogError("LIVE reduce fail {Ex}: {Err}", exchange, sent.Error);
                return sent;
            }
            return sent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LIVE reduce exception {Ex}", exchange);
            return new OrderExecution(false, ex.Message, null, false, false, 0m, 0m);
        }
    }

    private ExchangeRestClient? CreateClient(string exchange)
    {
        var cred = _settings.GetCredential(exchange);
        if (cred == null || !cred.Enabled
            || !string.Equals(cred.Permission, "trade", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(cred.ApiKey) || string.IsNullOrWhiteSpace(cred.ApiSecret))
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

    private async Task<OrderExecution> SendOrderAsync(
        ExchangeRestClient client,
        string exchange,
        SharedSymbol symbol,
        SharedOrderSide side,
        SharedQuantity qty,
        decimal? leverage,
        SharedPositionSide? hedgeSide,
        bool isClose,
        string clientOrderId,
        CancellationToken ct,
        decimal? limitPrice = null,
        decimal? expectedBaseQty = null)
    {
        async Task<OrderExecution> Once(bool hedge)
        {
            var limit = limitPrice is > 0;
            var req = new PlaceFuturesOrderRequest(
                symbol,
                side,
                limit ? SharedOrderType.Limit : SharedOrderType.Market,
                qty,
                price: limit ? limitPrice : null,
                reduceOnly: null,
                leverage: leverage,
                timeInForce: limit ? SharedTimeInForce.ImmediateOrCancel : null,
                positionSide: hedge ? hedgeSide : null,
                marginMode: SharedMarginMode.Cross,
                clientOrderId: hedge ? clientOrderId : clientOrderId + "n",
                exchangeParameters: ExParams(exchange, isClose, hedge));
            CryptoExchange.Net.Objects.HttpResult<SharedId> result;
            try
            {
                result = await client.PlaceFuturesOrderAsync(exchange, req, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
            {
                return new OrderExecution(false, "order request canceled after submission may have started: " + ex.Message,
                    null, true, false, 0m, 0m);
            }
            catch (Exception ex)
            {
                var ambiguous = IsAmbiguousOrderFailure(ex.Message);
                return new OrderExecution(false, ex.Message, null, ambiguous, false, 0m, 0m);
            }
            if (!result.Success)
            {
                var error = DescribeFail(result.Error, result.OriginalData, "order failed");
                return new OrderExecution(false, error, null, IsAmbiguousOrderFailure(error), false, 0m, 0m);
            }

            var id = result.Data?.Id?.ToString();
            if (string.IsNullOrWhiteSpace(id))
                return new OrderExecution(false, "order accepted without exchange order id; fill state is unknown", null, true, false, 0m, 0m);

            var expected = expectedBaseQty ?? 0m;
            var actualClientId = hedge ? clientOrderId : clientOrderId + "n";
            (bool Verified, decimal FilledBaseQty, decimal AveragePrice, string? Status) confirmed;
            try
            {
                confirmed = await ConfirmOrderFillAsync(client, exchange, symbol, id, actualClientId, expected, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new OrderExecution(false, "order accepted; fill confirmation interrupted: " + ex.Message,
                    id, true, false, 0m, 0m);
            }
            if (!confirmed.Verified)
                return new OrderExecution(false, "order accepted but fill state could not be confirmed", id, true, false, 0m, 0m);

            var fullyFilled = expected <= 0 || confirmed.FilledBaseQty >= expected * 0.999999m;
            return new OrderExecution(
                fullyFilled, fullyFilled ? "" : $"partial fill {confirmed.FilledBaseQty}/{expected} base ({confirmed.Status})",
                id, true, true, confirmed.FilledBaseQty, confirmed.AveragePrice, confirmed.Status);
        }

        var first = await Once(hedgeSide != null);
        if (first.Ok || hedgeSide == null || !IsNetModeError(first.Error))
            return first;
        _logger.LogWarning("LIVE {Ex} retry without position side ({Err})", exchange, first.Error);
        return await Once(false);
    }

    private static bool IsAmbiguousOrderFailure(string message)
        => message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
           || message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
           || message.Contains("connection", StringComparison.OrdinalIgnoreCase)
           || message.Contains("socket", StringComparison.OrdinalIgnoreCase)
           || message.Contains("network", StringComparison.OrdinalIgnoreCase)
           || message.Contains("canceled", StringComparison.OrdinalIgnoreCase)
           || message.Contains("cancelled", StringComparison.OrdinalIgnoreCase);

    private async Task<(bool Verified, decimal FilledBaseQty, decimal AveragePrice, string? Status)> ConfirmOrderFillAsync(
        ExchangeRestClient client, string exchange, SharedSymbol symbol, string orderId,
        string clientOrderId, decimal expectedBaseQty, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var request = new GetOrderRequest(symbol, orderId, ExParams(exchange));
                var result = await client.GetFuturesOrderAsync(exchange, request, ct).ConfigureAwait(false);
                if (result.Success && result.Data != null)
                {
                    var order = result.Data;
                    var filledBase = order.QuantityFilled?.QuantityInBaseAsset
                        ?? ToBaseQuantity(exchange, symbol.BaseAsset, order.QuantityFilled?.QuantityInContracts ?? 0m);
                    var status = order.Status.ToString();
                    var terminal = status.Equals("Filled", StringComparison.OrdinalIgnoreCase)
                                   || status.Equals("Canceled", StringComparison.OrdinalIgnoreCase);
                    if (terminal)
                        return (true, filledBase, order.AveragePrice ?? 0m, status);
                }

                // Some venues expose the order by client id a little earlier than by exchange id.
                var byClient = new GetOrderRequest(symbol, clientOrderId, ExParams(exchange));
                var retry = await client.GetFuturesOrderByClientOrderIdAsync(exchange, byClient, ct).ConfigureAwait(false);
                if (retry.Success && retry.Data != null)
                {
                    var order = retry.Data;
                    var filledBase = order.QuantityFilled?.QuantityInBaseAsset
                        ?? ToBaseQuantity(exchange, symbol.BaseAsset, order.QuantityFilled?.QuantityInContracts ?? 0m);
                    var status = order.Status.ToString();
                    var terminal = status.Equals("Filled", StringComparison.OrdinalIgnoreCase)
                                   || status.Equals("Canceled", StringComparison.OrdinalIgnoreCase);
                    if (terminal)
                        return (true, filledBase, order.AveragePrice ?? 0m, status);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LIVE order fill query {Ex} {OrderId} attempt {Attempt}", exchange, orderId, attempt + 1);
            }

            if (attempt < 2)
                await Task.Delay(150 * (attempt + 1), ct).ConfigureAwait(false);
        }

        return (false, 0m, 0m, null);
    }

    private decimal ToBaseQuantity(string exchange, string baseAsset, decimal filledContracts)
    {
        if (filledContracts <= 0) return 0m;
        if (IsOkx(exchange) && OkxLots.TryGetValue(NormSym(baseAsset + "USDT"), out var lot) && lot.CtVal > 0)
            return filledContracts * lot.CtVal;
        return filledContracts;
    }

    private decimal ClosedLegNetPnl(bool isLong, string exchange, decimal entry, decimal exit, decimal quantity)
    {
        if (quantity <= 0 || entry <= 0 || exit <= 0) return 0m;
        var gross = isLong ? (exit - entry) * quantity : (entry - exit) * quantity;
        var fees = (entry + exit) * quantity * TakerRate(exchange);
        return gross - fees;
    }

    private void QuarantineUnknownOrder(LiveHedgeRequest req, decimal qty, string leg, string detail)
    {
        var reason = $"{req.Symbol} {leg}: {detail}";
        _guard.Kill(reason);
        lock (_lock)
        {
            var pos = _open.FirstOrDefault(p => p.Symbol.Equals(req.Symbol, StringComparison.OrdinalIgnoreCase));
            if (pos == null)
            {
                pos = new LiveHedgePosition
                {
                    Symbol = req.Symbol.ToUpperInvariant(),
                    LongExchange = req.LongExchange,
                    ShortExchange = req.ShortExchange,
                    BaseQty = qty,
                    LongBaseQty = qty,
                    ShortBaseQty = qty,
                    HasPerLegQuantities = true,
                    NotionalUsd = req.NotionalUsd,
                    LongEntry = req.LongAsk ?? 0m,
                    ShortEntry = req.ShortBid ?? 0m,
                    IsOpen = true
                };
                _open.Add(pos);
            }
            else if (!pos.HasPerLegQuantities)
            {
                pos.LongBaseQty = pos.BaseQty;
                pos.ShortBaseQty = pos.BaseQty;
                pos.HasPerLegQuantities = true;
            }

            if (leg.StartsWith("long", StringComparison.OrdinalIgnoreCase))
                pos.LongBaseQty = Math.Max(pos.LongBaseQty, qty);
            else if (leg.StartsWith("short", StringComparison.OrdinalIgnoreCase))
                pos.ShortBaseQty = Math.Max(pos.ShortBaseQty, qty);
            else
            {
                pos.LongBaseQty = Math.Max(pos.LongBaseQty, qty);
                pos.ShortBaseQty = Math.Max(pos.ShortBaseQty, qty);
            }
            pos.BaseQty = Math.Max(pos.LongBaseQty, pos.ShortBaseQty);
            pos.NeedsReconciliation = true;
            pos.Status = "ReconciliationRequired";
            pos.Message = reason;
            SaveUnlocked();
        }
        _ = _safety.AlertAsync("LIVE RECONCILIATION REQUIRED", reason, CancellationToken.None);
        _logger.LogCritical("LIVE order state unknown: {Reason}", reason);
    }

    private void RemoveOpeningIntent(LiveHedgePosition position)
    {
        lock (_lock)
        {
            _open.Remove(position);
            SaveUnlocked();
        }
    }

    private static bool IsNetModeError(string err)
    {
        if (string.IsNullOrEmpty(err)) return false;
        return err.Contains("posSide", StringComparison.OrdinalIgnoreCase)
            || err.Contains("positionSide", StringComparison.OrdinalIgnoreCase)
            || err.Contains("position side", StringComparison.OrdinalIgnoreCase)
            || err.Contains("position mode", StringComparison.OrdinalIgnoreCase)
            || err.Contains("positionIdx", StringComparison.OrdinalIgnoreCase)
            || err.Contains("tradeSide", StringComparison.OrdinalIgnoreCase)
            || err.Contains("one-way", StringComparison.OrdinalIgnoreCase)
            || err.Contains("oneway", StringComparison.OrdinalIgnoreCase)
            || err.Contains("dual", StringComparison.OrdinalIgnoreCase)
            || err.Contains("hedge", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Exchange-specific parameters required by CryptoClients shared futures order API.
    /// Bitget requires ProductType + MarginAsset/marginCoin; hedge mode also needs tradeSide.
    /// </summary>
    private static ExchangeParameters ExParams(string exchange, bool isClose = false, bool hedge = true)
    {
        if (exchange.Equals("Bitget", StringComparison.OrdinalIgnoreCase))
        {
            if (!hedge)
            {
                return new ExchangeParameters(
                    new ExchangeParameter("Bitget", "ProductType", "UsdtFutures"),
                    new ExchangeParameter("Bitget", "MarginAsset", "USDT"),
                    new ExchangeParameter("Bitget", "marginCoin", "USDT"));
            }
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
        var longQty = pos.HasPerLegQuantities ? pos.LongBaseQty : pos.BaseQty;
        var shortQty = pos.HasPerLegQuantities ? pos.ShortBaseQty : pos.BaseQty;
        var open = pos.LongEntry * longQty * TakerRate(pos.LongExchange) + pos.ShortEntry * shortQty * TakerRate(pos.ShortExchange);
        var close = longBid * longQty * TakerRate(pos.LongExchange) + shortAsk * shortQty * TakerRate(pos.ShortExchange);
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
        var floor = Opt.MinTakeProfitUsd > 0 ? Opt.MinTakeProfitUsd : 1.50m;
        var scaled = notional > 0 ? notional * 0.006m : 1.50m;
        return Math.Max(floor, Math.Max(scaled, 1.00m));
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

    /// <summary>Last known free USDT per venue. Missing key means "not checked yet", not "empty".</summary>
    private static readonly ConcurrentDictionary<string, decimal> VenueFreeUsdt = new(StringComparer.OrdinalIgnoreCase);

    public static void NoteVenueUsdt(string exchange, decimal available)
    {
        if (string.IsNullOrWhiteSpace(exchange)) return;
        VenueFreeUsdt[exchange.Trim()] = available;
    }

    private static bool VenueTooPoor(string exchange, decimal needUsd, out string why)
    {
        why = "";
        if (!VenueFreeUsdt.TryGetValue(exchange, out var free)) return false;
        if (free + 0.5m >= needUsd) return false;
        why = $"no margin on {exchange} (free {free:F2}$, need {needUsd:F2}$)";
        return true;
    }

    /// <summary>
    /// Put a bumped limit back on the touch's own tick.
    /// ask * 1.0004 on AKE (0.002166) becomes 0.0021668664 and Binance rejects it with -1111.
    /// </summary>
    public static decimal SnapLimit(decimal touch, decimal bumped, bool up)
    {
        if (bumped <= 0) return 0;
        var scale = PriceScale(touch > 0 ? touch : bumped);
        var factor = 1m;
        for (var i = 0; i < scale; i++) factor *= 10m;
        var ticks = bumped * factor;
        var snapped = up ? Math.Ceiling(ticks - 0.0000001m) : Math.Floor(ticks + 0.0000001m);
        if (snapped < 1) snapped = 1;
        return snapped / factor;
    }

    private static int PriceScale(decimal price)
    {
        if (price <= 0) return 2;
        var text = price.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture);
        var dot = text.IndexOf('.');
        var scale = dot < 0 ? 0 : text.Length - dot - 1;
        if (scale < 1)
        {
            if (price >= 1000m) scale = 1;
            else if (price >= 1m) scale = 2;
            else scale = 4;
        }
        if (scale > 8) scale = 8;
        return scale;
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

    /// <summary>
    /// One directional leg for the scalp mode. Limit IOC, one tick through the touch.
    /// Does not touch the hedge ledger.
    /// </summary>
    public async Task<LiveLegOrderResult> PlaceScalpLegAsync(
        string exchange, string symbol, bool isLong, decimal qty, decimal limitPrice,
        decimal leverage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(exchange) || string.IsNullOrWhiteSpace(symbol) || qty <= 0 || limitPrice <= 0)
            return new(false, "bad scalp request", null, false, false, 0m, 0m);
        if (!_guard.CanPlaceOrders)
            return new(false, "live orders are disabled or killed", null, false, false, 0m, 0m);
        if (IsCoolingDown(exchange, out var cd))
            return new(false, $"cooldown {exchange} {cd.TotalSeconds:F0}s", null, false, false, 0m, 0m);
        if (IsSymbolCooling(symbol, out var symCd))
            return new(false, $"cooldown {symbol} {symCd.TotalSeconds:F0}s", null, false, false, 0m, 0m);

        var client = CreateClient(exchange);
        if (client == null)
            return new(false, "no trade credentials for " + exchange, null, false, false, 0m, 0m);

        var baseAsset = symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
            ? symbol[..^4] : symbol;
        var shared = new SharedSymbol(TradingMode.PerpetualLinear, baseAsset, "USDT");
        var rounded = RoundBaseQty(qty, limitPrice);
        if (rounded <= 0)
            return new(false, "qty rounded to zero", null, false, false, 0m, 0m);

        if (IsOkx(exchange))
        {
            await EnsureOkxLotsAsync(ct).ConfigureAwait(false);
            var aligned = AlignOkxBase(symbol, rounded, out var lotWhy);
            if (aligned <= 0)
                return new(false, lotWhy ?? "OKX lot reject", null, false, false, 0m, 0m);
            rounded = aligned;
        }

        var side = isLong ? SharedOrderSide.Buy : SharedOrderSide.Sell;
        var posSide = isLong ? SharedPositionSide.Long : SharedPositionSide.Short;
        var snapped = SnapLimit(limitPrice, limitPrice, isLong);
        var notional = rounded * snapped;
        var gate = _guard.CheckOpenAllowed(_open.Count + Math.Max(0, _guard.AdditionalOpenPositions - 1), notional);
        if (!gate.ok) return new(false, gate.reason, null, false, false, 0m, 0m);
        var safety = _safety.TryReserveOrderAttempt(symbol, exchange, exchange, notional);
        if (!safety.ok) return new(false, "safety: " + safety.reason, null, false, false, 0m, 0m);
        var sent = await SendOrderAsync(
            client, exchange, shared, side, QtyForExchange(exchange, symbol, rounded), leverage, posSide, isClose: false,
            "ab-s-" + Guid.NewGuid().ToString("N")[..12], ct, snapped, rounded).ConfigureAwait(false);
        if (!sent.Ok)
        {
            if (IsBalanceError(sent.Error))
                NoteVenueUsdt(exchange, 0);
            else
                MarkSymbolCooldown(symbol, sent.Error, RejectCooldown);
            _logger.LogWarning("SCALP open fail {Ex} {Sym} {Side}: {Err}", exchange, symbol, isLong ? "L" : "S", sent.Error);
            if (sent.Accepted && !sent.Verified)
            {
                var why = $"Scalp order state unknown for {symbol} on {exchange}";
                _guard.Kill(why);
                _ = _safety.AlertAsync("LIVE SCALP RECONCILIATION REQUIRED", why, CancellationToken.None);
            }
            return new(false, sent.Error, sent.Id, sent.Accepted, sent.Verified, sent.FilledBaseQty, sent.AveragePrice);
        }
        _logger.LogInformation("SCALP open {Ex} {Sym} {Side} qty={Q} @ {P}", exchange, symbol, isLong ? "L" : "S", rounded, snapped);
        return new(true, "", sent.Id, sent.Accepted, sent.Verified, sent.FilledBaseQty, sent.AveragePrice);
    }

    /// <summary>Flatten one scalp leg. Opposite side, same position side.</summary>
    public async Task<LiveLegOrderResult> CloseScalpLegAsync(
        string exchange, string symbol, bool wasLong, decimal qty, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(exchange) || string.IsNullOrWhiteSpace(symbol) || qty <= 0)
            return new(false, "bad scalp close", null, false, false, 0m, 0m);
        var client = CreateClient(exchange);
        if (client == null)
            return new(false, "no trade credentials for " + exchange, null, false, false, 0m, 0m);
        var baseAsset = symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
            ? symbol[..^4] : symbol;
        var shared = new SharedSymbol(TradingMode.PerpetualLinear, baseAsset, "USDT");
        var rounded = RoundBaseQty(qty, 0);
        if (rounded <= 0) rounded = qty;
        var side = wasLong ? SharedOrderSide.Sell : SharedOrderSide.Buy;
        var posSide = wasLong ? SharedPositionSide.Long : SharedPositionSide.Short;
        var ok = await TryReduceAsync(client, exchange, shared, QtyForExchange(exchange, symbol, rounded), side, posSide, ct, rounded).ConfigureAwait(false);
        if (ok.Accepted && !ok.Verified)
        {
            var why = $"Scalp close fill state unknown for {symbol} on {exchange}";
            _guard.Kill(why);
            _ = _safety.AlertAsync("LIVE SCALP RECONCILIATION REQUIRED", why, CancellationToken.None);
        }
        return new(ok.Ok, ok.Error, ok.Id, ok.Accepted, ok.Verified, ok.FilledBaseQty, ok.AveragePrice);
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
