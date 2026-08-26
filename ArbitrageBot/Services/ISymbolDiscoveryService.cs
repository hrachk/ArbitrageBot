namespace ArbitrageBot.Services;

public interface ISymbolDiscoveryService
{
    Task<DiscoveryResult> DiscoverAsync(
        IReadOnlyList<string> exchanges,
        CancellationToken ct = default);
}

public record DiscoveryResult
{
    public required IReadOnlyList<DiscoveredSymbol> Symbols { get; init; }
    /// <summary>dynamic | partial-dynamic | curated-futures | config-fallback</summary>
    public required string Source { get; init; }
    public string? Message { get; init; }
}

public record DiscoveredSymbol
{
    public required string Symbol { get; init; }
    public required string BaseAsset { get; init; }
    public required string QuoteAsset { get; init; }
    public decimal MedianQuoteVolume { get; init; }
    public int ExchangeCount { get; init; }
    public IReadOnlyList<string> Exchanges { get; init; } = [];
    /// <summary>Min(bid,ask) quote depth near top of book (USDT), Binance sample.</summary>
    public decimal DepthNotionalUsd { get; init; }
    /// <summary>DepthNotional / target trade size (≥1 = fills QuoteSize comfortably).</summary>
    public decimal DepthScore { get; init; }
}
