using System.Collections.Concurrent;

namespace ArbitrageBot.Services;

/// <summary>
/// Which symbols the hedge is about to trade. The scalper never trades a symbol the hedge
/// holds or is entering, and the hedge never enters a symbol the scalper holds: on a one-way
/// futures account two strategies on the same symbol and venue would net each other's legs.
/// </summary>
public sealed class ExposureRegistry
{
    private readonly ConcurrentDictionary<string, DateTime> _arbUntil = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reserve a symbol for the hedge for a short window (an order in flight).</summary>
    public void MarkArb(string symbol, TimeSpan forHowLong)
        => _arbUntil[symbol] = DateTime.UtcNow + forHowLong;

    public void ReleaseArb(string symbol) => _arbUntil.TryRemove(symbol, out _);

    public bool IsArbReserved(string symbol)
        => _arbUntil.TryGetValue(symbol, out var until) && until > DateTime.UtcNow;
}
