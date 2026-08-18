# ArbitrageBot (.NET 10)

Межбиржевой арбитражный сканер на .NET 10 Worker Service.

## Возможности (текущая версия)

- Подключение к нескольким биржам через **CryptoClients.Net** (Binance, Bybit, OKX и др.)
- Получение Best Bid / Best Ask (book ticker)
- Расчёт gross и net спреда с учётом estimated taker fees
- Логирование в консоль + файл (`logs/arbitrage-YYYYMMDD.log`)
- Режим PaperTrading (по умолчанию включён)
- Конфигурация через `appsettings.json`

## Структура

```
ArbitrageBot/
├── Configuration/ArbitrageOptions.cs
├── Models/ArbitrageOpportunity.cs
├── Services/
│   ├── IMarketDataService.cs
│   └── MarketDataService.cs
├── ArbitrageWorker.cs          # BackgroundService
├── Program.cs
└── appsettings.json
```

## Запуск

```bash
cd ArbitrageBot
dotnet run
```

Или из корня solution:

```bash
dotnet run --project ArbitrageBot
```

## Конфигурация

В `appsettings.json` секция `Arbitrage`:

- `Symbols` — пары (BTCUSDT, ETHUSDT...)
- `Exchanges` — Binance, Bybit, OKX, Bitget, GateIo...
- `MinProfitPercent` — минимальный net profit после fees (%)
- `ScanIntervalMs` — интервал сканирования
- `PaperTrading` — true/false
- `EstimatedTakerFees` — комиссии по биржам (%)

## API ключи (позже)

Для публичных book ticker ключи **не нужны**.  
Когда будем делать реальные ордера и балансы — добавим через User Secrets:

```bash
dotnet user-secrets set "ExchangeCredentials:Binance:ApiKey" "YOUR_KEY"
dotnet user-secrets set "ExchangeCredentials:Binance:ApiSecret" "YOUR_SECRET"
# аналогично для Bybit
```

## Следующие шаги

1. WebSocket order books (вместо REST polling)
2. Учёт реальной глубины стакана (slippage)
3. Paper execution engine
4. Inventory / балансы
5. Real order placement + risk manager
