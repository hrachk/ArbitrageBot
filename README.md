# ArbitrageBot (.NET 10)

Межбиржевой арбитражный сканер с **live WebSocket order books** и Web Dashboard.

## Возможности

- **WebSocket order books** через `IExchangeOrderBookFactory` (локально синхронизируемые стаканы)
- Fallback на **book ticker WebSocket**, если factory недоступен
- Multi-exchange: Binance, Bybit, OKX, …
- Расчёт gross / net спреда после taker fees
- Фоновый Worker + **SignalR** live updates
- Современный тёмный Web UI (статус WS-соединений, opportunities, tickers)
- Paper mode по умолчанию
- REST `/api/snapshot`, `/api/health`

## Запуск

```bash
git pull origin Develop
cd ArbitrageBot
dotnet run
```

Открой: **http://localhost:5050**

## Структура

```
ArbitrageBot/
├── Hubs/ArbitrageHub.cs
├── Services/
│   ├── ArbitrageState.cs
│   ├── IOrderBookService.cs / OrderBookService.cs   # WS books
│   ├── IMarketDataService.cs / MarketDataService.cs
├── wwwroot/index.html
├── ArbitrageWorker.cs
└── Program.cs
```

## Конфигурация (`Arbitrage`)

| Key | Description |
|-----|-------------|
| Symbols | BTCUSDT, ETHUSDT, … |
| Exchanges | Binance, Bybit, OKX, … |
| MinProfitPercent | минимальный net % |
| ScanIntervalMs | как часто пересчитывать спреды (книги уже live) |
| PaperTrading | true / false |
| EstimatedTakerFees | комиссии % |

## Roadmap

1. ✅ Live Web UI + SignalR
2. ✅ WebSocket order books
3. Slippage / depth-aware profit
4. UI polish (filters, buttons, spread chart)
5. Paper Execution Engine
6. API keys

## Security

Не коммить API-ключи. User Secrets / env vars.
