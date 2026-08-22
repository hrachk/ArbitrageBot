# Live trading roadmap (Live-Branch-1)

Canonical path from paper → real money. **Default remains PAPER.**

## Phase 0 — Paper gate (process)
- [ ] Paper equity Δ ≥ 0 under **realistic** rules (RT edge, full fill, min ~0.10%) for multiple days
- [ ] Review skip reasons / best net vs threshold
- **Do not enable live orders until this is honest-green**

## Phase 1 — Foundation (this branch) ✅
- [x] `LiveTradingEnabled = false` by default
- [x] `LiveReadOnlyMode = true` by default
- [x] `LiveTradingGuard` — enable phrase, kill switch, daily loss, max open/notional
- [x] API: `/api/live/status`, `/api/live/enable`, `/api/live/kill`, `/api/live/disable`, `/api/live/verify`
- [x] `ILiveExecutionService` stub — **no real orders**
- [x] Settings UI section: Live status + enable/kill

## Phase 2 — Read-only production ✅
- [x] Per-exchange balance/position fetch (USDT-M futures via CryptoClients)
- [x] UI: Reports → Live balances + Settings verify
- [x] Error hints for permission / IP whitelist
- [ ] Fee tier from account (optional later)

## Phase 3 — Live execution (orders) ✅ skeleton
- [x] Place dual-leg hedge (LONG + SHORT) via PlaceFuturesOrderAsync (market)
- [x] Short-leg fail → attempt unwind long
- [x] Close path (converge / stop / timeout / manual API)
- [x] Persist `data/live/trades-ledger.json`
- [x] Worker: live path only when `CanPlaceOrders`
- [ ] Harden partial fills / exchange-specific qty filters (next iteration)


## Phase 4 — Safety hardening
- [ ] Rate-limit guards per venue
- [ ] Disconnect / book stale → no new opens
- [ ] Telegram/webhook alerts on kill
- [ ] Separate API keys: trade without withdraw

## Phase 5 — Gradual launch
- [ ] 1 symbol, 2 venues, min size
- [ ] Raise limits only after stable fills

## Enable sequence (when ready)
1. Settings → store **read-only** keys → `/api/live/verify`
2. `POST /api/live/enable` with phrase + `readOnly: true`
3. Phase 2–3 code complete
4. New keys with **trade** (no withdraw) + IP whitelist
5. `POST /api/live/enable` with phrase + `readOnly: false`
6. Kill switch always one click away
