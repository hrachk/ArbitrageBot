namespace ArbitrageBot.Models;

public sealed class LiveHedgePosition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Symbol { get; set; } = "";
    public string LongExchange { get; set; } = "";
    public string ShortExchange { get; set; } = "";
    public decimal BaseQty { get; set; }
    public decimal NotionalUsd { get; set; }
    public decimal LongEntry { get; set; }
    public decimal ShortEntry { get; set; }
    public string? LongOrderId { get; set; }
    public string? ShortOrderId { get; set; }
    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
    public bool IsOpen { get; set; } = true;
    public string Status { get; set; } = "Open";
    public string? Message { get; set; }
    public decimal? RealizedPnlUsd { get; set; }
    public DateTime? ClosedAt { get; set; }
}

public sealed class LiveLedgerFile
{
    public List<LiveHedgePosition> Positions { get; set; } = [];
    public List<LiveHedgePosition> Closed { get; set; } = [];
}
