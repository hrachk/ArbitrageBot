using ArbitrageBot;
using ArbitrageBot.Configuration;
using ArbitrageBot.Services;
using CryptoClients.Net;
using Serilog;

// Serilog bootstrap
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting ArbitrageBot...");

    var builder = Host.CreateApplicationBuilder(args);

    // Serilog
    builder.Services.AddSerilog((services, lc) => lc
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console(
            outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(
            path: "logs/arbitrage-.log",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"));

    // Configuration
    builder.Services.Configure<ArbitrageOptions>(
        builder.Configuration.GetSection(ArbitrageOptions.SectionName));

    // CryptoClients.Net - unified multi-exchange access
    builder.Services.AddCryptoClients(options =>
    {
        options.OutputOriginalData = false;
    });

    // Our services
    builder.Services.AddSingleton<IMarketDataService, MarketDataService>();
    builder.Services.AddHostedService<ArbitrageWorker>();

    var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
