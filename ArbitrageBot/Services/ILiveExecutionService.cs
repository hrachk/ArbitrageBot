using ArbitrageBot.Models;

namespace ArbitrageBot.Services;

public interface ILiveExecutionService
{
    Task<object> VerifyCredentialsAsync(CancellationToken ct = default);
    Task<object> GetLiveBalancesAsync(CancellationToken ct = default);
    Task<object> TryOpenHedgeAsync(LiveHedgeRequest request, CancellationToken ct = default);
    Task<object> TryCloseHedgeAsync(string tradeId, CancellationToken ct = default);
    Task<int> TryCloseConvergedAsync(
        Func<string, string, string, (decimal longBid, decimal shortAsk)?> getMarks,
        decimal closeBelowNetPercent,
        CancellationToken ct = default);
    IReadOnlyList<LiveHedgePosition> GetOpenPositions();
    object GetLivePaperSnapshot();
}

public sealed class LiveHedgeRequest
{
    public string Symbol { get; set; } = "";
    public string LongExchange { get; set; } = "";
    public string ShortExchange { get; set; } = "";
    public decimal NotionalUsd { get; set; }
    public decimal BaseQty { get; set; }
    public decimal? LongAsk { get; set; }
    public decimal? ShortBid { get; set; }
    public decimal Leverage { get; set; } = 3;
}
