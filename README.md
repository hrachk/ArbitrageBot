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
