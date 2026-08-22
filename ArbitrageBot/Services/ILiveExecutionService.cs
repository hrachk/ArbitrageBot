namespace ArbitrageBot.Services;

public interface ILiveExecutionService
{
    Task<object> VerifyCredentialsAsync(CancellationToken ct = default);
    Task<object> GetLiveBalancesAsync(CancellationToken ct = default);
    /// <summary>Phase 2+: place hedge. Phase 1 always refuses unless CanPlaceOrders (still stub).</summary>
    Task<object> TryOpenHedgeAsync(LiveHedgeRequest request, CancellationToken ct = default);
    Task<object> TryCloseHedgeAsync(string tradeId, CancellationToken ct = default);
}

public sealed class LiveHedgeRequest
{
    public string Symbol { get; set; } = "";
    public string LongExchange { get; set; } = "";
    public string ShortExchange { get; set; } = "";
    public decimal NotionalUsd { get; set; }
    public decimal BaseQty { get; set; }
}
