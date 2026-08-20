AB.pages.dashboard = {
  render(data) {
    const fp = data.futuresPaper || data.paper || {};
    const opps = data.opportunities || [];
    AB.$('d_scans').textContent = data.scanCount ?? 0;
    AB.$('d_opps').textContent = opps.length;
    const best = opps.length ? Math.max(...opps.map(o => Number(o.netProfitPercent || 0))) : null;
    AB.$('d_best').innerHTML = best == null ? '—' : AB.fmtPct(best);
    const pnl = Number(fp.realizedPnl ?? fp.realizedPnlUsd ?? 0);
    AB.$('d_pnl').innerHTML = AB.fmtUsd(pnl);
    AB.$('d_open').textContent = (fp.positions || []).length;
    const day = Number(fp.dailyRealizedPnlUsd ?? 0);
    AB.$('d_day').innerHTML = AB.fmtUsd(day);

    AB.$('d_strategy').textContent = data.strategyNote || '';
    AB.$('d_discovery').textContent = [data.discoverySource, data.discoveryMessage].filter(Boolean).join(' — ');

    const pairs = data.discoveredSymbols || (data.symbols || []).map(s => ({ symbol: s }));
    AB.$('d_pairs').innerHTML = pairs.map(p =>
      `<span class="tag mono">${p.symbol || p}${p.medianQuoteVolume ? ' · ' + (Number(p.medianQuoteVolume)/1e6).toFixed(1)+'M' : ''}</span>`
    ).join(' ') || '—';

    // health
    const st = data.connectionStatus || {};
    const byEx = {};
    Object.entries(st).forEach(([k, v]) => {
      const ex = k.split(':')[0];
      if (!byEx[ex]) byEx[ex] = { ok: 0, total: 0, sample: v };
      byEx[ex].total++;
      if (/synced|book/i.test(String(v))) byEx[ex].ok++;
    });
    AB.$('d_health').innerHTML = Object.entries(byEx).map(([ex, i]) =>
      `<div class="card"><div class="kpi-label">${ex}</div>
       <div class="mono" style="margin-top:6px;color:${i.ok>0?'var(--emerald)':'var(--amber)'}">${i.ok}/${i.total} · ${i.sample}</div></div>`
    ).join('') || '<div class="muted">No streams yet</div>';

    // top opportunities
    const top = [...opps].sort((a,b)=>Number(b.netProfitPercent||0)-Number(a.netProfitPercent||0)).slice(0,8);
    AB.$('d_oppBody').innerHTML = top.length ? top.map(o => `<tr>
      <td class="mono" style="color:var(--cyan)">${o.symbol}</td>
      <td class="mono" style="font-size:11px"><span class="pos">${o.buyExchange||o.longExchange}</span>→<span class="neg">${o.sellExchange||o.shortExchange}</span></td>
      <td>${AB.fmtPct(o.netProfitPercent)}</td>
      <td class="mono">${AB.fmt(o.netProfitQuote||o.estNetPnlUsd,2)}</td>
      <td>${o.fullyFilled?'<span class="pos">full</span>':'<span style="color:var(--amber)">part</span>'}</td>
    </tr>`).join('') : '<tr><td colspan="5" class="muted" style="text-align:center;padding:24px">No signals ≥ threshold</td></tr>';

    // recent trades
    const trades = fp.trades || [];
    const positions = fp.positions || [];
    if (AB.$('d_positions')) {
      AB.$('d_positions').innerHTML = positions.length
        ? positions.map(p => AB.posCardHtml(p)).join('')
        : '<div class="empty-state">Нет открытых paper-хеджей</div>';
    }

    AB.$('d_tradeBody').innerHTML = trades.length ? trades.slice(0,10).map(t => {
      const tme = t.openedAt || t.executedAt ? new Date(t.openedAt||t.executedAt).toLocaleTimeString() : '—';
      return `<tr><td class="muted">${tme}</td>
        <td class="mono" style="font-size:11px">${t.symbol}</td>
        <td>${AB.fmtUsd(t.realizedPnlUsd ?? t.netPnlQuote)}</td>
        <td class="muted" style="font-size:11px">${t.status||''}</td></tr>`;
    }).join('') : '<tr><td colspan="4" class="muted" style="text-align:center;padding:20px">No paper trades yet</td></tr>';
  }
};

document.getElementById('btnPause')?.addEventListener('click', async () => {
  await AB.api.post('/api/control/toggle');
});
document.getElementById('btnResetPaper')?.addEventListener('click', async () => {
  await AB.api.post('/api/paper/reset');
  const s = await AB.api.get('/api/snapshot');
  AB.onSnapshot(s);
});
