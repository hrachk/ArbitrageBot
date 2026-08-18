# ArbitrageBot (.NET 10)

WebSocket order books + depth-aware arb scan + **Paper Execution Engine** + live UI.

## Roadmap

1. ✅ Live Web UI + SignalR  
2. ✅ WebSocket order books  
3. ✅ Depth-aware / slippage (VWAP)  
4. ✅ UI polish (filters, pause, chart)  
5. ✅ **Paper Execution Engine**  
6. ⬜ API keys + live risk  

## Paper mode

On each scan, if `PaperAutoExecute=true` and there is a qualifying opportunity:

1. Take best full-fill opp (optional `PaperRequireFullFill`)
2. Check virtual balances: USDT on buy exchange, base asset on sell exchange
3. Simulate fill at VWAP + fees
4. Update balances, realized PnL, trade history

**Starting inventory (default):**
- each exchange: `10_000 USDT`
- BTC `0.05`, ETH `0.5`, SOL `5` on each exchange

Cooldown between fills: `PaperCooldownMs` (default 8s).

**UI:** Paper Portfolio balances, Paper Trades, Reset Paper button.

**API:** `POST /api/paper/reset`

## Run

```bash
git pull origin Develop
cd ArbitrageBot
dotnet run
```

http://localhost:5050
