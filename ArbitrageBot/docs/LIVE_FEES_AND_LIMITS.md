# Live fees & limits (4 venues, VIP0)

Research snapshot for **USDT-M / perpetual** base tier (no VIP). Bot uses **market → taker**.

| Exchange | Maker | Taker (use in bot) | Notes |
|----------|-------|--------------------|-------|
| Binance  | 0.020% | **0.050%** (0.045% with BNB) | Official USDⓈ-M schedule |
| Bybit    | 0.020% | **0.055%** | Official VIP0 perps |
| OKX      | 0.020% | **0.050%** | Futures regular tier |
| Bitget   | 0.020% | **0.060%** | Futures standard |

Round-trip (open 2 legs + close 2 legs) ≈ **0.20–0.24%** fees alone.

## Sized for ~$250 USDT futures per exchange

| Setting | Value |
|---------|-------|
| QuoteSize / LiveMaxNotionalUsd | 100 |
| Max open positions | 1 |
| Leverage | 3x |
| MinProfitPercent | 0.10% |
| Require RT edge + full fill | true |
| Live still default OFF / read-only | true |

Always re-check **My Fee Rate** on each exchange after KYC — regional rates can differ.
