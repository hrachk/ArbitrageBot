# ArbitrageBot — Futures Cross-Exchange

**StrategyMode: `FuturesCross`** (default)

LONG perpetual on the cheaper exchange + SHORT on the richer exchange.  
Only **USDT margin** on each venue — **no coin transfers**.

## How it works

1. Discover liquid USDT-M style symbols (or use static list)
2. WebSocket **perp order books** (Binance / Bybit / OKX)
3. Depth-aware VWAP spread, minus taker fees
4. **Paper**: open hedge when net % ≥ threshold; close on convergence or max hold
5. Live UI via SignalR

## Config (`appsettings.json`)

```json
"StrategyMode": "FuturesCross",
"MinProfitPercent": 0.08,
"QuoteSize": 500,
"FuturesPaperLeverage": 2,
"FuturesMaxOpenPositions": 3,
"FuturesMaxHoldMinutes": 30,
"FuturesCloseBelowNetPercent": 0.02,
"PaperAutoExecute": true
```

Spot inventory mode: `"StrategyMode": "SpotInventory"`.

## Run

```bash
git pull origin Develop
cd ArbitrageBot && dotnet run
```

http://localhost:5050

## Roadmap

- ✅ FuturesCross paper (open/close hedge)
- ⬜ Funding rate in net edge
- ⬜ Live API keys + risk limits
