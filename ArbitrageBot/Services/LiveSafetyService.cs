using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ArbitrageBot.Configuration;
using Microsoft.Extensions.Options;

namespace ArbitrageBot.Services;

/// <summary>
/// Phase 4: pre-trade safety — healthy books, rate limits, venue allow-list, webhook alerts.
/// </summary>
public sealed class LiveSafetyService
{
    private readonly LiveTradingGuard _guard;
    private readonly ArbitrageOptions _options;
    private readonly IFuturesMarketService _futMarket;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<LiveSafetyService> _logger;

    private readonly ConcurrentDictionary<string, DateTime> _lastOrderUtc = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastGlobalOrderUtc = DateTime.MinValue;
    private readonly object _alertLock = new();
    private DateTime _lastAlertUtc = DateTime.MinValue;

    public LiveSafetyService(
        LiveTradingGuard guard,
        IOptions<ArbitrageOptions> options,
        IFuturesMarketService futMarket,
        IHttpClientFactory httpFactory,
        ILogger<LiveSafetyService> logger)
    {
        _guard = guard;
        _options = options.Value;
        _futMarket = futMarket;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public (bool ok, string reason) CanOpenLive(string symbol, string longEx, string shortEx, decimal notional)
    {
        var (ok, reason) = _guard.CheckOpenAllowed(
            // open count checked inside engine; here 0 placeholder — engine rechecks
            0, notional);
        if (!ok) return (false, reason);

        var allowed = _options.LiveAllowedExchanges;
        if (allowed is { Count: > 0 })
        {
            if (!allowed.Any(a => a.Equals(longEx, StringComparison.OrdinalIgnoreCase)))
                return (false, $"long venue {longEx} not in LiveAllowedExchanges");
            if (!allowed.Any(a => a.Equals(shortEx, StringComparison.OrdinalIgnoreCase)))
                return (false, $"short venue {shortEx} not in LiveAllowedExchanges");
        }

        var interval = Math.Max(500, _options.LiveMinOrderIntervalMs);
        var now = DateTime.UtcNow;
        if ((now - _lastGlobalOrderUtc).TotalMilliseconds < interval)
            return (false, $"global rate-limit {interval}ms");
        if (_lastOrderUtc.TryGetValue(longEx, out var tL) && (now - tL).TotalMilliseconds < interval)
            return (false, $"rate-limit {longEx}");
        if (_lastOrderUtc.TryGetValue(shortEx, out var tS) && (now - tS).TotalMilliseconds < interval)
            return (false, $"rate-limit {shortEx}");

        if (_options.LiveRequireHealthyBooks)
        {
            var st = _futMarket.ConnectionStatus;
            if (!BookHealthy(st, longEx, symbol))
                return (false, $"book not healthy {longEx}:{symbol}");
            if (!BookHealthy(st, shortEx, symbol))
                return (false, $"book not healthy {shortEx}:{symbol}");
        }

        return (true, "ok");
    }

    public void MarkOrderSent(string longEx, string shortEx)
    {
        var now = DateTime.UtcNow;
        _lastGlobalOrderUtc = now;
        _lastOrderUtc[longEx] = now;
        _lastOrderUtc[shortEx] = now;
    }

    public async Task AlertAsync(string title, string body, CancellationToken ct = default)
    {
        var url = _options.LiveAlertWebhookUrl;
        if (string.IsNullOrWhiteSpace(url)) return;

        lock (_alertLock)
        {
            if ((DateTime.UtcNow - _lastAlertUtc).TotalSeconds < 5) return;
            _lastAlertUtc = DateTime.UtcNow;
        }

        try
        {
            var client = _httpFactory.CreateClient("live-alerts");
            // Telegram bot API style: full URL includes token+chat, Discord expects {"content":"..."}
            var text = $"*[ArbitrageBot]* {title}\n{body}";
            if (url.Contains("discord", StringComparison.OrdinalIgnoreCase)
                || url.Contains("hooks.slack", StringComparison.OrdinalIgnoreCase)
                || url.Contains("discord.com", StringComparison.OrdinalIgnoreCase))
            {
                await client.PostAsJsonAsync(url, new { content = text }, ct).ConfigureAwait(false);
            }
            else if (url.Contains("api.telegram.org", StringComparison.OrdinalIgnoreCase))
            {
                // expects chat_id in URL query or we send text only if sendMessage URL complete
                await client.PostAsJsonAsync(url, new { text }, ct).ConfigureAwait(false);
            }
            else
            {
                await client.PostAsync(url,
                    new StringContent(JsonSerializer.Serialize(new { title, body, text, utc = DateTime.UtcNow }),
                        Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
            }
            _logger.LogInformation("Live alert sent: {Title}", title);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live alert webhook failed");
        }
    }

    private static bool BookHealthy(IReadOnlyDictionary<string, string> status, string exchange, string symbol)
    {
        var key = $"{exchange}:{symbol}";
        if (!status.TryGetValue(key, out var st) || string.IsNullOrEmpty(st))
        {
            // fallback: any key starting with exchange:
            var any = status.FirstOrDefault(kv =>
                kv.Key.StartsWith(exchange + ":", StringComparison.OrdinalIgnoreCase)
                && IsHealthyStatus(kv.Value));
            return !string.IsNullOrEmpty(any.Key);
        }
        return IsHealthyStatus(st);
    }

    private static bool IsHealthyStatus(string st) =>
        st.Contains("Synced", StringComparison.OrdinalIgnoreCase)
        || st.Contains("book-ticker", StringComparison.OrdinalIgnoreCase)
        || st.Equals("Connected", StringComparison.OrdinalIgnoreCase);
}
