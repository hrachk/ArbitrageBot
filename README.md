# ArbitrageBot (.NET 10)

Межбиржевой арбитражный сканер: WebSocket order books + depth-aware PnL + live Web UI.

## Цепочка (roadmap)

1. ✅ Live Web UI + SignalR  
2. ✅ WebSocket order books  
3. ✅ Depth-aware / slippage (VWAP)  
4. ✅ **UI polish** (filters, pause/resume, spread chart)  
5. ⬜ Paper Execution Engine  
6. ⬜ API keys + risk manager  

## Багфикс

Списки `Symbols` / `Exchanges` больше не дублируются (убраны default list initializers + `NormalizedSymbols` / `NormalizedExchanges`).

## UI

- Pause / Resume scan (`POST /api/control/toggle`)
- Фильтры: symbol, buy/sell exchange, min net %, full fill only
- Chart: best net % history (Chart.js)
- Depth-aware opportunities table + WS connection status

## Запуск

```bash
git pull origin Develop
cd ArbitrageBot
dotnet run
```

http://localhost:5050

## Конфиг (`Arbitrage`)

| Key | Default | Meaning |
|-----|---------|---------|
| Symbols | BTCUSDT… | пары |
| Exchanges | Binance, Bybit, OKX | биржи |
| QuoteSize | 500 | размер USDT для depth |
| MinProfitPercent | 0.15 | мин. net после fees+slip |
| ScanIntervalMs | 1500 | пересчёт |
| PaperTrading | true | без реальных ордеров |
