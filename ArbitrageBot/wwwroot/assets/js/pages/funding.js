AB.pages = AB.pages || {};
AB.pages.funding = {
  _timer: null,
  async load() {
    const el = document.getElementById('fundingList');
    if (!el) return;
    try {
      const data = await AB.api.get('/api/funding');
      const rows = Array.isArray(data) ? data : (data.rates || []);
      let all = [];
      try { all = await AB.api.get('/api/funding/all'); } catch (_) {}
      if (!Array.isArray(all)) all = [];

      // Dynamic venue set from data + snapshot exchanges
      const venueSet = new Set();
      all.forEach(s => {
        const ex = s.exchange || s.Exchange;
        if (ex) venueSet.add(ex);
      });
      const snapEx = (AB.state.snapshot && AB.state.snapshot.exchanges) || [];
      snapEx.forEach(e => venueSet.add(e));
      const preferred = ['Binance', 'Bybit', 'OKX', 'Bitget', 'GateIo', 'Kucoin'];
      const venues = [
        ...preferred.filter(v => venueSet.has(v) || true),
        ...[...venueSet].filter(v => !preferred.includes(v))
      ].filter((v, i, a) => a.indexOf(v) === i);

      const bySym = {};
      all.forEach(s => {
        const sym = s.symbol || s.Symbol;
        const ex = s.exchange || s.Exchange;
        if (!sym || !ex) return;
        if (!bySym[sym]) bySym[sym] = {};
        bySym[sym][ex] = {
          rate: Number(s.rate != null ? s.rate : s.Rate) || 0,
          next: s.nextFundingUtc || s.NextFundingUtc || null,
          fetched: s.fetchedUtc || s.FetchedUtc || null
        };
      });

      // KPIs
      const pairN = new Set([...rows.map(r => r.symbol || r.Symbol), ...Object.keys(bySym)]).size;
      if (AB.$('f_pairs')) AB.$('f_pairs').textContent = String(pairN);
      if (AB.$('f_venues')) AB.$('f_venues').textContent = String(venues.length);
      let best = null;
      rows.forEach(f => {
        const d = Number(f.deltaRate != null ? f.deltaRate : f.delta) || 0;
        if (best == null || Math.abs(d) > Math.abs(best)) best = d;
      });
      if (AB.$('f_best')) {
        AB.$('f_best').textContent = best == null ? '—' : ((best >= 0 ? '+' : '') + (best * 100).toFixed(4) + '%');
        AB.$('f_best').className = 'kpi-v ' + (best > 0 ? 'pos' : (best < 0 ? 'neg' : ''));
      }
      // soonest next funding
      let nextSoon = null;
      all.forEach(s => {
        const n = s.nextFundingUtc || s.NextFundingUtc;
        if (!n) return;
        const t = new Date(n).getTime();
        if (!Number.isFinite(t) || t < Date.now()) return;
        if (nextSoon == null || t < nextSoon) nextSoon = t;
      });
      if (AB.$('f_next')) {
        if (nextSoon == null) AB.$('f_next').textContent = '—';
        else {
          const sec = Math.max(0, Math.round((nextSoon - Date.now()) / 1000));
          const h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60);
          AB.$('f_next').textContent = h + 'h ' + m + 'm';
        }
      }

      if (!rows.length && !Object.keys(bySym).length) {
        el.innerHTML = '<div class="empty">No funding snapshots yet — poll every ~5 min after books/universe ready</div>';
      } else {
        const sorted = rows.slice().sort((a, b) => {
          const da = Math.abs(Number(a.deltaRate != null ? a.deltaRate : a.delta) || 0);
          const db = Math.abs(Number(b.deltaRate != null ? b.deltaRate : b.delta) || 0);
          return db - da;
        });
        el.innerHTML = sorted.map(f => {
          const delta = Number(f.deltaRate != null ? f.deltaRate : f.delta) || 0;
          const apr = Number(f.annualizedApr != null ? f.annualizedApr : (delta * 3 * 365)) || 0;
          const pct = (v) => (v * 100).toFixed(4) + '%';
          const aprPct = (apr * 100).toFixed(1);
          const trend = f.trend === 'expanding' ? '<span class="pos">↑ expanding</span>'
            : (f.trend === 'converging' ? '<span class="neg">↓ converging</span>' : '·');
          const route = (f.longExchange || '?') + ' → ' + (f.shortExchange || '?');
          const next = f.nextFundingUtc || f.NextFundingUtc;
          let nextStr = '';
          if (next) {
            const sec = Math.max(0, Math.round((new Date(next).getTime() - Date.now()) / 1000));
            const h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60);
            nextStr = '<span class="fr-next muted">next ' + h + 'h ' + m + 'm</span>';
          }
          const venuesMap = bySym[f.symbol || f.Symbol] || {};
          const vals = venues.map(e => Math.abs((venuesMap[e] && venuesMap[e].rate) || 0));
          const maxV = Math.max(...vals, 1e-12);
          const colors = ['#38bdf8', '#2dd4bf', '#a78bfa', '#fbbf24', '#f472b6', '#94a3b8', '#34d399'];
          const bars = venues.map((e, i) => {
            const cell = venuesMap[e];
            const v = cell ? cell.rate : 0;
            const h = cell ? Math.max(3, Math.round((Math.abs(v) / maxV) * 40)) : 2;
            const short = e.replace('GateIo', 'Gate').replace('Kucoin', 'Kuc').slice(0, 4);
            return '<div class="fr-bar-wrap" title="' + e + ': ' + (v * 100).toFixed(4) + '%">' +
              '<div class="fr-bar" style="height:' + h + 'px;background:' + colors[i % colors.length] + ';opacity:' + (cell ? 1 : 0.25) + '"></div>' +
              '<div class="fr-bar-lbl">' + short + '</div></div>';
          }).join('');
          return '<div class="funding-row">' +
            '<span class="fr-sym">' + (f.symbol || f.Symbol || '') + '</span>' +
            '<span class="fr-delta ' + (delta >= 0 ? 'pos' : 'neg') + '">' + (delta >= 0 ? '+' : '') + pct(delta) + '</span>' +
            '<span class="fr-apr">' + aprPct + '% APR</span>' +
            '<span class="fr-trend">' + trend + '</span>' +
            '<span class="fr-route mono">' + route + '</span>' +
            nextStr +
            '<div class="fr-bars">' + bars + '</div></div>';
        }).join('') || '<div class="empty">No ranked deltas yet</div>';
      }

      // Matrix table
      const thead = document.getElementById('fundingTableHead');
      const tbody = document.getElementById('fundingTableBody');
      if (thead && tbody) {
        thead.innerHTML = '<tr><th>Symbol</th>' + venues.map(v => '<th>' + v + '</th>').join('') + '<th>Best Δ</th><th>Route</th></tr>';
        const symbols = Object.keys(bySym).sort();
        if (!symbols.length) {
          tbody.innerHTML = '<tr><td class="empty" colspan="' + (venues.length + 3) + '">Waiting for funding poll…</td></tr>';
        } else {
          // rank by max cross delta
          const rank = symbols.map(sym => {
            const rates = venues.map(v => bySym[sym][v] ? bySym[sym][v].rate : null).filter(x => x != null);
            let bestD = 0, longEx = '?', shortEx = '?';
            venues.forEach(a => {
              venues.forEach(b => {
                if (a === b) return;
                const ra = bySym[sym][a], rb = bySym[sym][b];
                if (!ra || !rb) return;
                const d = rb.rate - ra.rate;
                if (Math.abs(d) > Math.abs(bestD)) {
                  bestD = d; longEx = a; shortEx = b;
                }
              });
            });
            return { sym, bestD, longEx, shortEx };
          }).sort((a, b) => Math.abs(b.bestD) - Math.abs(a.bestD));

          tbody.innerHTML = rank.map(r => {
            const cells = venues.map(v => {
              const cell = bySym[r.sym][v];
              if (!cell) return '<td class="muted">—</td>';
              const pct = (cell.rate * 100).toFixed(4) + '%';
              const cls = cell.rate > 0.0001 ? 'pos' : (cell.rate < -0.0001 ? 'neg' : '');
              return '<td class="mono ' + cls + '">' + pct + '</td>';
            }).join('');
            const dcls = r.bestD >= 0 ? 'pos' : 'neg';
            return '<tr><td><b>' + r.sym + '</b></td>' + cells +
              '<td class="mono ' + dcls + '">' + (r.bestD >= 0 ? '+' : '') + (r.bestD * 100).toFixed(4) + '%</td>' +
              '<td class="mono muted">' + r.longEx + '→' + r.shortEx + '</td></tr>';
          }).join('');
        }
      }
    } catch (e) {
      el.innerHTML = '<div class="empty">Funding API: ' + (e.message || e) + '</div>';
    }
  },
  render() { this.load(); },
  onShow() {
    this.load();
    if (this._timer) clearInterval(this._timer);
    this._timer = setInterval(() => this.load(), 60000);
  }
};
