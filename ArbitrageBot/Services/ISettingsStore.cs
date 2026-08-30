using ArbitrageBot.Configuration;

namespace ArbitrageBot.Services;

public interface ISettingsStore
{
    object GetPublicSettings(ArbitrageOptions arb);
    TradingUiSettings GetTrading();
    Task SaveTradingAsync(TradingUiSettings trading, CancellationToken ct = default);
    Task SaveExchangeCredentialAsync(string exchange, ExchangeCredential cred, CancellationToken ct = default);
    Task ClearExchangeCredentialAsync(string exchange, CancellationToken ct = default);
    ExchangeCredential? GetCredential(string exchange);
    IReadOnlyDictionary<string, object> GetMaskedExchanges();
}

/// <summary>
/// In-memory + file overlay at data/local-settings.json (gitignored).
/// Survives rebuild/restart when the file is kept on disk.
/// </summary>
public class SettingsStore : ISettingsStore
{
    private readonly object _lock = new();
    private readonly string _path;
    private readonly ILogger<SettingsStore> _logger;
    private Dictionary<string, ExchangeCredential> _creds = new(StringComparer.OrdinalIgnoreCase);
    private TradingUiSettings _trading = new();

    public SettingsStore(IWebHostEnvironment env, IConfiguration config, ILogger<SettingsStore> logger)
    {
        _logger = logger;
        _path = Path.Combine(env.ContentRootPath, "data", "local-settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        var section = config.GetSection(ExchangeCredentialsOptions.SectionName);
        var opts = section.Get<ExchangeCredentialsOptions>();
        if (opts?.Exchanges != null)
        {
            foreach (var kv in opts.Exchanges)
                _creds[kv.Key] = kv.Value;
        }

        LoadFileOverlay();
    }

    private void LoadFileOverlay()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("trading", out var tr))
                _trading = System.Text.Json.JsonSerializer.Deserialize<TradingUiSettings>(tr.GetRawText()) ?? _trading;
            if (doc.RootElement.TryGetProperty("exchanges", out var ex))
            {
                var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, ExchangeCredential>>(ex.GetRawText());
                if (map != null)
                    foreach (var kv in map)
                        _creds[kv.Key] = kv.Value;
            }
            _logger.LogInformation("Loaded local-settings.json (trading + {N} exchange keys)", _creds.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load local-settings.json");
        }
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        var payload = new
        {
            trading = _trading,
            exchanges = _creds,
            savedUtc = DateTime.UtcNow
        };
        var json = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var tmp = _path + ".tmp";
        await File.WriteAllTextAsync(tmp, json, ct);
        File.Copy(tmp, _path, true);
        try { File.Delete(tmp); } catch { /* ignore */ }
    }

    public TradingUiSettings GetTrading()
    {
        lock (_lock)
        {
            // return a copy so callers can't mutate store state
            var json = System.Text.Json.JsonSerializer.Serialize(_trading);
            return System.Text.Json.JsonSerializer.Deserialize<TradingUiSettings>(json) ?? new TradingUiSettings();
        }
    }

    public object GetPublicSettings(ArbitrageOptions arb)
    {
        var t = GetTrading();
        // Prefer persisted UI values; fall back to runtime arb snapshot
        return new
        {
            trading = new
            {
                strategyMode = !string.IsNullOrWhiteSpace(t.StrategyMode) ? t.StrategyMode : arb.StrategyMode,
                paperTrading = t.PaperTrading,
                paperAutoExecute = t.PaperAutoExecute,
                minProfitPercent = t.MinProfitPercent > 0 ? t.MinProfitPercent : arb.MinProfitPercent,
                quoteSize = t.QuoteSize > 0 ? t.QuoteSize : arb.QuoteSize,
                futuresPaperLeverage = t.FuturesPaperLeverage > 0 ? t.FuturesPaperLeverage : arb.FuturesPaperLeverage,
                futuresMaxOpenPositions = t.FuturesMaxOpenPositions > 0 ? t.FuturesMaxOpenPositions : arb.FuturesMaxOpenPositions,
                futuresStopLossUsd = t.FuturesStopLossUsd != 0 ? t.FuturesStopLossUsd : arb.FuturesStopLossUsd,
                futuresDailyLossLimitUsd = t.FuturesDailyLossLimitUsd != 0 ? t.FuturesDailyLossLimitUsd : arb.FuturesDailyLossLimitUsd,
                maxHoldMinutes = t.MaxHoldMinutes > 0 ? t.MaxHoldMinutes : arb.FuturesMaxHoldMinutes,
                closeBelowNetPercent = t.CloseBelowNetPercent,
                maxMarginUsagePercent = t.MaxMarginUsagePercent > 0 ? t.MaxMarginUsagePercent : arb.FuturesMaxMarginUsagePercent,
                maxNotionalUsd = t.MaxNotionalUsd > 0 ? t.MaxNotionalUsd : arb.FuturesMaxNotionalUsd,
                paperCooldownMs = t.PaperCooldownMs,
                paperRequireFullFill = t.PaperRequireFullFill,
                requireRoundTripEdge = t.RequireRoundTripEdge,
                includeFunding = t.IncludeFunding,
                // Live micro ($5/exchange default)
                liveEquityPerExchangeUsd = t.LiveEquityPerExchangeUsd > 0 ? t.LiveEquityPerExchangeUsd : arb.LiveEquityPerExchangeUsd,
                liveMarginUsageFraction = t.LiveMarginUsageFraction > 0 ? t.LiveMarginUsageFraction : arb.LiveMarginUsageFraction,
                liveMaxNotionalUsd = t.LiveMaxNotionalUsd > 0 ? t.LiveMaxNotionalUsd : arb.LiveMaxNotionalUsd,
                liveMaxOpenPositions = t.LiveMaxOpenPositions > 0 ? t.LiveMaxOpenPositions : arb.LiveMaxOpenPositions,
                liveStopLossUsd = t.LiveStopLossUsd != 0 ? t.LiveStopLossUsd : arb.LiveStopLossUsd,
                liveDailyLossLimitUsd = t.LiveDailyLossLimitUsd != 0 ? t.LiveDailyLossLimitUsd : arb.LiveDailyLossLimitUsd,
                scanIntervalMs = arb.ScanIntervalMs,
                dynamicSymbols = arb.DynamicSymbols,
                dynamicTopN = arb.DynamicTopN,
                exchanges = arb.NormalizedExchanges,
                symbols = arb.NormalizedSymbols
            },
            connections = GetMaskedExchanges(),
            securityNote = "API secrets never returned to browser. Stored in data/local-settings.json (gitignored)."
        };
    }

    public IReadOnlyDictionary<string, object> GetMaskedExchanges()
    {
        lock (_lock)
        {
            var known = new[] { "Binance", "Bybit", "OKX", "Bitget", "GateIo" };
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in known)
            {
                _creds.TryGetValue(name, out var c);
                result[name] = new
                {
                    enabled = c?.Enabled ?? false,
                    hasApiKey = !string.IsNullOrWhiteSpace(c?.ApiKey),
                    hasApiSecret = !string.IsNullOrWhiteSpace(c?.ApiSecret),
                    hasPassphrase = !string.IsNullOrWhiteSpace(c?.Passphrase),
                    apiKeyMasked = Mask(c?.ApiKey),
                    permission = c?.Permission ?? "read-only"
                };
            }
            return result;
        }
    }

    public ExchangeCredential? GetCredential(string exchange)
    {
        lock (_lock)
            return _creds.TryGetValue(exchange, out var c) ? c : null;
    }

    public async Task SaveTradingAsync(TradingUiSettings trading, CancellationToken ct = default)
    {
        lock (_lock) _trading = trading;
        await PersistAsync(ct);
        _logger.LogInformation(
            "Trading settings saved → local-settings.json (paper={Paper} equity={E}$ lev={L} liveMaxN={N})",
            trading.PaperTrading, trading.LiveEquityPerExchangeUsd, trading.FuturesPaperLeverage, trading.LiveMaxNotionalUsd);
    }

    public async Task SaveExchangeCredentialAsync(string exchange, ExchangeCredential cred, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_creds.TryGetValue(exchange, out var existing))
            {
                if (string.IsNullOrWhiteSpace(cred.ApiKey)) cred.ApiKey = existing.ApiKey;
                if (string.IsNullOrWhiteSpace(cred.ApiSecret)) cred.ApiSecret = existing.ApiSecret;
                if (string.IsNullOrWhiteSpace(cred.Passphrase)) cred.Passphrase = existing.Passphrase;
            }
            if (cred.ApiKey != null) cred.ApiKey = new string(cred.ApiKey.Where(c => !char.IsWhiteSpace(c)).ToArray());
            if (cred.ApiSecret != null) cred.ApiSecret = new string(cred.ApiSecret.Where(c => !char.IsWhiteSpace(c)).ToArray());
            if (cred.Passphrase != null) cred.Passphrase = cred.Passphrase.Trim();
            _creds[exchange] = cred;
        }
        await PersistAsync(ct);
        var finger = string.IsNullOrEmpty(cred.ApiKey) || cred.ApiKey.Length < 8
            ? "(none)" : cred.ApiKey[..4] + "…" + cred.ApiKey[^4..];
        _logger.LogInformation("Credentials updated for {Exchange} enabled={En} perm={Perm} key={Key}",
            exchange, cred.Enabled, cred.Permission, finger);
    }

    public async Task ClearExchangeCredentialAsync(string exchange, CancellationToken ct = default)
    {
        lock (_lock) _creds.Remove(exchange);
        await PersistAsync(ct);
        _logger.LogInformation("Credentials cleared for {Exchange}", exchange);
    }

    private static string Mask(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "";
        if (key.Length <= 8) return "****";
        return key[..4] + "…****…" + key[^4..];
    }
}
