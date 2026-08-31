using System.Collections.Concurrent;
using ArbitrageBot.Configuration;
using Binance.Net;
using Binance.Net.Objects.Models.Futures;
using Bybit.Net;
using OKX.Net.Clients;
using Bitget.Net;
using OKX.Net;
using GateIo.Net;
using Bitget.Net.Enums;
using Bitget.Net.Enums.Uta;
using Bybit.Net.Enums;
using CryptoClients.Net;
using CryptoClients.Net.Models;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.SharedApis;
using Microsoft.Extensions.Options;

namespace ArbitrageBot.Services;

/// <summary>
/// Phase 2: read-only balances via exchange-native APIs (Binance/Bybit/Bitget) + shared fallback.
/// </summary>
public sealed class LiveExecutionService : ILiveExecutionService
{
    private readonly ISettingsStore _settings;
    private readonly LiveTradingGuard _guard;
    private readonly ArbitrageOptions _options;
    private readonly ILogger<LiveExecutionService> _logger;
    private readonly LiveOrderEngine _orders;
    private readonly ConcurrentDictionary<string, (DateTime at, object data)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(12);

    public LiveExecutionService(
        ISettingsStore settings,
        LiveTradingGuard guard,
        IOptions<ArbitrageOptions> options,
        LiveOrderEngine orders,
        ILogger<LiveExecutionService> logger)
    {
        _settings = settings;
        _guard = guard;
        _options = options.Value;
        _orders = orders;
        _logger = logger;
    }

    public Task<object> VerifyCredentialsAsync(CancellationToken ct = default)
        => FetchAllAsync(includePositions: false, ct);

    public Task<object> GetLiveBalancesAsync(CancellationToken ct = default)
        => FetchAllAsync(includePositions: true, ct);

    public Task<object> TryOpenHedgeAsync(LiveHedgeRequest request, CancellationToken ct = default)
        => _orders.TryOpenAsync(request, ct);

    public Task<object> TryCloseHedgeAsync(string tradeId, CancellationToken ct = default)
        => _orders.TryCloseAsync(tradeId, null, ct);

    public Task<int> TryCloseConvergedAsync(
        Func<string, string, string, (decimal longBid, decimal shortAsk)?> getMarks,
        decimal closeBelowNetPercent,
        CancellationToken ct = default)
        => _orders.TryCloseConvergedAsync(
            getMarks,
            closeBelowNetPercent,
            _options.FuturesMaxHoldMinutes > 0 ? _options.FuturesMaxHoldMinutes : 15,
            _options.LiveStopLossUsd,
            ct);

    public IReadOnlyList<Models.LiveHedgePosition> GetOpenPositions() => _orders.GetOpen();

    public object GetLivePaperSnapshot() => new
    {
        open = _orders.GetOpen().Select(p => new
        {
            tradeId = p.Id,
            p.Symbol,
            p.LongExchange,
            p.ShortExchange,
            p.BaseQty,
            p.LongEntry,
            p.ShortEntry,
            p.OpenedAt,
            p.Status,
            p.Message
        }),
        closed = _orders.GetClosed(20),
        openCount = _orders.GetOpen().Count
    };

    /// <summary>
    /// Truth view: bot ledger + raw non-zero futures positions from each exchange.
    /// Use this in UI so restart / half-filled legs are still visible.
    /// </summary>
    public async Task<object> GetLivePositionsViewAsync(CancellationToken ct = default)
    {
        var ledgerOpen = _orders.GetOpen();
        var bal = await FetchAllAsync(includePositions: true, ct).ConfigureAwait(false);

        var exchangeLegs = new List<object>();
        try
        {
            // bal is anonymous — use reflection / dynamic JSON round-trip
            var json = System.Text.Json.JsonSerializer.Serialize(bal);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("exchanges", out var exArr) && exArr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var ex in exArr.EnumerateArray())
                {
                    var name = ex.TryGetProperty("exchange", out var en) ? en.GetString() ?? "?" : "?";
                    if (!ex.TryGetProperty("positions", out var posEl) || posEl.ValueKind != System.Text.Json.JsonValueKind.Array)
                        continue;
                    foreach (var p in posEl.EnumerateArray())
                    {
                        decimal qty = 0;
                        if (p.TryGetProperty("quantity", out var q))
                            q.TryGetDecimal(out qty);
                        if (qty == 0) continue;
                        exchangeLegs.Add(new
                        {
                            exchange = name,
                            symbol = p.TryGetProperty("symbol", out var s) ? s.GetString() : null,
                            side = p.TryGetProperty("side", out var sd) ? sd.GetString() : null,
                            quantity = qty,
                            entryPrice = p.TryGetProperty("entryPrice", out var ep) && ep.TryGetDecimal(out var epv) ? epv : (decimal?)null,
                            unrealizedPnl = p.TryGetProperty("unrealizedPnl", out var up) && up.TryGetDecimal(out var upv) ? upv : (decimal?)null,
                            leverage = p.TryGetProperty("leverage", out var lv) && lv.TryGetDecimal(out var lvv) ? lvv : (decimal?)null
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to flatten exchange positions");
        }

        return new
        {
            utc = DateTime.UtcNow,
            openLedgerCount = ledgerOpen.Count,
            exchangeLegCount = exchangeLegs.Count,
            ledger = ledgerOpen.Select(p => new
            {
                tradeId = p.Id,
                p.Symbol,
                p.LongExchange,
                p.ShortExchange,
                p.BaseQty,
                p.LongEntry,
                p.ShortEntry,
                p.OpenedAt,
                p.Status,
                p.Message,
                source = "ledger"
            }),
            exchangeLegs,
            closed = _orders.GetClosed(20),
            balances = bal
        };
    }

    private async Task<object> FetchAllAsync(bool includePositions, CancellationToken ct)
    {
        var exchanges = new List<object>();
        decimal totalUsdt = 0;
        var anyOk = false;

        foreach (var ex in _options.NormalizedExchanges)
        {
            var row = await FetchExchangeAsync(ex, includePositions, ct).ConfigureAwait(false);
            exchanges.Add(row.data);
            if (row.ok)
            {
                anyOk = true;
                totalUsdt += row.usdtTotal;
            }
        }

        return new
        {
            utc = DateTime.UtcNow,
            phase = 2,
            guard = _guard.Status(),
            totalUsdtApprox = totalUsdt,
            anyOk,
            tip = "Binance/Bybit/Bitget use native USDT-M APIs. Keys need Futures read (+ passphrase for Bitget).",
            exchanges
        };
    }

    private async Task<(bool ok, decimal usdtTotal, object data)> FetchExchangeAsync(
        string exchange, bool includePositions, CancellationToken ct)
    {
        var cacheKey = exchange + (includePositions ? ":pos" : ":bal");
        if (_cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.at < CacheTtl)
            return (true, ExtractUsdt(cached.data), cached.data);

        var cred = _settings.GetCredential(exchange);
        if (cred == null || string.IsNullOrWhiteSpace(cred.ApiKey) || string.IsNullOrWhiteSpace(cred.ApiSecret))
        {
            return (false, 0, new
            {
                exchange,
                ok = false,
                hasKey = false,
                error = "no api key/secret — Settings → Save " + exchange
            });
        }

        var needsPass = exchange.Equals("OKX", StringComparison.OrdinalIgnoreCase)
                        || exchange.Equals("Bitget", StringComparison.OrdinalIgnoreCase);
        if (needsPass && string.IsNullOrWhiteSpace(cred.Passphrase))
        {
            return (false, 0, new
            {
                exchange,
                ok = false,
                hasKey = true,
                permission = cred.Permission,
                error = "passphrase required for " + exchange
            });
        }

        try
        {
            var rest = CreateAuthedClient(exchange, cred);
            var native = await TryNativeBalancesAsync(rest, exchange, ct).ConfigureAwait(false);

            if (!native.ok)
            {
                // Shared API fallback
                var shared = await TrySharedBalancesAsync(rest, exchange, ct).ConfigureAwait(false);
                if (!shared.ok)
                {
                    return (false, 0, new
                    {
                        exchange,
                        ok = false,
                        hasKey = true,
                        permission = cred.Permission,
                        error = native.err ?? shared.err ?? "balance failed",
                        detail = native.detail ?? shared.detail,
                        hint = HintForError(native.err ?? shared.err)
                    });
                }
                native = shared;
            }

            object? positions = null;
            if (includePositions)
                positions = await TryNativePositionsAsync(rest, exchange, ct).ConfigureAwait(false);

            var usdt = native.balances
                .Where(b => b.asset.Equals("USDT", StringComparison.OrdinalIgnoreCase)
                            || b.asset.Equals("USD", StringComparison.OrdinalIgnoreCase))
                .Sum(b => b.total);

            var payload = new
            {
                exchange,
                ok = true,
                hasKey = true,
                permission = cred.Permission,
                tradePermission = string.Equals(cred.Permission, "trade", StringComparison.OrdinalIgnoreCase),
                accountMode = native.mode,
                usdtTotal = usdt,
                balances = native.balances.Select(b => new { b.asset, b.available, b.total }).ToList(),
                positions,
                error = (string?)null
            };
            _cache[cacheKey] = (DateTime.UtcNow, payload);
            return (true, usdt, payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live fetch failed for {Ex}", exchange);
            return (false, 0, new
            {
                exchange,
                ok = false,
                hasKey = true,
                permission = cred.Permission,
                error = ex.Message,
                hint = HintForError(ex.Message)
            });
        }
    }

    private static ExchangeRestClient CreateAuthedClient(string exchange, ExchangeCredential cred)
    {
        var rest = new ExchangeRestClient();
        var key = (cred.ApiKey ?? "").Trim();
        var secret = (cred.ApiSecret ?? "").Trim();
        var pass = (cred.Passphrase ?? "").Trim();

        // Exchange-native credentials (HMAC) — CreateFrom/HMAC alone often fails to attach to UsdFutures clients
        var bag = new ExchangeCredentials();
        var name = exchange.Trim();
        if (name.Equals("Binance", StringComparison.OrdinalIgnoreCase))
            bag.Binance = new BinanceCredentials(key, secret);
        else if (name.Equals("Bybit", StringComparison.OrdinalIgnoreCase))
            bag.Bybit = new BybitCredentials(key, secret);
        else if (name.Equals("Bitget", StringComparison.OrdinalIgnoreCase))
            bag.Bitget = new BitgetCredentials(key, secret, pass);
        else if (name.Equals("OKX", StringComparison.OrdinalIgnoreCase))
            bag.OKX = new OKXCredentials(key, secret, pass);
        else if (name.Equals("GateIo", StringComparison.OrdinalIgnoreCase) || name.Equals("GateIO", StringComparison.OrdinalIgnoreCase))
            bag.GateIo = new GateIoCredentials(key, secret);
        else
        {
            // generic fallback
            rest.SetApiCredentials(name, new DynamicCredentials(
                TradingMode.PerpetualLinear, key, secret, pass, ""));
            return rest;
        }

        rest.SetApiCredentials(bag);

        // Also set on the specific client instance for safety
        try
        {
            if (name.Equals("Binance", StringComparison.OrdinalIgnoreCase))
                rest.Binance.SetApiCredentials(new BinanceCredentials(key, secret));
            else if (name.Equals("Bybit", StringComparison.OrdinalIgnoreCase))
                rest.Bybit.SetApiCredentials(new BybitCredentials(key, secret));
            else if (name.Equals("Bitget", StringComparison.OrdinalIgnoreCase))
                rest.Bitget.SetApiCredentials(new BitgetCredentials(key, secret, pass));
            else if (name.Equals("OKX", StringComparison.OrdinalIgnoreCase))
                rest.OKX.SetApiCredentials(new OKXCredentials(key, secret, pass));
        }
        catch
        {
            /* client-specific set is best-effort */
        }

        return rest;
    }

    private static string FormatErr(object? error, string? original = null)
    {
        if (error is null)
            return string.IsNullOrEmpty(original) ? "unknown error" : Truncate(original) ?? "unknown";

        var parts = new List<string>();
        try
        {
            var et = error.GetType();
            string? Prop(params string[] names)
            {
                foreach (var n in names)
                {
                    var p = et.GetProperty(n);
                    var v = p?.GetValue(error)?.ToString();
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
                return null;
            }
            var code = Prop("ErrorCode", "Code");
            var msg = Prop("Message", "ErrorDescription", "ErrorType");
            if (code != null) parts.Add("code=" + code);
            if (msg != null) parts.Add(msg);
            var s = error.ToString();
            if (parts.Count == 0 && !string.IsNullOrWhiteSpace(s) && s != et.FullName)
                parts.Add(s);
        }
        catch (Exception ex)
        {
            parts.Add("format-fail:" + ex.Message);
        }

        if (parts.Count == 0 && !string.IsNullOrEmpty(original))
            parts.Add(Truncate(original)!);
        if (parts.Count == 0)
            parts.Add("OKX/API rejected (check passphrase, IP whitelist, key permissions)");
        return string.Join(" | ", parts);
    }

    private async Task<(bool ok, List<(string asset, decimal available, decimal total)> balances, string? err, string? detail, string mode)>
        TryNativeBalancesAsync(ExchangeRestClient rest, string exchange, CancellationToken ct)
    {
        try
        {
            if (exchange.Equals("Binance", StringComparison.OrdinalIgnoreCase))
            {
                var r = await rest.Binance.UsdFuturesApi.Account.GetBalancesAsync(ct: ct).ConfigureAwait(false);
                if (!r.Success)
                    return (false, [], FormatErr(r.Error, r.OriginalData),
                        Truncate(r.OriginalData), "Binance.UsdFutures");

                var list = (r.Data ?? Array.Empty<BinanceUsdFuturesAccountBalance>())
                    .Where(b => b.WalletBalance != 0 || b.AvailableBalance != 0)
                    .Select(b => (b.Asset, b.AvailableBalance, b.WalletBalance))
                    .OrderByDescending(b => b.WalletBalance)
                    .Take(40)
                    .ToList();
                // Success with zero assets is still OK (empty futures wallet)
                return (true, list, null, null, "Binance.UsdFutures");
            }

            if (exchange.Equals("Bybit", StringComparison.OrdinalIgnoreCase))
            {
                string? lastBybitErr = null;
                string? lastBybitDetail = null;
                // Unified first (UTA), then Contract
                foreach (var accType in new[] { AccountType.Unified, AccountType.Contract, AccountType.Fund })
                {
                    var r = await rest.Bybit.V5Api.Account.GetBalancesAsync(accType, ct: ct).ConfigureAwait(false);
                    if (!r.Success)
                    {
                        lastBybitErr = FormatErr(r.Error, r.OriginalData);
                        lastBybitDetail = Truncate(r.OriginalData);
                        _logger.LogWarning("Bybit {T}: {E}", accType, lastBybitErr);
                        continue;
                    }
                    var list = new List<(string, decimal, decimal)>();
                    if (r.Data?.List != null)
                    {
                        foreach (var acct in r.Data.List)
                        {
                            if (acct.Assets == null) continue;
                            foreach (var a in acct.Assets)
                            {
                                var total = a.WalletBalance ?? a.Equity ?? a.UsdValue ?? 0m;
                                var avail = a.AvailableToWithdraw ?? a.Free ?? 0m;
                                if (total == 0 && avail == 0) continue;
                                list.Add((a.Asset ?? "?", avail, total));
                            }
                        }
                    }
                    // also surface account-level total if no assets expanded
                    if (list.Count == 0 && r.Data.List != null)
                    {
                        foreach (var acct in r.Data.List)
                        {
                            var tw = acct.TotalWalletBalance ?? acct.TotalEquity ?? 0m;
                            var ta = acct.TotalAvailableBalance ?? 0m;
                            if (tw != 0 || ta != 0)
                                list.Add(("USDT", ta, tw));
                        }
                    }
                    return (true, list.OrderByDescending(x => x.Item3).Take(40).ToList(), null, null, "Bybit." + accType);
                }
                return (false, [], "Bybit: Unified/Contract/Fund all failed — " + (lastBybitErr ?? ""), lastBybitDetail, "Bybit");
            }

            if (exchange.Equals("Bitget", StringComparison.OrdinalIgnoreCase))
            {
                // Bitget UTA (Unified) uses /api/v3 via UnifiedApi — Classic FuturesApiV2 returns 40085.
                string? lastErr = null, lastDetail = null;

                // 1) UnifiedApi.Account.GetBalancesAsync — primary for UTA
                try
                {
                    var r = await rest.Bitget.UnifiedApi.Account.GetBalancesAsync(ct).ConfigureAwait(false);
                    if (r.Success && r.Data != null)
                    {
                        var assets = r.Data.Assets ?? [];
                        var list = assets
                            .Select(b =>
                            {
                                var avail = b.Available;
                                var total = b.Equity != 0 ? b.Equity : (b.Balance != 0 ? b.Balance : avail + b.Locked);
                                return (b.Asset ?? "?", avail, total);
                            })
                            .Where(x => x.Item1 is "USDT" or "USDC" || x.Item3 > 0.01m)
                            .OrderByDescending(x => x.Item3)
                            .Take(20)
                            .ToList();
                        if (list.Count == 0 && r.Data.UsdtEquity != 0)
                            list.Add(("USDT", r.Data.UsdtEquity, r.Data.UsdtEquity));
                        if (list.Count > 0)
                            return (true, list, null, null, "Bitget.UnifiedApi(UTA)");
                    }
                    else if (!r.Success)
                    {
                        lastErr = FormatErr(r.Error, r.OriginalData);
                        lastDetail = Truncate(r.OriginalData);
                    }
                }
                catch (Exception ex) { lastErr = "UnifiedApi: " + ex.Message; }

                // 2) SpotApiV2 — sometimes still works for funding/spot wallet
                try
                {
                    var r = await rest.Bitget.SpotApiV2.Account.GetSpotBalancesAsync(ct: ct).ConfigureAwait(false);
                    if (r.Success && r.Data != null)
                    {
                        var list = r.Data
                            .Select(b =>
                            {
                                var asset = b.GetType().GetProperty("Asset")?.GetValue(b)?.ToString()
                                            ?? b.GetType().GetProperty("Coin")?.GetValue(b)?.ToString()
                                            ?? "?";
                                decimal Dec(string n)
                                {
                                    var v = b.GetType().GetProperty(n)?.GetValue(b);
                                    return v is decimal d ? d : 0;
                                }
                                var avail = Dec("Available");
                                var locked = Dec("Locked") + Dec("Frozen");
                                var total = avail + locked;
                                if (total == 0) total = Dec("Equity") != 0 ? Dec("Equity") : avail;
                                return (asset, avail, total != 0 ? total : avail);
                            })
                            .Where(x => x.Item1 is "USDT" or "USDC" || x.Item3 > 1)
                            .OrderByDescending(x => x.Item3).Take(20).ToList();
                        if (list.Count > 0)
                            return (true, list, null, null, "Bitget.SpotApiV2");
                    }
                    else if (!r.Success)
                    {
                        lastErr ??= FormatErr(r.Error, r.OriginalData);
                        lastDetail ??= Truncate(r.OriginalData);
                    }
                }
                catch (Exception ex) { lastErr = (lastErr ?? "") + " | SpotApiV2: " + ex.Message; }

                // 3) FuturesApiV2 — Classic only (fails with 40085 on UTA)
                try
                {
                    var r = await rest.Bitget.FuturesApiV2.Account
                        .GetBalancesAsync(BitgetProductTypeV2.UsdtFutures, ct).ConfigureAwait(false);
                    if (r.Success && r.Data != null)
                    {
                        var list = (r.Data ?? [])
                            .Select(b =>
                            {
                                var asset = b.GetType().GetProperty("MarginCoin")?.GetValue(b)?.ToString()
                                            ?? b.GetType().GetProperty("Asset")?.GetValue(b)?.ToString()
                                            ?? "USDT";
                                decimal Dec(string n)
                                {
                                    var v = b.GetType().GetProperty(n)?.GetValue(b);
                                    return v is decimal d ? d : 0;
                                }
                                var avail = Dec("Available") != 0 ? Dec("Available") : Dec("MaxOpenPosAvailable");
                                var total = Dec("Available") + Dec("Locked");
                                if (total == 0) total = Dec("AccountEquity");
                                if (total == 0) total = avail;
                                return (asset, avail, total != 0 ? total : avail);
                            })
                            .Where(x => x.Item3 != 0 || x.Item2 != 0)
                            .ToList();
                        return (true, list, null, null, "Bitget.UsdtFutures(Classic)");
                    }
                    var classicErr = FormatErr(r.Error, r.OriginalData);
                    lastErr ??= classicErr;
                    lastDetail ??= Truncate(r.OriginalData);
                    if (classicErr != null && classicErr.Contains("40085", StringComparison.Ordinal))
                        lastErr = "Bitget UTA (Unified): Classic Futures API blocked (40085). Using UnifiedApi path required.";
                }
                catch (Exception ex) { lastErr = (lastErr ?? "") + " | FuturesApiV2: " + ex.Message; }

                return (false, [], lastErr ?? "Bitget: Unified+Spot+Classic all failed", lastDetail,
                    "Bitget UTA needs Unified API key permissions (uta trade/read). Classic keys only work on Classic accounts.");
            }

            if (exchange.Equals("OKX", StringComparison.OrdinalIgnoreCase))
            {
                var credOkx = _settings.GetCredential("OKX");
                var key = (credOkx?.ApiKey ?? "").Trim();
                var secret = (credOkx?.ApiSecret ?? "").Trim();
                var pass = (credOkx?.Passphrase ?? "").Trim();
                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(secret))
                    return (false, [], "OKX key/secret missing", null, "OKX");
                if (string.IsNullOrEmpty(pass))
                    return (false, [], "OKX passphrase required — must match the passphrase set when creating the API key (not account password)", null, "OKX");

                var envs = new (string name, OKXEnvironment env)[]
                {
                    ("Live", OKXEnvironment.Live),
                    ("Demo", OKXEnvironment.Demo),
                    ("Europe", OKXEnvironment.Europe),
                    ("EuropeDemo", OKXEnvironment.EuropeDemo),
                };

                var attempts = new List<string>();
                string? lastErr = null;
                string? lastDetail = null;

                foreach (var (envName, env) in envs)
                {
                    try
                    {
                        using var okx = new OKXRestClient(o =>
                        {
                            o.ApiCredentials = new OKXCredentials(key, secret, pass);
                            o.Environment = env;
                            o.OutputOriginalData = true;
                        });

                        // Config first — lighter, clear auth errors
                        var cfg = await okx.UnifiedApi.Account.GetAccountConfigurationAsync(ct).ConfigureAwait(false);
                        if (!cfg.Success)
                        {
                            var e = FormatErr(cfg.Error, cfg.OriginalData);
                            attempts.Add(envName + ":cfg " + e);
                            lastErr = e;
                            lastDetail = Truncate(cfg.OriginalData);
                            _logger.LogWarning("OKX {Env} config fail: {E}", envName, e);
                            continue;
                        }

                        var r = await okx.UnifiedApi.Account.GetAccountBalanceAsync(asset: null!, ct: ct)
                            .ConfigureAwait(false);
                        if (r.Success)
                        {
                            var list = new List<(string, decimal, decimal)>();
                            if (r.Data != null && (r.Data.Details == null || r.Data.Details.Length == 0))
                            {
                                if (r.Data.TotalEquity != 0 || (r.Data.AvailableEquity ?? 0) != 0)
                                    list.Add(("USDT", r.Data.AvailableEquity ?? 0, r.Data.TotalEquity));
                            }
                            if (r.Data?.Details != null)
                            {
                                foreach (var d in r.Data.Details)
                                {
                                    var asset = d.Asset ?? "?";
                                    var total = d.Equity ?? d.CashBalance ?? 0;
                                    if (total == 0) total = (d.AvailableBalance ?? 0) + (d.FrozenBalance ?? 0);
                                    var avail = d.AvailableBalance ?? d.AvailableEquity ?? 0;
                                    if (total == 0 && avail == 0) continue;
                                    list.Add((asset, avail, total));
                                }
                            }
                            _logger.LogInformation("OKX balances OK via {Env}, assets={N}", envName, list.Count);
                            return (true, list.OrderByDescending(x => x.Item3).Take(40).ToList(), null, null, "OKX." + envName);
                        }

                        lastErr = FormatErr(r.Error, r.OriginalData);
                        lastDetail = Truncate(r.OriginalData);
                        attempts.Add(envName + ":bal " + lastErr);

                        var f = await okx.UnifiedApi.Account.GetFundingBalanceAsync(asset: null!, ct: ct)
                            .ConfigureAwait(false);
                        if (f.Success && f.Data != null)
                        {
                            var flist = f.Data
                                .Where(b => b.Balance != 0 || b.Available != 0)
                                .Select(b => (b.Asset ?? "?", b.Available, b.Balance))
                                .OrderByDescending(x => x.Item3)
                                .Take(40)
                                .ToList();
                            return (true, flist, null, null, "OKX.Funding." + envName);
                        }
                        if (f.Error != null)
                            attempts.Add(envName + ":fund " + FormatErr(f.Error, f.OriginalData));
                    }
                    catch (Exception ex)
                    {
                        attempts.Add(envName + ":ex " + ex.Message);
                        lastErr = ex.Message;
                    }
                }

                var summary = string.Join(" · ", attempts.Take(8));
                var detailBlob = (lastDetail ?? "") + summary;
                var hint50119 = detailBlob.Contains("50119", StringComparison.Ordinal) ||
                                detailBlob.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase)
                    ? " | code 50119 = API key doesn't exist on OKX (wrong key saved, or key deleted). Clear OKX in Settings → paste NEW key/secret/passphrase from okx.com → Save. Or remove OKX from exchange list."
                    : " | Fix: passphrase, IP whitelist, or remove OKX from exchanges";
                var finger = key.Length >= 8 ? key[..4] + "…" + key[^4..] : "?";
                return (false, [],
                    (lastErr ?? "Unauthorized") + " | tried: " + summary + hint50119 + " | usingKey=" + finger,
                    lastDetail, "OKX");
            }
        }
        catch (Exception ex)
        {
            return (false, [], ex.Message, null, "native-exception");
        }

        return (false, [], "no native handler", null, "none");
    }

    private static async Task<(bool ok, List<(string asset, decimal available, decimal total)> balances, string? err, string? detail, string mode)>
        TrySharedBalancesAsync(ExchangeRestClient rest, string exchange, CancellationToken ct)
    {
        var exParams = BuildExchangeParameters(exchange);
        string? lastErr = null;
        string? lastDetail = null;

        foreach (var mode in new[] { TradingMode.PerpetualLinear, TradingMode.Spot })
        {
            try
            {
                var client = rest.GetBalancesClient(mode, exchange);
                var req = new GetBalancesRequest(mode, exParams);
                var result = client != null
                    ? await client.GetBalancesAsync(req, ct).ConfigureAwait(false)
                    : await rest.GetBalancesAsync(exchange, req, ct).ConfigureAwait(false);

                if (result.Success)
                {
                    var list = (result.Data ?? [])
                        .Where(b => b.Total != 0 || b.Available != 0)
                        .Select(b => (b.Asset, b.Available, b.Total))
                        .OrderByDescending(b => b.Total)
                        .Take(40)
                        .ToList();
                    return (true, list, null, null, "Shared." + mode);
                }

                lastErr = result.Error?.Message ?? "shared fail";
                lastDetail = Truncate(result.OriginalData);
            }
            catch (Exception ex)
            {
                lastErr = ex.Message;
            }
        }

        return (false, [], lastErr, lastDetail, "Shared.none");
    }

    private static async Task<object?> TryNativePositionsAsync(
        ExchangeRestClient rest, string exchange, CancellationToken ct)
    {
        // Bybit: use native V5 API for positions — shared API often returns empty for Bybit
        if (exchange.Equals("Bybit", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var r = await rest.Bybit.V5Api.Trading.GetPositionsAsync(
                    Bybit.Net.Enums.Category.Linear, settleAsset: "USDT", ct: ct).ConfigureAwait(false);
                if (r.Success && r.Data?.List != null)
                {
                    var list = r.Data.List
                        .Where(p => p.Quantity != 0)
                        .Select(p => new
                        {
                            symbol = p.Symbol,
                            side = p.Side.ToString(),
                            quantity = p.Quantity,
                            entryPrice = p.AveragePrice,
                            unrealizedPnl = p.UnrealizedPnl,
                            leverage = p.Leverage
                        }).Take(50).ToList();
                    return list.Count > 0 ? (object)list : new List<object>();
                }
                if (r.Error != null)
                    return new { error = "Bybit positions: " + FormatErr(r.Error, r.OriginalData), detail = Truncate(r.OriginalData) };
            }
            catch (Exception ex)
            {
                return new { error = "Bybit native positions: " + ex.Message };
            }
        }

        // Bitget: UTA UnifiedApi first, then Classic FuturesApiV2, then shared
        if (exchange.Equals("Bitget", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // symbol null = all USDT-M positions on UTA
                var r = await rest.Bitget.UnifiedApi.Trading
                    .GetPositionsAsync(ProductCategory.UsdtFutures, symbol: null!, ct: ct).ConfigureAwait(false);
                if (r.Success && r.Data != null)
                {
                    var list = r.Data
                        .Select(p =>
                        {
                            var qty = p.Total != 0 ? p.Total : p.Available;
                            return new
                            {
                                symbol = p.Symbol,
                                side = p.PositionSide?.ToString() ?? "—",
                                quantity = qty,
                                entryPrice = (decimal?)p.AveragePrice,
                                unrealizedPnl = (decimal?)p.UnrealisedPnl,
                                leverage = (decimal?)p.Leverage
                            };
                        })
                        .Where(x => x.quantity != 0)
                        .Take(50)
                        .ToList();
                    return list.Count > 0 ? (object)list : new List<object>();
                }
            }
            catch { /* fall through */ }

            try
            {
                var r = await rest.Bitget.FuturesApiV2.Trading
                    .GetPositionsAsync(BitgetProductTypeV2.UsdtFutures, "USDT", ct: ct).ConfigureAwait(false);
                if (r.Success && r.Data != null)
                {
                    var list = r.Data
                        .Where(p => { var v = p.GetType().GetProperty("Quantity")?.GetValue(p); return v is decimal d && d != 0; })
                        .Select(p =>
                        {
                            decimal Dec(string n) { var v = p.GetType().GetProperty(n)?.GetValue(p); return v is decimal d ? d : 0; }
                            string Str(string n) => p.GetType().GetProperty(n)?.GetValue(p)?.ToString() ?? "—";
                            return new
                            {
                                symbol = Str("Symbol"),
                                side = Str("PositionSide"),
                                quantity = Dec("Quantity"),
                                entryPrice = (decimal?)Dec("AverageOpenPrice"),
                                unrealizedPnl = (decimal?)Dec("UnrealizedPnl"),
                                leverage = (decimal?)Dec("Leverage")
                            };
                        }).Take(50).ToList();
                    return list.Count > 0 ? (object)list : new List<object>();
                }
            }
            catch { /* fall through */ }
        }
        return await TrySharedPositionsAsync(rest, exchange, ct).ConfigureAwait(false);
    }

    private static async Task<object?> TrySharedPositionsAsync(
        ExchangeRestClient rest, string exchange, CancellationToken ct)
    {
        try
        {
            var req = new GetPositionsRequest(TradingMode.PerpetualLinear, BuildExchangeParameters(exchange));
            var result = await rest.GetPositionsAsync(exchange, req, ct).ConfigureAwait(false);
            if (!result.Success)
                return new { error = result.Error?.Message, detail = Truncate(result.OriginalData) };
            // Only non-zero positions — exchanges often return many empty shells
            return (result.Data ?? [])
                .Where(p => p.PositionSize != 0)
                .Select(p => new
                {
                    symbol = p.Symbol,
                    side = p.PositionSide.ToString(),
                    quantity = p.PositionSize,
                    entryPrice = p.AverageOpenPrice,
                    unrealizedPnl = p.UnrealizedPnl,
                    leverage = p.Leverage
                }).Take(50).ToList();
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private static ExchangeParameters BuildExchangeParameters(string exchange)
    {
        if (exchange.Equals("Bitget", StringComparison.OrdinalIgnoreCase))
            return new ExchangeParameters(
                new ExchangeParameter("Bitget", "ProductType", "UsdtFutures"),
                new ExchangeParameter("Bitget", "MarginAsset", "USDT"),
                new ExchangeParameter("Bitget", "marginCoin", "USDT"));
        if (exchange.Equals("GateIo", StringComparison.OrdinalIgnoreCase)
            || exchange.Equals("Bybit", StringComparison.OrdinalIgnoreCase))
            return new ExchangeParameters(new ExchangeParameter(exchange, "SettleAsset", "usdt"));
        return new ExchangeParameters();
    }

    private static string? Truncate(string? s, int max = 300)
        => string.IsNullOrEmpty(s) ? null : (s.Length <= max ? s : s[..max] + "…");

    private static string? HintForError(string? err)
    {
        if (string.IsNullOrEmpty(err)) return "Enable Futures on API key; check IP whitelist; Bitget needs passphrase";
        if (err.Contains("40085", StringComparison.Ordinal) || err.Contains("Unified Account mode", StringComparison.OrdinalIgnoreCase))
            return "Bitget UTA: Classic API blocked. Bot now uses UnifiedApi; recreate key with UTA read/trade perms if still failing.";
        if (err.Contains("Invalid", StringComparison.OrdinalIgnoreCase)
            || err.Contains("signature", StringComparison.OrdinalIgnoreCase)
            || err.Contains("API-key", StringComparison.OrdinalIgnoreCase))
            return "Invalid key/secret or wrong key type";
        if (err.Contains("IP", StringComparison.OrdinalIgnoreCase))
            return "Whitelist this server IP on the exchange API settings";
        if (err.Contains("permission", StringComparison.OrdinalIgnoreCase)
            || err.Contains("401", StringComparison.OrdinalIgnoreCase)
            || err.Contains("403", StringComparison.OrdinalIgnoreCase))
            return "Enable USDT-M Futures Read on the API key";
        return null;
    }

    private static decimal ExtractUsdt(object data)
    {
        try
        {
            var prop = data.GetType().GetProperty("usdtTotal");
            if (prop?.GetValue(data) is decimal d) return d;
        }
        catch { /* ignore */ }
        return 0;
    }
}
