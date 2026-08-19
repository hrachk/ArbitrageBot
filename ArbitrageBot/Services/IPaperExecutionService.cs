using ArbitrageBot.Models;

namespace ArbitrageBot.Services;

public interface IPaperExecutionService
{
    void Initialize(IEnumerable<string> exchanges, IEnumerable<string> symbols);

    /// <summary>
    /// Try to execute opportunity against virtual balances using VWAP fills already computed.
    /// </summary>
    PaperTrade TryExecute(ArbitrageOpportunity opportunity);

    IReadOnlyList<PaperTrade> GetRecentTrades(int take = 50);
    IReadOnlyDictionary<string, Dictionary<string, decimal>> GetBalances();
    decimal RealizedPnlQuote { get; }
    int TradeCount { get; }
    int SuccessCount { get; }
    void Reset(IEnumerable<string> exchanges, IEnumerable<string> symbols);
}
