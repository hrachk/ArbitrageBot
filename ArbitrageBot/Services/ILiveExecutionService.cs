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
    /// <summary>Ledger hedges + non-zero positions from each exchange API (truth for UI).</summary>
    Task<object> GetLivePositionsViewAsync(CancellationToken ct = default);
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
    /// <summary>Fair basis of this route, percent. Live must not treat it as capturable edge.</summary>
    public decimal FairBasisPercent { get; set; }
    public decimal ExitCrossPercent { get; set; }
    public decimal Leverage { get; set; } = 3;
}
