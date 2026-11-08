using System.Collections.Concurrent;
using System.Text.Json;
using ArbitrageBot.Configuration;
using Microsoft.Extensions.Options;

namespace ArbitrageBot.Services;

/// <summary>
/// Canonical live gate: OFF by default, kill-switch, daily limits, audit log.
/// No orders may be sent unless CanPlaceOrders is true.
/// </summary>
public sealed class LiveTradingGuard
{
    private readonly ILogger<LiveTradingGuard> _logger;
    private readonly string _statePath;
    private readonly object _lock = new();
    private readonly ConcurrentQueue<object> _audit = new();
    private const int MaxAudit = 200;

    private bool _enabled;
    private bool _readOnly = true;
    private bool _killed;
    private string? _killReason;
    private decimal _dayRealized;
    private DateTime _dayUtc = DateTime.UtcNow.Date;
    private decimal _dailyLossLimit = -50m;
    private int _maxOpen = 1;
    private decimal _maxNotional = 200m;
    private int _additionalOpenPositions;

    public LiveTradingGuard(IOptions<ArbitrageOptions> options, IWebHostEnvironment env, ILogger<LiveTradingGuard> logger)
    {
        _logger = logger;
        _statePath = Path.Combine(env.ContentRootPath, "data", "live", "risk-state.json");
        var o = options.Value;
        _enabled = o.LiveTradingEnabled;
        _readOnly = o.LiveReadOnlyMode;
        _dailyLossLimit = o.LiveDailyLossLimitUsd;
        _maxOpen = Math.Max(1, o.LiveMaxOpenPositions);
        _maxNotional = o.LiveMaxNotionalUsd > 0 ? o.LiveMaxNotionalUsd : 200m;
        RestoreRiskState();
        Audit("boot", new { _enabled, _readOnly, _dailyLossLimit, _maxOpen, _maxNotional });
        _logger.LogWarning(
            "LiveTradingGuard boot: enabled={E} readOnly={R} maxOpen={M} maxNotional={N} dayLimit={D}",
            _enabled, _readOnly, _maxOpen, _maxNotional, _dailyLossLimit);
    }

    public bool IsEnabled { get { lock (_lock) return _enabled && !_killed; } }
    public bool IsReadOnly { get { lock (_lock) return _readOnly || !_enabled; } }
    public bool IsKilled { get { lock (_lock) return _killed; } }
    public string? KillReason { get { lock (_lock) return _killReason; } }
    public int AdditionalOpenPositions => Volatile.Read(ref _additionalOpenPositions);
    public void SetAdditionalOpenPositions(int count) => Volatile.Write(ref _additionalOpenPositions, Math.Max(0, count));

    /// <summary>True only when live is on, not killed, not read-only.</summary>
    public bool CanPlaceOrders { get { lock (_lock) return _enabled && !_killed && !_readOnly; } }

    public object Status()
    {
        lock (_lock)
        {
            RollDayUnlocked();
            var canPlace = _enabled && !_killed && !_readOnly;
            return new
            {
                enabled = _enabled,
                readOnly = _readOnly,
                killed = _killed,
                killReason = _killReason,
                canPlaceOrders = canPlace,
                dayRealizedUsd = _dayRealized,
                dailyLossLimitUsd = _dailyLossLimit,
                maxOpenPositions = _maxOpen,
                maxNotionalUsd = _maxNotional,
                phase = canPlace ? "LIVE_ORDERS" : (_enabled && _readOnly ? "LIVE_READONLY" : (_killed ? "KILLED" : "PAPER_ONLY")),
                auditTail = _audit.Take(20).ToList()
            };
        }
    }

    /// <summary>Settings save updates the notional ceiling without toggling live.</summary>
    public void ApplyLimits(decimal maxNotional, int maxOpen, decimal dailyLossLimit)
    {
        lock (_lock)
        {
            RollDayUnlocked();
            if (maxNotional > 0) _maxNotional = maxNotional;
            if (maxOpen > 0) _maxOpen = maxOpen;
            if (dailyLossLimit != 0) _dailyLossLimit = dailyLossLimit;
            PersistRiskStateUnlocked();
        }
    }

    public (bool ok, string message) TryEnable(string confirmPhrase, bool readOnly, ArbitrageOptions opts)
    {
        var expected = string.IsNullOrWhiteSpace(opts.LiveEnableConfirmPhrase)
            ? "ENABLE LIVE TRADING"
            : opts.LiveEnableConfirmPhrase;
        if (!string.Equals(confirmPhrase?.Trim(), expected, StringComparison.Ordinal))
            return (false, $"Confirm phrase mismatch. Type exactly: {expected}");

        lock (_lock)
        {
            RollDayUnlocked();
            if (_dailyLossLimit < 0 && _dayRealized <= _dailyLossLimit)
                return (false, $"Live enable rejected: daily loss limit {_dailyLossLimit:F0} already reached ({_dayRealized:F2})");
            _killed = false;
            _killReason = null;
            _enabled = true;
            _readOnly = readOnly;
            _dailyLossLimit = opts.LiveDailyLossLimitUsd;
            _maxOpen = Math.Max(1, opts.LiveMaxOpenPositions);
            _maxNotional = opts.LiveMaxNotionalUsd > 0 ? opts.LiveMaxNotionalUsd : 200m;
            PersistRiskStateUnlocked();
        }
        Audit("enable", new { readOnly, _maxOpen, _maxNotional, _dailyLossLimit });
        _logger.LogWarning("LIVE ENABLE requested. readOnly={R} canPlaceOrders={C}", _readOnly, CanPlaceOrders);
        if (IsKilled)
            return (false, "Live enable rejected because risk state could not be persisted");
        return (true, readOnly
            ? "Live READ-ONLY enabled — balances may be fetched; NO orders."
            : "Live ORDERS enabled — real futures orders may be placed. Kill switch available.");
    }

    public void Disable(string reason = "manual")
    {
        lock (_lock)
        {
            _enabled = false;
            _readOnly = true;
            PersistRiskStateUnlocked();
        }
        Audit("disable", new { reason });
        _logger.LogWarning("LIVE disabled: {Reason}", reason);
    }

    public void Kill(string reason)
    {
        lock (_lock)
        {
            _killed = true;
            _killReason = reason;
            _enabled = false;
            _readOnly = true;
            PersistRiskStateUnlocked();
        }
        Audit("kill", new { reason });
        _logger.LogError("LIVE KILL SWITCH: {Reason}", reason);
    }

    public void RecordRealized(decimal pnl)
    {
        lock (_lock)
        {
            RollDayUnlocked();
            _dayRealized += pnl;
            if (_dailyLossLimit < 0 && _dayRealized <= _dailyLossLimit)
                Kill($"Daily loss limit {_dailyLossLimit:F0} hit (day={_dayRealized:F2})");
            else
                PersistRiskStateUnlocked();
        }
    }

    public (bool ok, string reason) CheckOpenAllowed(int currentOpen, decimal notional)
    {
        lock (_lock)
        {
            RollDayUnlocked();
            if (!_enabled || _killed || _readOnly)
                return (false, _killed ? $"killed: {_killReason}" : (_readOnly || !_enabled ? "read-only mode" : "live disabled"));
            if (currentOpen >= _maxOpen)
                return (false, $"max open {_maxOpen}");
            if (notional > _maxNotional)
                return (false, $"notional {notional:F0} > max {_maxNotional:F0}");
            if (_dailyLossLimit < 0 && _dayRealized <= _dailyLossLimit)
                return (false, "daily loss limit");
            return (true, "ok");
        }
    }

    private void Audit(string type, object data)
    {
        _audit.Enqueue(new { utc = DateTime.UtcNow, type, data });
        while (_audit.Count > MaxAudit && _audit.TryDequeue(out _)) { }
    }

    private void RestoreRiskState()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            var saved = JsonSerializer.Deserialize<LiveRiskState>(File.ReadAllText(_statePath));
            if (saved == null)
                throw new InvalidDataException("Persisted live risk state is empty");
            if (saved.DayUtc == default)
                throw new InvalidDataException("Persisted live risk state is empty or invalid");
            if (saved.DayUtc.Date == DateTime.UtcNow.Date)
            {
                _dayUtc = saved.DayUtc.Date;
                _dayRealized = saved.DayRealizedUsd;
            }
            _killed = saved.Killed;
            _killReason = saved.KillReason;
            if (DateTime.UtcNow.Date != saved.DayUtc.Date)
            {
                _dayUtc = DateTime.UtcNow.Date;
                _dayRealized = 0m;
                if (_killReason?.StartsWith("Daily loss limit", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _killed = false;
                    _killReason = null;
                }
            }
            PersistRiskStateUnlocked();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not restore persisted live risk state; live trading is disabled");
            _killed = true;
            _killReason = "risk state restore failed";
            _enabled = false;
            _readOnly = true;
        }
    }

    private void PersistRiskStateUnlocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var tempPath = _statePath + ".tmp";
            var json = JsonSerializer.Serialize(new LiveRiskState(_dayUtc, _dayRealized, _killed, _killReason));
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _statePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not persist live risk state");
            _killed = true;
            _killReason = "risk state persistence failed";
            _enabled = false;
            _readOnly = true;
        }
    }

    private void RollDayUnlocked()
    {
        var today = DateTime.UtcNow.Date;
        if (_dayUtc.Date == today) return;
        _dayUtc = today;
        _dayRealized = 0m;
        PersistRiskStateUnlocked();
    }

    private sealed record LiveRiskState(DateTime DayUtc, decimal DayRealizedUsd, bool Killed, string? KillReason);
}
