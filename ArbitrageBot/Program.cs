using ArbitrageBot;
using ArbitrageBot.Configuration;
using ArbitrageBot.Hubs;
using ArbitrageBot.Services;
using CryptoClients.Net;
using CryptoClients.Net.Interfaces;
using CryptoExchange.Net.SharedApis;
using Microsoft.Extensions.Options;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting ArbitrageBot Web (WS order books + SignalR realtime)...");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, services, lc) => lc
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(
            path: "logs/arbitrage-.log",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14));

    builder.Services.Configure<ArbitrageOptions>(
        builder.Configuration.GetSection(ArbitrageOptions.SectionName));

    builder.Services.AddCryptoClients(options =>
    {
        options.OutputOriginalData = false;
    });

    // Shared API exchange parameters (USDT-M) — required before discovery/WS
    CryptoExchange.Net.SharedApis.ExchangeParameters.SetStaticParameter("Bitget", "ProductType", "UsdtFutures");
    CryptoExchange.Net.SharedApis.ExchangeParameters.SetStaticParameter("BitGet", "ProductType", "UsdtFutures");
    CryptoExchange.Net.SharedApis.ExchangeParameters.SetStaticParameter("GateIo", "SettleAsset", "usdt");
    CryptoExchange.Net.SharedApis.ExchangeParameters.SetStaticParameter("GateIO", "SettleAsset", "usdt");

    builder.Services.AddSingleton<ArbitrageState>();
    builder.Services.AddSingleton<ActiveMarketContext>();
    builder.Services.AddSingleton<ISymbolDiscoveryService, SymbolDiscoveryService>();
    builder.Services.AddSingleton<IFuturesMarketService, FuturesMarketService>();
    builder.Services.AddSingleton<IFuturesPaperService, FuturesPaperService>();
    builder.Services.AddSingleton<IOrderBookService, OrderBookService>();
    builder.Services.AddSingleton<IMarketDataService, MarketDataService>();
    builder.Services.AddSingleton<IPaperExecutionService, PaperExecutionService>();
    builder.Services.AddSingleton<ISettingsStore, SettingsStore>();
    builder.Services.Configure<ExchangeCredentialsOptions>(
        builder.Configuration.GetSection(ExchangeCredentialsOptions.SectionName));
    builder.Services.AddHostedService<ArbitrageWorker>();
    builder.Services.AddHostedService<RealtimeBroadcastService>();
    builder.Services.AddSignalR();

    var app = builder.Build();

    app.UseDefaultFiles();
    app.UseStaticFiles();

    app.MapHub<ArbitrageHub>("/hubs/arbitrage");
    app.MapGet("/api/snapshot", (ArbitrageState state) => Results.Json(state.GetSnapshot()));
    app.MapGet("/api/health", (ArbitrageState state) =>
    {
        var snap = state.GetSnapshot();
        return Results.Ok(new { status = "ok", utc = DateTime.UtcNow, mode = state.Mode, snapshot = snap });
    });

    app.MapPost("/api/control/pause", (ArbitrageState state) =>
    {
        state.IsPaused = true;
        return Results.Ok(new { isPaused = true });
    });
    app.MapPost("/api/control/resume", (ArbitrageState state) =>
    {
        state.IsPaused = false;
        return Results.Ok(new { isPaused = false });
    });
    app.MapPost("/api/control/toggle", (ArbitrageState state) =>
    {
        state.IsPaused = !state.IsPaused;
        return Results.Ok(new { isPaused = state.IsPaused });
    });

    app.MapPost("/api/paper/reset", (
        ArbitrageState state,
        IPaperExecutionService paper,
        IFuturesPaperService futPaper,
        IOptions<ArbitrageOptions> options) =>
    {
        var opt = options.Value;
        if (opt.IsFuturesCross)
        {
            futPaper.Reset(opt.NormalizedExchanges);
            var margin = futPaper.GetMarginBalances();
            state.UpdatePaper(
                futPaper.RealizedPnlUsd,
                futPaper.TradeAttempts,
                futPaper.OpenCount,
                [],
                margin.ToDictionary(
                    kv => kv.Key,
                    kv => new Dictionary<string, decimal> { ["USDT"] = kv.Value },
                    StringComparer.OrdinalIgnoreCase));
            return Results.Ok(new { reset = true, mode = "FuturesCross", margin });
        }
        paper.Reset(opt.NormalizedExchanges, opt.NormalizedSymbols);
        state.UpdatePaper(
            paper.RealizedPnlQuote,
            paper.TradeCount,
            paper.SuccessCount,
            paper.GetRecentTrades(40),
            paper.GetBalances());
        return Results.Ok(new { reset = true, mode = "SpotInventory", balances = paper.GetBalances() });
    });

    
    app.MapGet("/api/settings", (ISettingsStore store, IOptions<ArbitrageOptions> opt) =>
        Results.Json(store.GetPublicSettings(opt.Value)));

    app.MapPost("/api/settings/trading", async (TradingUiSettings body, ISettingsStore store) =>
    {
        await store.SaveTradingAsync(body);
        return Results.Ok(new { saved = true });
    });

    app.MapPost("/api/settings/exchanges/{name}", async (string name, ExchangeCredential body, ISettingsStore store) =>
    {
        await store.SaveExchangeCredentialAsync(name, body);
        return Results.Ok(new { saved = true, exchange = name });
    });

    
    app.MapGet("/api/klines/{symbol}", async (
        string symbol,
        IExchangeRestClient rest,
        string? exchange,
        string interval = "15m",
        int limit = 120,
        CancellationToken ct = default) =>
    {
        exchange ??= "Binance";
        var baseAsset = symbol.ToUpperInvariant();
        if (baseAsset.EndsWith("USDT")) baseAsset = baseAsset[..^4];
        else if (baseAsset.EndsWith("USDC")) baseAsset = baseAsset[..^4];
        var shared = new CryptoExchange.Net.SharedApis.SharedSymbol(
            CryptoExchange.Net.SharedApis.TradingMode.PerpetualLinear, baseAsset, "USDT");

        var iv = interval.ToLowerInvariant() switch
        {
            "1m" => CryptoExchange.Net.SharedApis.SharedKlineInterval.OneMinute,
            "5m" => CryptoExchange.Net.SharedApis.SharedKlineInterval.FiveMinutes,
            "15m" => CryptoExchange.Net.SharedApis.SharedKlineInterval.FifteenMinutes,
            "1h" => CryptoExchange.Net.SharedApis.SharedKlineInterval.OneHour,
            "4h" => CryptoExchange.Net.SharedApis.SharedKlineInterval.FourHours,
            _ => CryptoExchange.Net.SharedApis.SharedKlineInterval.FifteenMinutes
        };

        var result = await rest.GetKlinesAsync(
            exchange,
            new CryptoExchange.Net.SharedApis.GetKlinesRequest(shared, iv),
            null,
            ct);

        if (!result.Success || result.Data == null)
        {
            // fallback Binance
            if (!exchange.Equals("Binance", StringComparison.OrdinalIgnoreCase))
            {
                result = await rest.GetKlinesAsync(
                    "Binance",
                    new CryptoExchange.Net.SharedApis.GetKlinesRequest(shared, iv),
                    null,
                    ct);
            }
        }

        if (!result.Success || result.Data == null)
            return Results.BadRequest(new { error = result.Error?.Message ?? "no klines" });

        var bars = result.Data
            .OrderBy(x => x.OpenTime)
            .TakeLast(Math.Clamp(limit, 20, 500))
            .Select(k => new
            {
                time = new DateTimeOffset(k.OpenTime).ToUnixTimeSeconds(),
                open = k.OpenPrice,
                high = k.HighPrice,
                low = k.LowPrice,
                close = k.ClosePrice
            });
        return Results.Json(new { exchange = result.Exchange, symbol, interval, bars });
    });

    app.MapFallbackToFile("index.html");

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
