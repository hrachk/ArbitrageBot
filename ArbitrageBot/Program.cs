using ArbitrageBot;
using ArbitrageBot.Configuration;
using ArbitrageBot.Hubs;
using ArbitrageBot.Services;
using CryptoClients.Net;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting ArbitrageBot Web (WebSocket order books)...");

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
    builder.Services.AddSingleton<IOrderBookService, OrderBookService>();
    builder.Services.AddSingleton<IMarketDataService, MarketDataService>();
    builder.Services.AddHostedService<ArbitrageWorker>();
    builder.Services.AddSignalR();

    var app = builder.Build();

    app.UseDefaultFiles();
    app.UseStaticFiles();

    app.MapHub<ArbitrageHub>("/hubs/arbitrage");
    app.MapGet("/api/snapshot", (ArbitrageState state) => Results.Json(state.GetSnapshot()));
    app.MapGet("/api/health", () => Results.Ok(new { status = "ok", utc = DateTime.UtcNow }));
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
