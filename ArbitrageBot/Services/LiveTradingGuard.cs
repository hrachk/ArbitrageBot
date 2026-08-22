using System.Collections.Concurrent;
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

    public LiveTradingGuard(IOptions<ArbitrageOptions> options, ILogger<LiveTradingGuard> logger)
    {
        _logger = logger;
        var o = options.Value;
        _enabled = o.LiveTradingEnabled;
        _readOnly = o.LiveReadOnlyMode;
        _dailyLossLimit = o.LiveDailyLossLimitUsd;
        _maxOpen = Math.Max(1, o.LiveMaxOpenPositions);
        _maxNotional = o.LiveMaxNotionalUsd > 0 ? o.LiveMaxNotionalUsd : 200m;
        Audit("boot", new { _enabled, _readOnly, _dailyLossLimit, _maxOpen, _maxNotional });
        _logger.LogWarning(
            "LiveTradingGuard boot: enabled={E} readOnly={R} maxOpen={M} maxNotional={N} dayLimit={D}",
            _enabled, _readOnly, _maxOpen, _maxNotional, _dailyLossLimit);
    }

    public bool IsEnabled => _enabled && !_killed;
    public bool IsReadOnly => _readOnly || !_enabled;
    public bool IsKilled => _killed;
    public string? KillReason => _killReason;

    /// <summary>True only when live is on, not killed, not read-only.</summary>
    public bool CanPlaceOrders => _enabled && !_killed && !_readOnly;

    public object Status() => new
    {
        enabled = _enabled,
        readOnly = _readOnly,
        killed = _killed,
        killReason = _killReason,
        canPlaceOrders = CanPlaceOrders,
        dayRealizedUsd = _dayRealized,
        dailyLossLimitUsd = _dailyLossLimit,
        maxOpenPositions = _maxOpen,
        maxNotionalUsd = _maxNotional,
        phase = CanPlaceOrders ? "LIVE_ORDERS" : (_enabled && _readOnly ? "LIVE_READONLY" : (_killed ? "KILLED" : "PAPER_ONLY")),
        auditTail = _audit.Take(20).ToList()
    };

    public (bool ok, string message) TryEnable(string confirmPhrase, bool readOnly, ArbitrageOptions opts)
    {
        var expected = string.IsNullOrWhiteSpace(opts.LiveEnableConfirmPhrase)
            ? "ENABLE LIVE TRADING"
            : opts.LiveEnableConfirmPhrase;
        if (!string.Equals(confirmPhrase?.Trim(), expected, StringComparison.Ordinal))
            return (false, $"Confirm phrase mismatch. Type exactly: {expected}");

        lock (_lock)
        {
            _killed = false;
            _killReason = null;
            _enabled = true;
            _readOnly = readOnly;
            _dailyLossLimit = opts.LiveDailyLossLimitUsd;
            _maxOpen = Math.Max(1, opts.LiveMaxOpenPositions);
            _maxNotional = opts.LiveMaxNotionalUsd > 0 ? opts.LiveMaxNotionalUsd : 200m;
        }
        Audit("enable", new { readOnly, _maxOpen, _maxNotional, _dailyLossLimit });
        _logger.LogWarning("LIVE ENABLE requested. readOnly={R} canPlaceOrders={C}", _readOnly, CanPlaceOrders);
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
        }
        Audit("kill", new { reason });
        _logger.LogError("LIVE KILL SWITCH: {Reason}", reason);
    }

    public void RecordRealized(decimal pnl)
    {
        lock (_lock)
        {
            if (DateTime.UtcNow.Date != _dayUtc)
            {
                _dayUtc = DateTime.UtcNow.Date;
                _dayRealized = 0;
            }
            _dayRealized += pnl;
            if (_dailyLossLimit < 0 && _dayRealized <= _dailyLossLimit)
                Kill($"Daily loss limit {_dailyLossLimit:F0} hit (day={_dayRealized:F2})");
        }
    }

    public (bool ok, string reason) CheckOpenAllowed(int currentOpen, decimal notional)
    {
        if (!CanPlaceOrders)
            return (false, IsKilled ? $"killed: {_killReason}" : IsReadOnly ? "read-only mode" : "live disabled");
        if (currentOpen >= _maxOpen)
            return (false, $"max open {_maxOpen}");
        if (notional > _maxNotional)
            return (false, $"notional {notional:F0} > max {_maxNotional:F0}");
        if (_dailyLossLimit < 0 && _dayRealized <= _dailyLossLimit)
            return (false, "daily loss limit");
        return (true, "ok");
    }

    private void Audit(string type, object data)
    {
        _audit.Enqueue(new { utc = DateTime.UtcNow, type, data });
        while (_audit.Count > MaxAudit && _audit.TryDequeue(out _)) { }
    }
}
