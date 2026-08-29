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
    builder.Services.AddSingleton<IPaperAnalyticsStore, PaperAnalyticsStore>();
    builder.Services.AddSingleton<RuntimeRiskConfig>();
    builder.Services.AddSingleton<LiveTradingGuard>();
    builder.Services.AddSingleton<LiveOrderEngine>();
    builder.Services.AddSingleton<LiveSafetyService>();
    builder.Services.AddHttpClient("live-alerts");
    builder.Services.AddSingleton<ILiveExecutionService, LiveExecutionService>();
    builder.Services.Configure<ExchangeCredentialsOptions>(
        builder.Configuration.GetSection(ExchangeCredentialsOptions.SectionName));
    builder.Services.AddHostedService<ArbitrageWorker>();
    builder.Services.AddHostedService<RealtimeBroadcastService>();
    builder.Services.AddSignalR();
    builder.Services.AddHttpClient("discovery");
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

    var app = builder.Build();

    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.UseAntiforgery();

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

    
    app.MapGet("/api/settings", (ISettingsStore store, RuntimeRiskConfig risk) =>
        Results.Json(store.GetPublicSettings(risk.Snapshot)));

    app.MapPost("/api/settings/trading", async (TradingUiSettings body, ISettingsStore store, RuntimeRiskConfig risk) =>
    {
        await store.SaveTradingAsync(body);
        risk.ApplyTrading(body);
        return Results.Ok(new { saved = true, appliedRuntime = true });
    });

    app.MapPost("/api/settings/risk", async (RiskUiSettings body, ISettingsStore store, RuntimeRiskConfig risk) =>
    {
        risk.ApplyRisk(body);
        // persist overlapping trading fields
        await store.SaveTradingAsync(new TradingUiSettings
        {
            StrategyMode = risk.Snapshot.StrategyMode,
            PaperTrading = risk.Snapshot.PaperTrading,
            PaperAutoExecute = risk.Snapshot.PaperAutoExecute,
            MinProfitPercent = risk.Snapshot.MinProfitPercent,
            QuoteSize = risk.Snapshot.QuoteSize,
            FuturesPaperLeverage = risk.Snapshot.FuturesPaperLeverage,
            FuturesMaxOpenPositions = risk.Snapshot.FuturesMaxOpenPositions,
            FuturesStopLossUsd = risk.Snapshot.FuturesStopLossUsd,
            FuturesDailyLossLimitUsd = risk.Snapshot.FuturesDailyLossLimitUsd
        });
        return Results.Ok(new { saved = true, appliedRuntime = true, risk = risk.Snapshot });
    });

    app.MapGet("/api/settings/risk", (RuntimeRiskConfig risk) => Results.Json(new
    {
        minProfitPercent = risk.Snapshot.MinProfitPercent,
        quoteSize = risk.Snapshot.QuoteSize,
        leverage = risk.Snapshot.FuturesPaperLeverage,
        maxOpenPositions = risk.Snapshot.FuturesMaxOpenPositions,
        maxHoldMinutes = risk.Snapshot.FuturesMaxHoldMinutes,
        closeBelowNetPercent = risk.Snapshot.FuturesCloseBelowNetPercent,
        maxMarginUsagePercent = risk.Snapshot.FuturesMaxMarginUsagePercent,
        maxNotionalUsd = risk.Snapshot.FuturesMaxNotionalUsd,
        stopLossUsd = risk.Snapshot.FuturesStopLossUsd,
        dailyLossLimitUsd = risk.Snapshot.FuturesDailyLossLimitUsd,
        paperCooldownMs = risk.Snapshot.PaperCooldownMs,
        paperRequireFullFill = risk.Snapshot.PaperRequireFullFill,
        requireRoundTripEdge = risk.Snapshot.FuturesRequireRoundTripEdge,
        includeFunding = risk.Snapshot.FuturesIncludeFunding
    }));

    app.MapPost("/api/paper/close/{tradeId:guid}", (
        Guid tradeId,
        IFuturesPaperService paper,
        IFuturesMarketService market) =>
    {
        var result = paper.ForceClose(tradeId, (symbol, longEx, shortEx) =>
        {
            var books = market.GetBookTickers(symbol);
            if (!books.TryGetValue(longEx, out var l) || !books.TryGetValue(shortEx, out var s))
                return null;
            if (l.BestBid <= 0 || s.BestAsk <= 0) return null;
            return (l.BestBid, s.BestAsk);
        });
        if (result == null) return Results.NotFound(new { error = "position not found" });
        return Results.Ok(result);
    });

    app.MapPost("/api/paper/close-all", (IFuturesPaperService paper) =>
    {
        var n = paper.ForceCloseAll();
        return Results.Ok(new { closed = n });
    });

    app.MapPost("/api/paper/prune-orphans", (IFuturesPaperService paper, ActiveMarketContext markets) =>
    {
        var n = paper.PruneOrphanPositions(markets.Symbols);
        return Results.Ok(new { pruned = n, activeSymbols = markets.Symbols });
    });

    app.MapDelete("/api/settings/exchanges/{name}", async (string name, ISettingsStore store, CancellationToken ct) =>
    {
        await store.ClearExchangeCredentialAsync(name, ct);
        return Results.Ok(new { ok = true, exchange = name, cleared = true });
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

    app.MapGet("/api/analytics/summary", (IPaperAnalyticsStore a) => Results.Json(a.GetLiveSummary()));
    app.MapGet("/api/analytics/events", (IPaperAnalyticsStore a, int take = 80) => Results.Json(a.GetRecentEvents(take)));
    app.MapGet("/api/analytics/skips", (IPaperAnalyticsStore a, int take = 40) => Results.Json(a.GetRecentSkips(take)));
    app.MapGet("/api/analytics/performance", (IPaperAnalyticsStore a, int days = 7) => Results.Json(a.GetPerformanceReport(days)));
    app.MapGet("/api/analytics/trades", (IPaperAnalyticsStore a, int take = 80) => Results.Json(a.GetTradeDetails(take)));
    app.MapGet("/api/analytics/days", (IPaperAnalyticsStore a, int maxDays = 14) => Results.Json(a.GetDaySummaries(maxDays)));

// ─── Live trading control (Phase 1: gate + verify, no orders) ───
    app.MapGet("/api/live/status", (LiveTradingGuard guard) => Results.Ok(guard.Status()));

    app.MapPost("/api/live/enable", async (HttpRequest req, LiveTradingGuard guard, IOptions<ArbitrageOptions> opt) =>
    {
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(req.Body);
        var root = doc.RootElement;
        var phrase = root.TryGetProperty("confirmPhrase", out var p) ? p.GetString() ?? "" : "";
        var readOnly = !root.TryGetProperty("readOnly", out var r) || r.ValueKind != System.Text.Json.JsonValueKind.False;
        var (ok, message) = guard.TryEnable(phrase, readOnly, opt.Value);
        return ok ? Results.Ok(new { ok, message, status = guard.Status() })
                  : Results.BadRequest(new { ok, message, status = guard.Status() });
    });

    app.MapPost("/api/live/disable", (LiveTradingGuard guard) =>
    {
        guard.Disable("api");
        return Results.Ok(guard.Status());
    });

    app.MapPost("/api/live/kill", async (HttpRequest req, LiveTradingGuard guard) =>
    {
        var reason = "manual kill";
        try
        {
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(req.Body);
            if (doc.RootElement.TryGetProperty("reason", out var r))
                reason = r.GetString() ?? reason;
        }
        catch { /* empty body ok */ }
        guard.Kill(reason);
        try
        {
            var safety = app.Services.GetRequiredService<LiveSafetyService>();
            _ = safety.AlertAsync("KILL SWITCH", reason, CancellationToken.None);
        }
        catch { /* ignore */ }
        return Results.Ok(guard.Status());
    });

    app.MapPost("/api/live/verify", async (ILiveExecutionService live, CancellationToken ct) =>
        Results.Ok(await live.VerifyCredentialsAsync(ct)));
    app.MapGet("/api/live/balances", async (ILiveExecutionService live, CancellationToken ct) =>
        Results.Ok(await live.GetLiveBalancesAsync(ct)));
    app.MapGet("/api/live/positions", (ILiveExecutionService live) => Results.Ok(live.GetLivePaperSnapshot()));
    app.MapPost("/api/live/close/{tradeId}", async (string tradeId, ILiveExecutionService live, CancellationToken ct) =>
        Results.Ok(await live.TryCloseHedgeAsync(tradeId, ct)));
    app.MapGet("/api/live/verify", async (ILiveExecutionService live, CancellationToken ct) =>
        Results.Ok(await live.VerifyCredentialsAsync(ct)));

    // Blazor terminal UI — root "/" = Dashboard
    //app.MapRazorComponents<ArbitrageBot.Components.App>()
    //    .AddInteractiveServerRenderMode();

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

