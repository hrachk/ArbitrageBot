namespace ArbitrageBot.Services.Metrics;

public interface IExternalMetricsHub
{
    AggregatedMetricsSnapshot GetSnapshot();
    Task RefreshAsync(CancellationToken ct = default);
}
