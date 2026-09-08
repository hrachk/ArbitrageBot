namespace ArbitrageBot.Configuration;

/// <summary>
/// Alternative CEX + on-chain metric sources (Coinglass, CryptoQuant, Glassnode, …).
/// Section: "ExternalMetrics"
/// </summary>
public class ExternalMetricsOptions
{
    public const string SectionName = "ExternalMetrics";

    public bool Enabled { get; set; } = true;

    /// <summary>Poll interval for REST funding/OI snapshots.</summary>
    public int PollIntervalSeconds { get; set; } = 60;

    public CoinglassOptions Coinglass { get; set; } = new();
    public CryptoQuantOptions CryptoQuant { get; set; } = new();
    public GlassnodeOptions Glassnode { get; set; } = new();

    /// <summary>Telegram bot token for anomaly alerts (optional).</summary>
    public string? AlertTelegramBotToken { get; set; }
    public string? AlertTelegramChatId { get; set; }
    public string? AlertWebhookUrl { get; set; }
}

public class CoinglassOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>open-api.coinglass.com key (header CG-API-KEY).</summary>
    public string? ApiKey { get; set; }
    public string BaseUrl { get; set; } = "https://open-api.coinglass.com";
    /// <summary>Symbols to track (BTC, ETH, …) — base assets without USDT.</summary>
    public List<string> Bases { get; set; } = ["BTC", "ETH", "SOL", "XRP", "DOGE"];
}

public class CryptoQuantOptions
{
    public bool Enabled { get; set; }
    public string? ApiKey { get; set; }
    public string BaseUrl { get; set; } = "https://api.cryptoquant.com";
}

public class GlassnodeOptions
{
    public bool Enabled { get; set; }
    public string? ApiKey { get; set; }
    public string BaseUrl { get; set; } = "https://api.glassnode.com";
}
