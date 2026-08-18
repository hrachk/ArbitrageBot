using ArbitrageBot;
using ArbitrageBot.Configuration;
using ArbitrageBot.Hubs;
using ArbitrageBot.Services;
using CryptoClients.Net;
using Microsoft.Extensions.Options;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting ArbitrageBot Web (Paper Execution)...");

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

    builder.Services.AddSingleton<ArbitrageState>();
    builder.Services.AddSingleton<ActiveMarketContext>();
    builder.Services.AddSingleton<ISymbolDiscoveryService, SymbolDiscoveryService>();
    builder.Services.AddSingleton<IOrderBookService, OrderBookService>();
    builder.Services.AddSingleton<IMarketDataService, MarketDataService>();
    builder.Services.AddSingleton<IPaperExecutionService, PaperExecutionService>();
    builder.Services.AddHostedService<ArbitrageWorker>();
    builder.Services.AddSignalR();

    var app = builder.Build();

    app.UseDefaultFiles();
    app.UseStaticFiles();

    app.MapHub<ArbitrageHub>("/hubs/arbitrage");
    app.MapGet("/api/snapshot", (ArbitrageState state) => Results.Json(state.GetSnapshot()));
    app.MapGet("/api/health", () => Results.Ok(new { status = "ok", utc = DateTime.UtcNow }));

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
        IOptions<ArbitrageOptions> options) =>
    {
        var opt = options.Value;
        paper.Reset(opt.NormalizedExchanges, opt.NormalizedSymbols);
        state.UpdatePaper(
            paper.RealizedPnlQuote,
            paper.TradeCount,
            paper.SuccessCount,
            paper.GetRecentTrades(40),
            paper.GetBalances());
        return Results.Ok(new { reset = true, balances = paper.GetBalances() });
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
