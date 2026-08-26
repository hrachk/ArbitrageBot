# ArbitrageBot — Guide & Presentation

**Cross-exchange futures spatial arbitrage** · Paper → Live · .NET  
*Inventory hedge: long the cheap perpetual, short the rich — no coin transfers on the critical path.*

---

## 1. What this product is

ArbitrageBot watches **the same USDT-M perpetual** on several exchanges (Binance, Bybit, OKX, Bitget). When one venue is **cheaper** and another is **more expensive**, the bot can open a **delta-neutral hedge**:

| Leg | Action |
|-----|--------|
| **Long** | Buy perp on the cheaper exchange |
| **Short** | Sell perp on the richer exchange |

Profit comes when prices **converge** (spread shrinks), not from predicting direction of BTC.

**Paper mode** simulates fills, fees, margin and PnL on real WebSocket order books — without real orders.  
**Live mode** (phased, guarded) can place real hedges after you enable keys and confirm.

This is **execution engineering**, not a “get rich” signal feed. Edge is thin; discipline and costs decide who wins.

---

## 2. Who it is for

- Traders learning **cross-venue microstructure** with real books  
- Operators who want a **controlled paper lab** before small live size  
- Teams documenting **fees, depth, persistence, risk limits**

**Not for:** guaranteed daily returns, one-click passive income, or ignoring exchange risk.

---

## 3. Core idea (60 seconds)

1. **Pre-fund** each venue (demo or real). No withdrawal in the trade path.  
2. **Scan** multi-venue books for net edge after fees (and optional funding).  
3. **Open** only if depth can fill size, edge persists briefly, limits allow.  
4. **Hold** a short time; **close** when spread converges, time/stop hits, or manually.  
5. **Measure** win rate, skips, hold time — then tune.

```text
Gross width  −  open fees  −  close fees  −  slippage  ± funding  >  threshold
```

---

## 4. Architecture (simple)

```text
┌─────────────┐     WS books      ┌──────────────┐
│ Exchanges   │ ───────────────► │ Market layer │
│ BN / BY /   │     REST depth   │ Discovery    │
│ OKX / BG    │ ───────────────► │ Scan + gates │
└─────────────┘                  └──────┬───────┘
                                        │ opportunities
                               ┌────────▼────────┐
                               │ Paper / Live    │
                               │ execution       │
                               └────────┬────────┘
                                        │
                               ┌────────▼────────┐
                               │ Web UI + API    │
                               │ SignalR live    │
                               └─────────────────┘
```

| Module | Role |
|--------|------|
| **Symbol discovery** | Tickers + **depth score** at your trade size; refresh ~10 min |
| **Futures scan** | VWAP on notional, fees, RT edge, full fill, persistence |
| **Paper engine** | Virtual margin per venue, open/close, ledger |
| **Live guard** | Read-only → enable phrase → kill switch |
| **UI** | Dashboard, Market, Reports, Settings, Help |

---

## 5. Screens map

| Page | You see | You do |
|------|---------|--------|
| **Dashboard** | Mode, scans, signals, PnL, open hedges, spread map, **paper quality**, skips | Reset paper, read health |
| **Market** | Chart, multi-venue **overlay**, order books, positions | Pick symbol, Close hedge |
| **Reports** | Margin by venue, performance, history | Audit day results |
| **Settings** | Trading params, API keys, Live phase | Save params (runtime), keys |
| **Help** | This map + recommended settings | Onboard newcomers |

---

## 6. How to start (checklist)

### A. Install & run
```bash
git clone <repo> && cd ArbitrageBot
git checkout Live-Branch-1
dotnet restore && dotnet run
```
Open `http://127.0.0.1:<port>/` (see console for port).

### B. First session (paper only)
1. Confirm **Paper trading** + **Auto execute** in Settings.  
2. **Apply parameters** (see §7 recommended).  
3. **Reset paper** → balances = starting quote per exchange (e.g. 2500 USDT).  
4. Wait until heartbeat shows `books > 0` and discovery `http-tickers+depth`.  
5. Watch **Top signals** and **Open positions** — few trades is normal.

### C. Clean slate
Stop bot, delete `ArbitrageBot/data/paper/*` (open-state, ledger, events, daily), run again, Reset paper.  
Do **not** delete `local-settings.json` if you need saved keys/params.

---

## 7. Recommended settings (effective paper ≈ live rules)

| Parameter | Suggested | Notes |
|-----------|-----------|--------|
| Strategy | FuturesCross | Spatial perp hedge |
| Paper / Auto | On / On | Lab mode |
| Min open edge % | **0.08 – 0.10** | After open fees; RT still required |
| Quote / notional | **150 – 200** USDT | Fits ~2500/venue × 5× |
| Leverage | **5** | Cap inventory risk |
| Max open | **3 – 5** | Start 3 if learning |
| Max legs / venue | **3** | Avoid one-exchange pile-up |
| Full fill | **On** | No phantom size |
| Require RT edge | **On** | Open+close fees in gate |
| Funding | **On** | Perp realism |
| Stop / day limit | **−50 / −150** | Hard brakes |
| Max hold | **10 – 15** min | Spatial windows are short |
| Close width ≤ | **~0.015%** | Converge threshold |
| Discovery refresh | **10** min | In appsettings |
| Depth score | Automatic | Prefer books that fill size |
| Persist edge | **~1.5 s** | Anti-flash |

**Rule:** never lower thresholds only to “see more trades”. That destroys the lab’s value.

---

## 8. Reading the UI like a pro

| Signal | Meaning |
|--------|---------|
| **NET %** | Edge after open fees (not free money) |
| **EST $** | Rough ideal if exit were perfect now |
| **Unrealized** | Live mark vs entry — can be red while waiting for converge |
| **width vs entry** | Wider than entry → often worse for the hedge |
| **Δ % overlay** | Cheap venue → rich venue mid gap |
| **persistWait** | Edges seen but not held long enough |
| **skip reasons** | Why opens were blocked (depth, max pos, margin…) |
| **win rate** | From closed ledger — trust this more than open count |

---

## 9. Symbol selection (how pros + how we)

**Professionals:** multi-venue listing, **depth at size**, costs, avoid ultra-tight majors, rebalance universe often.

**ArbitrageBot:**
1. Public futures tickers (volume band, ≥2 venues, exclude BTC/ETH majors)  
2. Stratified mid liquidity + movers bias  
3. **Depth score** (Binance book sample vs QuoteSize)  
4. Refresh ~10 minutes  
5. Skip missing instruments per venue  

Correct direction for this strategy class — not HFT colocation, but sound.

---

## 10. Path to live (phased)

1. Paper **days** with realistic fees → stable process, positive *process* metrics  
2. API keys **read-only** → verify balances  
3. Small **trade** permission, no withdraw  
4. Live guard: read-only → confirm phrase → **1** position, tiny notional  
5. Kill switch known; hedge mode / leverage set on each exchange  
6. Scale only after live fills match paper assumptions  

Live is optional. Paper alone is a valid training product.

---

## 11. Risks (honest)

- Exchange / API / custody risk  
- Leg risk (one side fills worse)  
- Fees and funding dominate thin edges  
- Illusory ticker spreads without depth  
- Competition on liquid names  
- Software and ops errors  

Past paper PnL **does not** guarantee live PnL.

---

## 12. FAQ

**Why so few trades?**  
Threshold + RT + full fill + persistence. Markets are efficient; silence is data.

**Why unrealized negative after a good signal?**  
Spread can widen after entry; exit waits for converge or rules.

**Why same coins?**  
Depth + multi-venue filter; list still rotates on refresh when tickers work.

**books = 0?**  
WS not synced yet or geo/network — check exchange health on Dashboard.

**Close stuck?**  
Fixed path uses entry marks if books gone; or `POST /api/paper/close-all`.

---

## 13. One-page pitch

> **ArbitrageBot** turns fragmented crypto perpetual prices into a **disciplined, measurable** cross-exchange hedge workflow — with live books, depth-aware symbols, fee-aware gates, and a paper lab that behaves like real trading rules.  
> Learn the market. Measure skips and win rate. Only then size live.

---

*Document version aligned with Live-Branch-1 · FuturesCross · paper-first design.*
