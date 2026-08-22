using ArbitrageBot.Configuration;
using Microsoft.Extensions.Options;

namespace ArbitrageBot.Services;

/// <summary>
/// Phase 1: inventory credentials + refuse readiness.
/// Real REST balance probes and order placement land in Phase 2–3.
/// </summary>
public sealed class LiveExecutionService : ILiveExecutionService
{
    private readonly ISettingsStore _settings;
    private readonly LiveTradingGuard _guard;
    private readonly ArbitrageOptions _options;
    private readonly ILogger<LiveExecutionService> _logger;

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
    {
        var results = new List<object>();
        foreach (var ex in _options.NormalizedExchanges)
        {
            var cred = _settings.GetCredential(ex);
            if (cred == null || string.IsNullOrWhiteSpace(cred.ApiKey) || string.IsNullOrWhiteSpace(cred.ApiSecret))
            {
                results.Add(new
                {
                    exchange = ex,
                    ok = false,
                    hasKey = false,
                    hasSecret = false,
                    permission = (string?)null,
                    error = "no api key/secret in Settings (data/local-settings.json)"
                });
                continue;
            }

            var needsPass = ex.Equals("OKX", StringComparison.OrdinalIgnoreCase)
                            || ex.Equals("Bitget", StringComparison.OrdinalIgnoreCase);
            var passOk = !needsPass || !string.IsNullOrWhiteSpace(cred.Passphrase);
            var trade = string.Equals(cred.Permission, "trade", StringComparison.OrdinalIgnoreCase);

            results.Add(new
            {
                exchange = ex,
                ok = passOk,
                hasKey = true,
                hasSecret = true,
                hasPassphrase = !string.IsNullOrWhiteSpace(cred.Passphrase),
                permission = cred.Permission,
                tradePermission = trade,
                error = passOk ? null : "passphrase required for this exchange",
                note = "Phase 1: local credential inventory only. Phase 2 will call exchange REST for balances."
            });
        }

        _logger.LogInformation("Live credential verify: {N} exchanges checked", results.Count);
        return Task.FromResult<object>(new
        {
            utc = DateTime.UtcNow,
            phase = 1,
            guard = _guard.Status(),
            exchanges = results
        });
    }

    public Task<object> GetLiveBalancesAsync(CancellationToken ct = default)
        => VerifyCredentialsAsync(ct);

    public Task<object> TryOpenHedgeAsync(LiveHedgeRequest request, CancellationToken ct = default)
    {
        var check = _guard.CheckOpenAllowed(0, request.NotionalUsd);
        if (!check.ok)
            return Task.FromResult<object>(new { ok = false, phase = 1, error = check.reason });

        _logger.LogWarning(
            "LIVE open blocked (Phase 1 stub): {Sym} L={L} S={S} notional={N}",
            request.Symbol, request.LongExchange, request.ShortExchange, request.NotionalUsd);

        return Task.FromResult<object>(new
        {
            ok = false,
            phase = 1,
            error = "Phase 1: order placement not implemented. Guard + credentials only. See docs/LIVE_ROADMAP.md Phase 3."
        });
    }

    public Task<object> TryCloseHedgeAsync(string tradeId, CancellationToken ct = default)
    {
        if (!_guard.CanPlaceOrders)
            return Task.FromResult<object>(new { ok = false, error = "cannot place orders" });
        return Task.FromResult<object>(new { ok = false, phase = 1, error = "Phase 1: close not implemented." });
    }
}
