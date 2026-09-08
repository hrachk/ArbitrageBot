using ArbitrageBot.Configuration;
using Microsoft.Extensions.Options;

namespace ArbitrageBot.Services.Metrics;

/// <summary>
/// CryptoQuant / Glassnode exchange netflow stubs.
/// Wire real endpoints when API keys are present; never block trading loop.
/// </summary>
public sealed class OnChainMetricsClient
{
    private readonly HttpClient _http;
    private readonly ExternalMetricsOptions _opt;
    private readonly ILogger<OnChainMetricsClient> _log;

    public OnChainMetricsClient(HttpClient http, IOptions<ExternalMetricsOptions> opt, ILogger<OnChainMetricsClient> log)
    {
        _http = http;
        _opt = opt.Value;
        _log = log;
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<List<ExchangeFlowSnapshot>> FetchFlowsAsync(CancellationToken ct)
    {
        var list = new List<ExchangeFlowSnapshot>();
        if (_opt.CryptoQuant.Enabled && !string.IsNullOrWhiteSpace(_opt.CryptoQuant.ApiKey))
        {
            try
            {
                // Placeholder path — replace with product-specific CQ endpoints when key is live
                _log.LogDebug("CryptoQuant key present — flow endpoints pending product mapping");
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "CryptoQuant");
            }
        }
        if (_opt.Glassnode.Enabled && !string.IsNullOrWhiteSpace(_opt.Glassnode.ApiKey))
        {
            try
            {
                _log.LogDebug("Glassnode key present — exchange netflow pending product mapping");
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Glassnode");
            }
        }
        await Task.CompletedTask;
        return list;
    }
}
