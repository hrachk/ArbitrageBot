using System.Collections.Concurrent;
using ArbitrageBot.Configuration;
using CryptoClients.Net;
using CryptoClients.Net.Models;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.SharedApis;
using Microsoft.Extensions.Options;

namespace ArbitrageBot.Services;

/// <summary>
/// Phase 2: read-only futures balances + positions via CryptoClients unified REST.
/// </summary>
public sealed class LiveExecutionService : ILiveExecutionService
{
    private readonly ISettingsStore _settings;
    private readonly LiveTradingGuard _guard;
    private readonly ArbitrageOptions _options;
    private readonly ILogger<LiveExecutionService> _logger;
    private readonly ConcurrentDictionary<string, (DateTime at, object data)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);

    public LiveExecutionService(
        ISettingsStore settings,
        LiveTradingGuard guard,
        IOptions<ArbitrageOptions> options,
        ILogger<LiveExecutionService> logger)
    {
        _settings = settings;
        _guard = guard;
        _options = options.Value;
        _logger = logger;
    }

    public Task<object> VerifyCredentialsAsync(CancellationToken ct = default)
        => FetchAllAsync(includePositions: false, ct);

    public Task<object> GetLiveBalancesAsync(CancellationToken ct = default)
        => FetchAllAsync(includePositions: true, ct);

    public Task<object> TryOpenHedgeAsync(LiveHedgeRequest request, CancellationToken ct = default)
    {
        var check = _guard.CheckOpenAllowed(0, request.NotionalUsd);
        if (!check.ok)
            return Task.FromResult<object>(new { ok = false, phase = 2, error = check.reason });
        return Task.FromResult<object>(new
        {
            ok = false,
            phase = 2,
            error = "Phase 2 is read-only. Orders = Phase 3."
        });
    }

    public Task<object> TryCloseHedgeAsync(string tradeId, CancellationToken ct = default)
        => Task.FromResult<object>(new { ok = false, phase = 2, error = "Phase 2 is read-only." });

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
            tip = "Save API key+secret per exchange in Settings. OKX/Bitget need passphrase. Prefer read-only keys.",
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
                error = "no api key/secret — open Settings → Exchange API keys → Save " + exchange
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
                error = "passphrase required for " + exchange + " (Settings → Passphrase)"
            });
        }

        try
        {
            var rest = CreateAuthedClient(exchange, cred);
            var exParams = BuildExchangeParameters(exchange);

            // Explicit client selection avoids "Multiple API's available… specify TradingMode"
            var (balOk, balances, balErr, modeUsed) = await TryGetBalancesAsync(rest, exchange, exParams, ct)
                .ConfigureAwait(false);

            if (!balOk)
            {
                _logger.LogWarning("Live balance {Ex}: {Err}", exchange, balErr);
                return (false, 0, new
                {
                    exchange,
                    ok = false,
                    hasKey = true,
                    permission = cred.Permission,
                    error = balErr,
                    hint = HintForError(balErr)
                });
            }

            var balObjs = balances.Select(b => new { asset = b.asset, available = b.available, total = b.total }).ToList();
            var usdt = balances
                .Where(b => b.asset.Equals("USDT", StringComparison.OrdinalIgnoreCase)
                            || b.asset.Equals("USD", StringComparison.OrdinalIgnoreCase)
                            || b.asset.Equals("USDC", StringComparison.OrdinalIgnoreCase))
                .Sum(b => b.total);

            object? positions = null;
            if (includePositions)
                positions = await TryGetPositionsAsync(rest, exchange, exParams, ct).ConfigureAwait(false);

            var payload = new
            {
                exchange,
                ok = true,
                hasKey = true,
                permission = cred.Permission,
                tradePermission = string.Equals(cred.Permission, "trade", StringComparison.OrdinalIgnoreCase),
                accountMode = modeUsed,
                usdtTotal = usdt,
                balances = balObjs,
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

        // CryptoExchange.Net v12+: ApiCredentials is abstract — use HMAC* + CreateFrom
        try
        {
            ApiCredentials apiCred = string.IsNullOrEmpty(cred.Passphrase)
                ? new HMACCredential(cred.ApiKey!, cred.ApiSecret!)
                : new HMACPassCredential(cred.ApiKey!, cred.ApiSecret!, cred.Passphrase!);
            var all = ExchangeCredentials.CreateFrom(exchange, apiCred);
            rest.SetApiCredentials(all);
            return rest;
        }
        catch (Exception)
        {
            // Fallback: per-exchange DynamicCredentials (all trading modes share same key on most venues)
            foreach (var mode in new[] { TradingMode.PerpetualLinear, TradingMode.Spot })
            {
                try
                {
                    rest.SetApiCredentials(exchange, new DynamicCredentials(
                        mode,
                        cred.ApiKey!,
                        cred.ApiSecret!,
                        cred.Passphrase ?? "",
                        ""));
                }
                catch { /* try next mode */ }
            }
            return rest;
        }
    }

    private static async Task<(bool ok, List<(string asset, decimal available, decimal total)> balances, string? err, string mode)>
        TryGetBalancesAsync(ExchangeRestClient rest, string exchange, ExchangeParameters exParams, CancellationToken ct)
    {
        // Ordered attempts: USDT-M perp first, then unified, then spot (some keys only see spot)
        var attempts = new (string label, Func<IBalanceRestClient?> client, GetBalancesRequest req)[]
        {
            ("PerpetualLinear",
                () => rest.GetBalancesClient(TradingMode.PerpetualLinear, exchange),
                new GetBalancesRequest(TradingMode.PerpetualLinear, exParams)),
            ("PerpetualLinearFutures",
                () => rest.GetBalancesClient(SharedAccountType.PerpetualLinearFutures, exchange),
                new GetBalancesRequest(SharedAccountType.PerpetualLinearFutures, exParams)),
            ("Unified",
                () => rest.GetBalancesClient(SharedAccountType.Unified, exchange),
                new GetBalancesRequest(SharedAccountType.Unified, exParams)),
            ("Spot",
                () => rest.GetBalancesClient(TradingMode.Spot, exchange),
                new GetBalancesRequest(TradingMode.Spot, exParams)),
        };

        string? lastErr = null;
        foreach (var (label, clientFactory, req) in attempts)
        {
            try
            {
                // Ensure TradingMode is non-null when request uses it
                if (req.TradingMode == null && label.StartsWith("Perpetual", StringComparison.Ordinal))
                    req.TradingMode = TradingMode.PerpetualLinear;

                var client = clientFactory();
                var result = client != null
                    ? await client.GetBalancesAsync(req, ct).ConfigureAwait(false)
                    : await rest.GetBalancesAsync(exchange, req, ct).ConfigureAwait(false);

                if (result.Success && result.Data != null)
                {
                    var list = result.Data
                        .Select(b => (b.Asset, b.Available, b.Total))
                        .Where(b => b.Total != 0 || b.Available != 0)
                        .OrderByDescending(b => b.Total)
                        .Take(40)
                        .Select(b => (b.Asset, b.Available, b.Total))
                        .ToList();
                    return (true, list, null, label);
                }

                lastErr = result.Error?.Message ?? "empty";
            }
            catch (Exception ex)
            {
                lastErr = ex.Message;
            }
        }

        return (false, [], lastErr ?? "all balance attempts failed", "none");
    }

    private static async Task<object?> TryGetPositionsAsync(
        ExchangeRestClient rest, string exchange, ExchangeParameters exParams, CancellationToken ct)
    {
        try
        {
            var req = new GetPositionsRequest(TradingMode.PerpetualLinear, exParams);
            var result = await rest.GetPositionsAsync(exchange, req, ct).ConfigureAwait(false);

            if (!result.Success)
                return new { error = result.Error?.Message };

            return (result.Data ?? []).Select(p => new
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
            return new ExchangeParameters(new ExchangeParameter("Bitget", "ProductType", "UsdtFutures"));
        if (exchange.Equals("GateIo", StringComparison.OrdinalIgnoreCase)
            || exchange.Equals("GateIO", StringComparison.OrdinalIgnoreCase))
            return new ExchangeParameters(new ExchangeParameter("GateIo", "SettleAsset", "usdt"));
        return new ExchangeParameters();
    }

    private static string? HintForError(string? err)
    {
        if (string.IsNullOrEmpty(err)) return null;
        if (err.Contains("Multiple API", StringComparison.OrdinalIgnoreCase))
            return "Internal: TradingMode selection — retry after update";
        if (err.Contains("Invalid API", StringComparison.OrdinalIgnoreCase)
            || err.Contains("API-key", StringComparison.OrdinalIgnoreCase)
            || err.Contains("signature", StringComparison.OrdinalIgnoreCase))
            return "Check key/secret and that the key is active";
        if (err.Contains("IP", StringComparison.OrdinalIgnoreCase)
            || err.Contains("whitelist", StringComparison.OrdinalIgnoreCase))
            return "Add server IP to exchange API whitelist";
        if (err.Contains("permission", StringComparison.OrdinalIgnoreCase)
            || err.Contains("401", StringComparison.OrdinalIgnoreCase))
            return "Enable Futures read on the API key";
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
