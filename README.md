# ArbitrageBot (.NET 10)

Cross-exchange **FuturesCross** scanner + paper execution on live order books.

## Web Console (professional UI)

| Section | Purpose |
|---------|---------|
| **Dashboard** | KPIs, strategy, exchange health, top signals, recent trades |
| **Market** | Opportunities + multi-venue order books + bid/ask matrix |
| **Reports** | Margin, open hedges, full trade ledger, day PnL |
| **Settings** | Trading params + per-exchange API key forms (masked) |

Secrets: `ArbitrageBot/data/local-settings.json` (gitignored) or User Secrets. Never returned to the browser.

### Roadmap live

1. Paper on live books (current)
2. Read-only keys → balances
3. Trade keys + risk limits
4. Per-exchange enable

```bash
git pull origin Develop
cd ArbitrageBot && dotnet run
```

Open http://localhost:5050 — sidebar: Dashboard / Market / Reports / Settings.


## Production checklist

- [ ] Confirm `LiveTradingEnabled=false` and `LiveReadOnlyMode=true` until intentionally enabled
- [ ] On restart with open hedges: open `/live` → Refresh positions → reconcile against exchange
- [ ] Kill switch available on `/live` and `POST /api/live/kill`
- [ ] Health: `GET /api/health` and `GET /api/health/live` (no secrets)
- [ ] API keys only in `data/local-settings.json` or User Secrets — never in browser or logs
- [ ] Daily loss limit and max open positions configured before any live enable
- [ ] Confirm phrase required to enable live orders
- [ ] Paper mode validated on real books before small live size
