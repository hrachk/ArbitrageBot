AB.pages = AB.pages || {};
AB.pages.live = {
  async load() {
    const cards = document.getElementById('live_balCards');
    const totalEl = document.getElementById('live_total');
    try {
      const data = await AB.api.get('/api/live/balances');
      const exs = data.exchanges || data.items || [];
      let sum = Number(data.totalUsdtApprox) || 0;
      if (cards) {
        if (!exs.length) {
          cards.innerHTML = '<div class="empty">No exchange data</div>';
        } else {
          cards.innerHTML = exs.map(e => {
            if (!e.ok) {
              return '<div class="kpi" style="border-color:rgba(248,113,113,.3)"><div class="kpi-l">' + (e.exchange || '?') +
                ' <span class="neg" style="font-size:10px">● error</span></div><div class="neg" style="font-size:11px;margin-top:6px">' +
                (e.error || 'failed') + '</div></div>';
            }
            const u = Number(e.usdtTotal != null ? e.usdtTotal : 0);
            return '<div class="kpi" style="border-color:rgba(56,189,248,.25)"><div class="kpi-l">' + (e.exchange || '?') +
              ' <span class="pos" style="font-size:10px">● live</span></div><div class="kpi-v" style="color:var(--blue);font-size:16px">$' +
              u.toFixed(2) + '</div><div class="muted" style="font-size:11px;margin-top:4px">' +
              (e.accountMode || e.permission || '') + '</div></div>';
          }).join('');
        }
      }
      if (totalEl) totalEl.textContent = '$' + sum.toFixed(2);
    } catch (e) {
      if (cards) cards.innerHTML = '<div class="empty">' + (e.message || e) + '</div>';
    }
  },
  render(data) {
    this.load();
    // open hedges from snapshot (paper + live ledger) — real data
    const hold = document.getElementById('live_holdCards');
    if (!hold || !data) return;
    const fp = data.futuresPaper || data.paper || {};
    const paper = Array.isArray(fp.positions) ? fp.positions : [];
    const live = data.livePositions || {};
    const ledger = Array.isArray(live.ledger) ? live.ledger : (Array.isArray(live.open) ? live.open : []);
    const rows = [];
    paper.forEach(p => rows.push({
      sym: p.symbol, route: (p.longExchange||'?')+' → '+(p.shortExchange||'?'),
      upnl: Number(p.unrealizedPnlUsd??p.unrealizedPnl)||0,
      entry: p.longEntry, id: p.id||p.tradeId, src: 'paper'
    }));
    ledger.forEach(p => rows.push({
      sym: p.symbol, route: (p.exchange||'')+' '+(p.side||''),
      upnl: Number(p.unrealizedPnl)||0, entry: p.entryPrice||p.averagePrice,
      id: p.id||p.tradeId, src: 'live'
    }));
    const openN = document.getElementById('live_openN');
    if (openN) openN.textContent = String(rows.length);
    if (!rows.length) {
      hold.innerHTML = '<div class="empty">No open hedges (paper/live ledger empty)</div>';
      return;
    }
    hold.innerHTML = rows.map(p => {
      const up = p.upnl;
      return '<div class="hold-card" style="border:1px solid var(--b);border-radius:8px;padding:10px;margin-bottom:8px">' +
        '<div style="display:flex;justify-content:space-between"><span class="mono" style="color:var(--blue);font-weight:700">' +
        (p.sym||'') + '</span><span class="mono" style="color:' + (up>=0?'var(--green)':'var(--red)') + '">' +
        (up>=0?'+':'') + up.toFixed(2) + '</span></div>' +
        '<div class="muted" style="font-size:11px;margin-top:4px">' + p.route + ' · ' + p.src + '</div></div>';
    }).join('');
  }
};
document.addEventListener('DOMContentLoaded', () => {
  const b = document.getElementById('btnLiveRefresh');
  if (b) b.addEventListener('click', () => AB.pages.live.load());
});
