AB.pages = AB.pages || {};
AB.pages.funding = {
  async load() {
    const el = document.getElementById('fundingList');
    if (!el) return;
    try {
      const data = await AB.api.get('/api/funding');
      const rows = Array.isArray(data) ? data : (data.rates || data.items || []);
      if (!rows.length) {
        el.innerHTML = '<div class="empty">No funding snapshots yet — service polls every 5 min</div>';
        return;
      }
      el.innerHTML = rows.map(f => {
        const delta = Number(f.deltaRate != null ? f.deltaRate : f.delta) || 0;
        const apr = Number(f.annualizedApr != null ? f.annualizedApr : (delta * (24 / 8) * 365)) || 0;
        const pct = (v) => (v * 100).toFixed(4) + '%';
        const aprPct = (apr * 100).toFixed(1);
        const aprCls = apr > 0.1 ? 'pos' : (apr > 0.03 ? '' : 'muted');
        const trend = f.trend === 'expanding' ? '<span style="color:var(--green)">↑</span>' : (f.trend === 'converging' ? '<span style="color:var(--red)">↓</span>' : '·');
        const route = (f.longExchange || '?') + '→' + (f.shortExchange || '?');
        return '<div class="funding-row">' +
          '<span class="fr-sym">' + (f.symbol || '') + '</span>' +
          '<span class="fr-delta ' + (delta >= 0 ? 'pos' : 'neg') + '">' + (delta >= 0 ? '+' : '') + pct(delta) + '</span>' +
          '<span class="fr-apr ' + aprCls + '">' + aprPct + '% APR</span>' +
          '<span class="fr-trend">' + trend + '</span>' +
          '<span class="fr-route">' + route + '</span>' +
          '</div>';
      }).join('');
    } catch (e) {
      el.innerHTML = '<div class="empty">Funding API: ' + (e.message || e) + '</div>';
    }
  },
  render() { this.load(); }
};
