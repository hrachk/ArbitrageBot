AB.pages.reports = {
  render(data) {
    const fp = data.futuresPaper || data.paper || {};
    const trades = fp.trades || [];
    const positions = fp.positions || [];

    AB.$('r_realized').innerHTML = AB.fmtUsd(fp.realizedPnl ?? fp.realizedPnlUsd);
    AB.$('r_day').innerHTML = AB.fmtUsd(fp.dailyRealizedPnlUsd);
    AB.$('r_attempts').textContent = fp.tradeCount ?? fp.tradeAttempts ?? 0;
    AB.$('r_open').textContent = positions.length;
    AB.$('r_lev').textContent = (fp.leverage || 5) + 'x';
    AB.$('r_stop').textContent = fp.stopLossUsd ?? '—';

    // margins
    const bal = fp.margin || fp.balances || {};
    AB.$('r_margin').innerHTML = Object.keys(bal).length
      ? Object.entries(bal).map(([ex, v]) => {
          const usdt = typeof v === 'object' ? (v.USDT ?? Object.values(v)[0]) : v;
          return `<div class="card"><div class="kpi-label">${ex}</div><div class="mono kpi-value" style="font-size:18px">${AB.fmt(usdt,2)} <span class="muted" style="font-size:12px">USDT</span></div></div>`;
        }).join('')
      : '<div class="muted">No margin data</div>';

    AB.$('r_posBody').innerHTML = positions.length ? positions.map(p => `<tr>
      <td class="mono">${p.symbol}</td>
      <td class="mono" style="font-size:11px">${p.longExchange}→${p.shortExchange}</td>
      <td class="mono">${AB.fmt(p.baseQty,6)}</td>
      <td>${AB.fmtUsd(p.unrealizedPnlUsd)}</td>
      <td class="mono muted">${AB.fmt(p.currentWidthPercent,3)}%</td>
      <td class="muted">${p.openedAt ? new Date(p.openedAt).toLocaleString() : '—'}</td>
    </tr>`).join('') : '<tr><td colspan="6" class="muted" style="text-align:center;padding:20px">No open hedges</td></tr>';

    AB.$('r_tradeBody').innerHTML = trades.length ? trades.map(t => `<tr>
      <td class="muted">${t.openedAt ? new Date(t.openedAt).toLocaleString() : '—'}</td>
      <td class="mono">${t.symbol}</td>
      <td class="mono" style="font-size:11px">${t.longExchange||t.buyExchange}→${t.shortExchange||t.sellExchange}</td>
      <td class="mono">${AB.fmt(t.baseQty,6)}</td>
      <td>${AB.fmtUsd(t.realizedPnlUsd ?? t.netPnlQuote)}</td>
      <td class="muted" style="font-size:11px">${t.status||''}</td>
      <td class="muted" style="font-size:11px">${t.message||''}</td>
    </tr>`).join('') : '<tr><td colspan="7" class="muted" style="text-align:center;padding:24px">Trade history empty</td></tr>';
  },
  onShow(d) { if (d) this.render(d); }
};
