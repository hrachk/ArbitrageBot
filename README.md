# ArbitrageBot (.NET 10)

Межбиржевой арбитражный сканер с **live Web Dashboard**.

## Возможности

- Multi-exchange book tickers (Binance, Bybit, OKX, …) via **CryptoClients.Net**
- Расчёт gross / net спреда с учётом taker fees
- Фоновый Worker + **SignalR** live updates
- Современный тёмный Web UI (Tailwind)
- Paper mode по умолчанию
- REST `/api/snapshot` и `/api/health`

## Запуск

```bash
cd ArbitrageBot
dotnet run
```

Открой в браузере: **http://localhost:5050**

## Структура

```
ArbitrageBot/
├── Hubs/ArbitrageHub.cs          # SignalR
├── Services/
│   ├── ArbitrageState.cs         # shared in-memory state
│   ├── IMarketDataService.cs
│   └── MarketDataService.cs
├── wwwroot/index.html            # Live dashboard
├── ArbitrageWorker.cs
├── Program.cs
└── appsettings.json
```

## Конфигурация (`Arbitrage` section)

| Key | Description |
|-----|-------------|
| Symbols | BTCUSDT, ETHUSDT, … |
| Exchanges | Binance, Bybit, OKX, … |
| MinProfitPercent | минимальный net % |
| ScanIntervalMs | интервал скана |
| PaperTrading | true / false |
| EstimatedTakerFees | комиссии по биржам |

## Roadmap

1. ✅ Live Web UI + SignalR
2. WebSocket order books (depth)
3. Slippage / depth-aware profit
4. Paper Execution Engine
5. API keys (read-only → trade)
6. Risk manager + kill-switch

## Security

Не коммить API-ключи. Используй User Secrets / env vars.
