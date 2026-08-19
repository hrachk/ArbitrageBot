namespace ArbitrageBot.Services;

/// <summary>
/// Runtime active markets (may differ from appsettings when DynamicSymbols is on).
/// </summary>
public class ActiveMarketContext
{
    private readonly object _lock = new();
    private List<string> _symbols = [];
    private List<string> _exchanges = [];
    private List<DiscoveredSymbol> _discovered = [];

    public IReadOnlyList<string> Symbols
    {
        get { lock (_lock) return _symbols.ToList(); }
    }

    public IReadOnlyList<string> Exchanges
    {
        get { lock (_lock) return _exchanges.ToList(); }
    }

    public IReadOnlyList<DiscoveredSymbol> Discovered
    {
        get { lock (_lock) return _discovered.ToList(); }
    }

    public void SetExchanges(IEnumerable<string> exchanges)
    {
        lock (_lock)
            _exchanges = exchanges.Where(e => !string.IsNullOrWhiteSpace(e))
                .Select(e => e.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    public void SetSymbols(IEnumerable<string> symbols, IEnumerable<DiscoveredSymbol>? meta = null)
    {
        lock (_lock)
        {
            _symbols = symbols.Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim().ToUpperInvariant())
                .Distinct()
                .ToList();
            if (meta != null)
                _discovered = meta.ToList();
        }
    }
}
