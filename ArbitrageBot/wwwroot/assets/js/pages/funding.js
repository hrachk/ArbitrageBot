AB.pages = AB.pages || {};
AB.pages.funding = {
  async load() {
    const el = document.getElementById('fundingList');
    if (!el) return;
    try {
      const data = await AB.api.get('/api/funding');
      const rows = Array.isArray(data) ? data : (data.rates || []);
      if (!rows.length) {
        el.innerHTML = '<div class="empty">No funding snapshots yet — service polls every 5 min</div>';
        return;
      }
      // also try per-venue rates for bar cubes
      let all = [];
      try { all = await AB.api.get('/api/funding/all'); } catch (_) {}
      const bySym = {};
      (Array.isArray(all) ? all : []).forEach(s => {
        const sym = s.symbol || s.Symbol;
        if (!sym) return;
        if (!bySym[sym]) bySym[sym] = {};
        bySym[sym][s.exchange || s.Exchange] = Number(s.rate || s.Rate) || 0;
      });

      el.innerHTML = rows.map(f => {
        const delta = Number(f.deltaRate != null ? f.deltaRate : f.delta) || 0;
        const apr = Number(f.annualizedApr != null ? f.annualizedApr : (delta * (24 / 8) * 365)) || 0;
        const pct = (v) => (v * 100).toFixed(4) + '%';
        const aprPct = (apr * 100).toFixed(1);
        const aprCls = apr > 0.1 ? 'pos' : (apr > 0.03 ? '' : 'muted');
        const trend = f.trend === 'expanding' ? '<span style="color:var(--green)">↑</span>'
          : (f.trend === 'converging' ? '<span style="color:var(--red)">↓</span>' : '·');
        const route = (f.longExchange || '?') + '→' + (f.shortExchange || '?');
        const venues = bySym[f.symbol] || {};
        const exOrder = ['Binance', 'Bybit', 'OKX', 'Bitget'];
        const cols = ['var(--blue)', 'var(--accent)', 'var(--purple)', 'var(--amber)'];
        const vals = exOrder.map(e => Math.abs(Number(venues[e]) || 0));
        const maxV = Math.max(...vals, 1e-12);
        const bars = exOrder.map((e, i) => {
          const v = Number(venues[e]) || 0;
          const h = Math.max(2, Math.round((Math.abs(v) / maxV) * 36));
          const short = e.slice(0, 2).toUpperCase();
          return '<div class="fr-bar-wrap"><div class="fr-bar" style="height:' + h + 'px;background:' + cols[i] + '"></div>' +
            '<div class="fr-bar-lbl">' + short + '</div></div>';
        }).join('');
        return '<div class="funding-row">' +
          '<span class="fr-sym">' + (f.symbol || '') + '</span>' +
          '<span class="fr-delta ' + (delta >= 0 ? 'pos' : 'neg') + '">' + (delta >= 0 ? '+' : '') + pct(delta) + '</span>' +
          '<span class="fr-apr ' + aprCls + '">' + aprPct + '% APR</span>' +
          '<span class="fr-trend">' + trend + '</span>' +
          '<span class="fr-route">' + route + '</span>' +
          '<div class="fr-bars">' + bars + '</div></div>';
      }).join('');
    } catch (e) {
      el.innerHTML = '<div class="empty">Funding API: ' + (e.message || e) + '</div>';
    }
  },
  render() { this.load(); }
};
