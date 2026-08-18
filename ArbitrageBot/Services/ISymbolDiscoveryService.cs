namespace ArbitrageBot.Services;

public interface ISymbolDiscoveryService
{
    /// <summary>
    /// Discover top liquid USDT pairs present on all configured exchanges.
    /// Returns symbols like BTCUSDT ranked by median quote volume.
    /// </summary>
    Task<IReadOnlyList<DiscoveredSymbol>> DiscoverAsync(
        IReadOnlyList<string> exchanges,
        CancellationToken ct = default);
}

public record DiscoveredSymbol
{
    public required string Symbol { get; init; }       // BTCUSDT
    public required string BaseAsset { get; init; }    // BTC
    public required string QuoteAsset { get; init; }   // USDT
    public decimal MedianQuoteVolume { get; init; }
    public int ExchangeCount { get; init; }
    public IReadOnlyList<string> Exchanges { get; init; } = [];
}
