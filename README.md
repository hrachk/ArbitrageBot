# ArbitrageBot (.NET 10)

Inventory cross-exchange arbitrage (no transfers) + dynamic liquid pairs + paper execution + live UI.

## Strategy

**No asset transfers.** On each exchange the bot holds USDT + base inventory.  
When A is cheaper than B: **buy on A, sell on B** — balances shift, coins never leave the venues.

## Dynamic symbols

With `DynamicSymbols: true` (default):

1. Load all USDT tickers from Binance / Bybit / OKX  
2. Keep pairs listed on **all** exchanges  
3. Filter by min median 24h quote volume  
4. Rank (majors boost + volume) → take `DynamicTopN` (default 8)

Fallback: static `Symbols` list if discovery fails.

## Config

```json
"DynamicSymbols": true,
"DynamicTopN": 8,
"DynamicMinQuoteVolumeUsd": 5000000,
"DynamicQuoteAsset": "USDT",
"PaperAutoExecute": true,
"PaperStartingQuote": 10000
```

## Run

```bash
git pull origin Develop
cd ArbitrageBot && dotnet run
```

http://localhost:5050
