# ArbitrageBot (.NET 10)

Межбиржевой арбитражный сканер: **WebSocket order books + depth-aware (slippage) PnL** + live Web UI.

## Цепочка (roadmap)

1. ✅ Live Web UI + SignalR  
2. ✅ WebSocket order books (`IExchangeOrderBookFactory` + book-ticker fallback)  
3. ✅ **Depth-aware / slippage** (VWAP walk по стакану на `QuoteSize`)  
4. ⬜ UI polish (filters, pause, spread chart)  
5. ⬜ Paper Execution Engine  
6. ⬜ API keys + risk manager  

## Как считается возможность

1. Best bid/ask из live WS books  
2. Walk asks (buy) и bids (sell) на сумму `QuoteSize` USDT → **VWAP**  
3. Fees (taker %) с обеих сторон  
4. `Net%` и `Net PnL (quote)` только если после slippage+fees ≥ `MinProfitPercent`  
5. `full` / `partial` — хватило ли глубины на полный размер  

## Запуск

```bash
git pull origin Develop
cd ArbitrageBot
dotnet run
```

UI: **http://localhost:5050**

## Конфиг (`Arbitrage`)

| Key | Default | Meaning |
|-----|---------|---------|
| Symbols | BTCUSDT… | пары |
| Exchanges | Binance, Bybit, OKX | биржи |
| QuoteSize | 500 | размер в USDT для depth |
| MinProfitPercent | 0.15 | мин. net после fees+slip |
| ScanIntervalMs | 1500 | пересчёт спредов |
| PaperTrading | true | без реальных ордеров |
| EstimatedTakerFees | 0.10 | % по биржам |

## Структура

```
Services/OrderBookService.cs   # WS books + EstimateFill
Services/MarketDataService.cs  # depth-aware scan
Services/ArbitrageState.cs     # snapshot for UI
wwwroot/index.html             # live dashboard
```
