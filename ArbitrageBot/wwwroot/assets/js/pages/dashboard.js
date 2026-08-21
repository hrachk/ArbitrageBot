AB.pages.dashboard = {
  render(data) {
    if (!data) return;
    const fp = data.futuresPaper || {};
    const opps = data.opportunities || [];

    AB.$('d_scans').textContent = data.scanCount ?? data.scans ?? '—';
    AB.$('d_opps').textContent = opps.length;
    const best = opps.length
      ? Math.max(...opps.map(o => Number(o.netSpreadPercent ?? o.netProfitPercent ?? -999)))
      : null;
    AB.$('d_best').innerHTML = best == null || best < -100 ? '—' : AB.fmtPct(best);
    AB.$('d_pnl').innerHTML = AB.fmtUsd(fp.realizedPnlUsd ?? fp.realizedPnl);
    AB.$('d_open').textContent = (fp.positions || []).length;
    AB.$('d_day').innerHTML = AB.fmtUsd(fp.dailyRealizedPnlUsd);

    if (AB.$('d_strategy')) AB.$('d_strategy').textContent = data.strategyNote || data.strategyMode || '';
    if (AB.$('d_discovery'))
      AB.$('d_discovery').textContent = [data.discoverySource, data.discoveryMessage].filter(Boolean).join(' — ');

    const pairs = data.discoveredSymbols || (data.symbols || []).map(s => ({ symbol: s }));
    if (AB.$('d_pairs'))
      AB.$('d_pairs').innerHTML = pairs.map(p =>
        `<span class="tag sym">${p.symbol || p}</span>`
      ).join('') || '—';

    const st = data.connectionStatus || {};
    const byEx = {};
    Object.entries(st).forEach(([k, v]) => {
      const ex = k.split(':')[0];
      if (!byEx[ex]) byEx[ex] = { ok: 0, total: 0, sample: v };
      byEx[ex].total++;
      if (/synced|book/i.test(String(v))) byEx[ex].ok++;
    });
    if (AB.$('d_health')) {
      const entries = Object.entries(byEx);
      AB.$('d_health').innerHTML = entries.length
        ? entries.map(([ex, i]) =>
            `<div class="kpi" style="margin:0"><div class="kpi-l">${ex}</div>
             <div class="mono" style="margin-top:6px;color:${i.ok > 0 ? 'var(--green)' : 'var(--amber)'}">${i.ok}/${i.total}</div>
             <div class="muted" style="font-size:10px;margin-top:4px">${i.sample}</div></div>`
          ).join('')
        : '<div class="empty">No streams yet</div>';
    }

    if (AB.$('d_oppBody')) {
      const top = [...opps].sort((a, b) =>
        Number(b.netSpreadPercent ?? b.netProfitPercent || 0) -
        Number(a.netSpreadPercent ?? a.netProfitPercent || 0)).slice(0, 10);
      AB.$('d_oppBody').innerHTML = top.length ? top.map(o => `<tr>
        <td class="mono" style="color:var(--blue)">${o.symbol}</td>
        <td class="mono" style="font-size:11px"><span class="pos">${o.longExchange || o.buyExchange}</span>→<span class="neg">${o.shortExchange || o.sellExchange}</span></td>
        <td>${AB.fmtPct(o.netSpreadPercent ?? o.netProfitPercent)}</td>
        <td>${AB.fmtUsd(o.estNetPnlUsd ?? o.netProfitQuote)}</td>
        <td>${o.fullyFilled ? '<span class="pos">full</span>' : '<span style="color:var(--amber)">part</span>'}</td>
      </tr>`).join('') : '<tr><td colspan="5" class="empty">No signals ≥ threshold</td></tr>';
    }

    const trades = fp.trades || [];
    if (AB.$('d_tradeBody')) {
      AB.$('d_tradeBody').innerHTML = trades.length ? trades.slice(0, 12).map(t => {
        const tme = t.openedAt ? new Date(t.openedAt).toLocaleTimeString() : '—';
        return `<tr>
          <td class="muted mono">${tme}</td>
          <td class="mono" style="color:var(--blue)">${t.symbol}</td>
          <td>${AB.fmtUsd(t.realizedPnlUsd)}</td>
          <td class="muted">${t.status}</td>
        </tr>`;
      }).join('') : '<tr><td colspan="4" class="empty">No trades yet</td></tr>';
    }

    const positions = fp.positions || [];
    if (AB.$('d_positions')) {
      AB.$('d_positions').innerHTML = positions.length
        ? positions.map(p => AB.posCardHtml(p)).join('')
        : '<div class="empty">No open hedges</div>';
    }
  },
  onShow(d) { if (d) this.render(d); }
};
