using System.Collections.Concurrent;
using ArbitrageBot.Configuration;
using CryptoClients.Net;
using CryptoClients.Net.Models;
using CryptoExchange.Net.SharedApis;
using Microsoft.Extensions.Options;

namespace ArbitrageBot.Services;

/// <summary>
/// Phase 2: read-only futures balances + positions via CryptoClients unified REST.
/// Order placement remains Phase 3.
/// </summary>
public sealed class LiveExecutionService : ILiveExecutionService
{
    private readonly ISettingsStore _settings;
    private readonly LiveTradingGuard _guard;
    private readonly ArbitrageOptions _options;
    private readonly ILogger<LiveExecutionService> _logger;
    private readonly ConcurrentDictionary<string, (DateTime at, object data)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(20);

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

    public async Task<object> VerifyCredentialsAsync(CancellationToken ct = default)
        => await FetchAllAsync(includePositions: false, ct).ConfigureAwait(false);

    public async Task<object> GetLiveBalancesAsync(CancellationToken ct = default)
        => await FetchAllAsync(includePositions: true, ct).ConfigureAwait(false);

    public Task<object> TryOpenHedgeAsync(LiveHedgeRequest request, CancellationToken ct = default)
    {
        var check = _guard.CheckOpenAllowed(0, request.NotionalUsd);
        if (!check.ok)
            return Task.FromResult<object>(new { ok = false, phase = 2, error = check.reason });

        return Task.FromResult<object>(new
        {
            ok = false,
            phase = 2,
            error = "Phase 2 is read-only. Order placement is Phase 3 (see docs/LIVE_ROADMAP.md)."
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
            var missing = new
            {
                exchange,
                ok = false,
                hasKey = false,
                permission = (string?)null,
                error = "no api key/secret stored in Settings"
            };
            return (false, 0, missing);
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
                error = "passphrase required"
            });
        }

        try
        {
            var rest = new ExchangeRestClient();
            rest.SetApiCredentials(exchange, new DynamicCredentials(
                TradingMode.PerpetualLinear,
                cred.ApiKey!,
                cred.ApiSecret!,
                cred.Passphrase ?? "",
                ""));

            var exParams = BuildExchangeParameters(exchange);
            var balReq = new GetBalancesRequest(TradingMode.PerpetualLinear, exParams);
            var balResult = await rest.GetBalancesAsync(exchange, balReq, ct).ConfigureAwait(false);

            if (!balResult.Success)
            {
                // Fallback: unified / spot account type
                balReq = new GetBalancesRequest(SharedAccountType.Unified, exParams);
                balResult = await rest.GetBalancesAsync(exchange, balReq, ct).ConfigureAwait(false);
            }

            if (!balResult.Success)
            {
                balReq = new GetBalancesRequest(SharedAccountType.PerpetualLinearFutures, exParams);
                balResult = await rest.GetBalancesAsync(exchange, balReq, ct).ConfigureAwait(false);
            }

            if (!balResult.Success)
            {
                var err = balResult.Error?.Message ?? "balance request failed";
                _logger.LogWarning("Live balance {Ex}: {Err}", exchange, err);
                var fail = new
                {
                    exchange,
                    ok = false,
                    hasKey = true,
                    permission = cred.Permission,
                    error = err,
                    hint = err.Contains("permission", StringComparison.OrdinalIgnoreCase)
                           || err.Contains("API-key", StringComparison.OrdinalIgnoreCase)
                        ? "Check API key permissions (futures read) and IP whitelist"
                        : null
                };
                return (false, 0, fail);
            }

            var balances = (balResult.Data ?? [])
                .Select(b => new
                {
                    asset = b.Asset,
                    available = b.Available,
                    total = b.Total
                })
                .Where(b => b.total != 0 || b.available != 0)
                .OrderByDescending(b => b.total)
                .Take(30)
                .ToList();

            var usdt = balances
                .Where(b => string.Equals(b.asset, "USDT", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(b.asset, "USD", StringComparison.OrdinalIgnoreCase))
                .Sum(b => b.total);

            object? positions = null;
            if (includePositions)
            {
                try
                {
                    var posReq = new GetPositionsRequest(TradingMode.PerpetualLinear, exParams);
                    var posResult = await rest.GetPositionsAsync(exchange, posReq, ct).ConfigureAwait(false);
                    if (posResult.Success && posResult.Data != null)
                    {
                        positions = posResult.Data.Select(p => new
                        {
                            symbol = p.Symbol,
                            side = p.PositionSide.ToString(),
                            quantity = p.PositionSize,
                            entryPrice = p.AverageOpenPrice,
                            unrealizedPnl = p.UnrealizedPnl,
                            leverage = p.Leverage
                        }).Take(50).ToList();
                    }
                    else if (!posResult.Success)
                    {
                        positions = new { error = posResult.Error?.Message };
                    }
                }
                catch (Exception pex)
                {
                    positions = new { error = pex.Message };
                }
            }

            var payload = new
            {
                exchange,
                ok = true,
                hasKey = true,
                permission = cred.Permission,
                tradePermission = string.Equals(cred.Permission, "trade", StringComparison.OrdinalIgnoreCase),
                usdtTotal = usdt,
                balances,
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
                error = ex.Message
            });
        }
    }

    private static ExchangeParameters BuildExchangeParameters(string exchange)
    {
        // Match params we already use for public market data
        if (exchange.Equals("Bitget", StringComparison.OrdinalIgnoreCase))
            return new ExchangeParameters(new ExchangeParameter("Bitget", "ProductType", "UsdtFutures"));
        if (exchange.Equals("GateIo", StringComparison.OrdinalIgnoreCase)
            || exchange.Equals("GateIO", StringComparison.OrdinalIgnoreCase))
            return new ExchangeParameters(new ExchangeParameter("GateIo", "SettleAsset", "usdt"));
        return new ExchangeParameters();
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
