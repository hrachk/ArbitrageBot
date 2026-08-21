AB.pages.dashboard = {
  render(data) {
    if (!data) {
      console.warn('dashboard: no data');
      return;
    }
    try {
      const fp = data.futuresPaper || data.paper || {};
      const opps = Array.isArray(data.opportunities) ? data.opportunities : [];
      const symbols = data.symbols || [];
      const exchanges = data.exchanges || [];

      const setText = (id, v) => { const el = AB.$(id); if (el) el.textContent = v; };
      const setHtml = (id, v) => { const el = AB.$(id); if (el) el.innerHTML = v; };

      setText('d_scans', data.scanCount ?? 0);
      setText('d_opps', opps.length);

      let best = null;
      opps.forEach(o => {
        const v = Number(o.netSpreadPercent ?? o.netProfitPercent);
        if (Number.isFinite(v) && (best == null || v > best)) best = v;
      });
      setHtml('d_best', best == null ? '—' : AB.fmtPct(best));
      setHtml('d_pnl', AB.fmtUsd(fp.realizedPnlUsd ?? fp.realizedPnl ?? 0));
      setText('d_open', (fp.positions || []).length);
      setHtml('d_day', AB.fmtUsd(fp.dailyRealizedPnlUsd ?? 0));

      if (AB.$('d_banner')) {
        const mode = data.mode || 'PAPER';
        const pause = data.isPaused ? ' · PAUSED' : '';
        AB.$('d_banner').innerHTML =
          `<b>${mode}${pause}</b> — ${exchanges.length} exchanges · ${symbols.length} symbols · ` +
          `scan #${data.scanCount ?? 0} · min edge ${AB.fmt(data.minProfitPercent, 3)}% · size ${AB.fmt(data.quoteSize, 0)} USDT` +
          (data.lastError ? ` · <span class="neg">${data.lastError}</span>` : '');
      }

      setText('d_strategy', data.strategyNote || data.strategyMode || 'FuturesCross');
      setText('d_discovery', [data.discoverySource, data.discoveryMessage].filter(Boolean).join(' — ') || '—');

      const pairs = data.discoveredSymbols?.length
        ? data.discoveredSymbols
        : symbols.map(s => ({ symbol: s }));
      if (AB.$('d_pairs')) {
        AB.$('d_pairs').innerHTML = pairs.length
          ? pairs.map(p => `<span class="tag sym">${p.symbol || p}</span>`).join('')
          : '<span class="muted">No symbols yet — wait for discovery</span>';
      }

      // Prefer exchangeHealth from snapshot; fallback connectionStatus
      if (AB.$('d_health')) {
        const health = data.exchangeHealth || [];
        if (health.length) {
          AB.$('d_health').innerHTML = health.map(h => {
            const color = h.state === 'live' ? 'var(--green)' : h.state === 'error' ? 'var(--red)' : 'var(--amber)';
            return `<div class="kpi" style="margin:0">
              <div class="kpi-l">${h.name}</div>
              <div class="mono" style="margin-top:6px;color:${color}">${h.state} · ${h.liveStreams}/${h.totalStreams}</div>
              <div class="muted" style="font-size:10px;margin-top:4px">${h.hasQuotes ? 'quotes ok' : 'no quotes'}</div>
            </div>`;
          }).join('');
        } else {
          const st = data.connectionStatus || {};
          const byEx = {};
          Object.entries(st).forEach(([k, v]) => {
            const ex = k.split(':')[0];
            if (!byEx[ex]) byEx[ex] = { ok: 0, total: 0, sample: v };
            byEx[ex].total++;
            if (/synced|book|live/i.test(String(v))) byEx[ex].ok++;
          });
          const entries = Object.entries(byEx);
          AB.$('d_health').innerHTML = entries.length
            ? entries.map(([ex, i]) =>
                `<div class="kpi" style="margin:0"><div class="kpi-l">${ex}</div>
                 <div class="mono" style="margin-top:6px;color:${i.ok > 0 ? 'var(--green)' : 'var(--amber)'}">${i.ok}/${i.total}</div>
                 <div class="muted" style="font-size:10px;margin-top:4px">${i.sample}</div></div>`
              ).join('')
            : '<div class="empty">Streams starting… refresh in a few seconds</div>';
        }
      }

      if (AB.$('d_oppBody')) {
        const top = [...opps].sort((a, b) =>
          Number(b.netSpreadPercent ?? b.netProfitPercent || 0) -
          Number(a.netSpreadPercent ?? a.netProfitPercent || 0)).slice(0, 12);
        AB.$('d_oppBody').innerHTML = top.length ? top.map(o => `<tr>
          <td class="mono" style="color:var(--blue)">${o.symbol}</td>
          <td class="mono" style="font-size:11px"><span class="pos">${o.longExchange || o.buyExchange}</span>→<span class="neg">${o.shortExchange || o.sellExchange}</span></td>
          <td>${AB.fmtPct(o.netSpreadPercent ?? o.netProfitPercent)}</td>
          <td>${AB.fmtUsd(o.estNetPnlUsd ?? o.netProfitQuote)}</td>
          <td>${o.fullyFilled ? '<span class="pos">full</span>' : '<span style="color:var(--amber)">part</span>'}</td>
        </tr>`).join('') : `<tr><td colspan="5" class="empty">No signals ≥ ${AB.fmt(data.minProfitPercent, 3)}% yet (system scanning…)</td></tr>`;
      }

      const trades = fp.trades || [];
      if (AB.$('d_tradeBody')) {
        AB.$('d_tradeBody').innerHTML = trades.length ? trades.slice(0, 12).map(t => {
          const tme = t.openedAt || t.executedAt ? new Date(t.openedAt || t.executedAt).toLocaleTimeString() : '—';
          return `<tr>
            <td class="muted mono">${tme}</td>
            <td class="mono" style="color:var(--blue)">${t.symbol}</td>
            <td>${AB.fmtUsd(t.realizedPnlUsd ?? t.netPnlQuote)}</td>
            <td class="muted">${t.status || '—'}</td>
          </tr>`;
        }).join('') : '<tr><td colspan="4" class="empty">No paper trades yet</td></tr>';
      }

      const positions = fp.positions || [];
      if (AB.$('d_positions')) {
        AB.$('d_positions').innerHTML = positions.length
          ? positions.map(p => AB.posCardHtml(p)).join('')
          : '<div class="empty">No open hedges</div>';
      }
    } catch (e) {
      console.error('dashboard render failed', e);
      if (AB.$('d_banner')) AB.$('d_banner').textContent = 'Dashboard render error: ' + e.message;
    }
  },
  onShow(d) {
    if (d) this.render(d);
    else if (AB.state.snapshot) this.render(AB.state.snapshot);
  }
};
