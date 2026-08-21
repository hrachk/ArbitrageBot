AB.pages = AB.pages || {};
AB.pages.dashboard = {
  render(data) {
    if (!data) return;
    const $ = (id) => document.getElementById(id);
    const text = (id, v) => { const el = $(id); if (el) el.textContent = v == null ? '—' : String(v); };
    const html = (id, v) => { const el = $(id); if (el) el.innerHTML = v == null ? '—' : v; };

    try {
      const fp = data.futuresPaper || data.paper || {};
      const opps = Array.isArray(data.opportunities) ? data.opportunities : [];
      const symbols = Array.isArray(data.symbols) ? data.symbols : [];
      const exchanges = Array.isArray(data.exchanges) ? data.exchanges : [];

      text('d_scans', data.scanCount != null ? data.scanCount : 0);
      text('d_opps', opps.length);

      let best = null;
      for (let i = 0; i < opps.length; i++) {
        const v = Number(opps[i].netSpreadPercent != null ? opps[i].netSpreadPercent : opps[i].netProfitPercent);
        if (Number.isFinite(v) && (best == null || v > best)) best = v;
      }
      html('d_best', best == null ? '—' : AB.fmtPct(best));
      html('d_pnl', AB.fmtUsd(fp.realizedPnlUsd != null ? fp.realizedPnlUsd : (fp.realizedPnl != null ? fp.realizedPnl : 0)));
      text('d_open', (fp.positions && fp.positions.length) || 0);
      html('d_day', AB.fmtUsd(fp.dailyRealizedPnlUsd != null ? fp.dailyRealizedPnlUsd : 0));

      const banner = $('d_banner');
      if (banner) {
        const mode = data.mode || 'PAPER';
        const pause = data.isPaused ? ' · PAUSED' : '';
        var bestNet = data.lastBestNetOpenPercent;
        var bestGross = data.lastBestGrossPercent;
        var why = '';
        if (opps.length === 0 && bestNet != null) {
          why = ' · best net now ' + AB.fmt(bestNet, 3) + '% (need ≥ ' + AB.fmt(data.minProfitPercent, 3) + '%)';
          if (Number(bestNet) < Number(data.minProfitPercent))
            why += ' — below threshold, no open';
        }
        banner.innerHTML =
          '<b>' + mode + pause + '</b> — ' +
          exchanges.length + ' exchanges · ' + symbols.length + ' symbols · ' +
          'scan #' + (data.scanCount != null ? data.scanCount : 0) +
          ' · books ' + (data.lastBooksReady != null ? data.lastBooksReady : '—') +
          ' · pairs ' + (data.lastPairsCompared != null ? data.lastPairsCompared : '—') +
          ' · min edge ' + AB.fmt(data.minProfitPercent, 3) + '% · size ' + AB.fmt(data.quoteSize, 0) + ' USDT' +
          (bestGross != null ? ' · best gross ' + AB.fmt(bestGross, 3) + '%' : '') +
          why +
          (data.lastError ? ' · <span class="neg">' + data.lastError + '</span>' : '');
      }

      text('d_strategy', data.strategyNote || data.strategyMode || 'FuturesCross');
      text('d_discovery', [data.discoverySource, data.discoveryMessage].filter(Boolean).join(' — ') || '—');

      const pairsEl = $('d_pairs');
      if (pairsEl) {
        const pairs = (data.discoveredSymbols && data.discoveredSymbols.length)
          ? data.discoveredSymbols
          : symbols.map(function (s) { return { symbol: s }; });
        pairsEl.innerHTML = pairs.length
          ? pairs.map(function (p) {
              return '<span class="tag sym">' + (p.symbol || p) + '</span>';
            }).join('')
          : '<span class="muted">Waiting for symbol list…</span>';
      }

      const healthEl = $('d_health');
      if (healthEl) {
        const health = data.exchangeHealth || [];
        if (health.length) {
          healthEl.innerHTML = health.map(function (h) {
            const color = h.state === 'live' ? 'var(--green)' : (h.state === 'error' ? 'var(--red)' : 'var(--amber)');
            return '<div class="kpi" style="margin:0">' +
              '<div class="kpi-l">' + h.name + '</div>' +
              '<div class="mono" style="margin-top:6px;color:' + color + '">' + h.state + ' · ' + h.liveStreams + '/' + h.totalStreams + '</div>' +
              '<div class="muted" style="font-size:10px;margin-top:4px">' + (h.hasQuotes ? 'quotes ok' : 'no quotes') + '</div></div>';
          }).join('');
        } else {
          const st = data.connectionStatus || {};
          const keys = Object.keys(st);
          if (keys.length) {
            const byEx = {};
            keys.forEach(function (k) {
              const ex = k.split(':')[0];
              if (!byEx[ex]) byEx[ex] = { ok: 0, total: 0, sample: st[k] };
              byEx[ex].total++;
              if (/synced|book|live/i.test(String(st[k]))) byEx[ex].ok++;
            });
            healthEl.innerHTML = Object.keys(byEx).map(function (ex) {
              const i = byEx[ex];
              return '<div class="kpi" style="margin:0"><div class="kpi-l">' + ex + '</div>' +
                '<div class="mono" style="margin-top:6px;color:' + (i.ok > 0 ? 'var(--green)' : 'var(--amber)') + '">' + i.ok + '/' + i.total + '</div>' +
                '<div class="muted" style="font-size:10px;margin-top:4px">' + i.sample + '</div></div>';
            }).join('');
          } else {
            healthEl.innerHTML = '<div class="empty">Connecting streams… (scan #' + (data.scanCount || 0) + ')</div>';
          }
        }
      }

      const oppBody = $('d_oppBody');
      if (oppBody) {
        if (opps.length) {
          const top = opps.slice().sort(function (a, b) {
            return Number(b.netSpreadPercent != null ? b.netSpreadPercent : b.netProfitPercent || 0) -
              Number(a.netSpreadPercent != null ? a.netSpreadPercent : a.netProfitPercent || 0);
          }).slice(0, 12);
          oppBody.innerHTML = top.map(function (o) {
            return '<tr>' +
              '<td class="mono" style="color:var(--blue)">' + o.symbol + '</td>' +
              '<td class="mono" style="font-size:11px"><span class="pos">' + (o.longExchange || o.buyExchange || '') +
              '</span>→<span class="neg">' + (o.shortExchange || o.sellExchange || '') + '</span></td>' +
              '<td>' + AB.fmtPct(o.netSpreadPercent != null ? o.netSpreadPercent : o.netProfitPercent) + '</td>' +
              '<td>' + AB.fmtUsd(o.estNetPnlUsd != null ? o.estNetPnlUsd : o.netProfitQuote) + '</td>' +
              '<td>' + (o.fullyFilled ? '<span class="pos">full</span>' : '<span style="color:var(--amber)">part</span>') + '</td></tr>';
          }).join('');
        } else {
          oppBody.innerHTML = '<tr><td colspan="5" class="empty">No signals ≥ ' + AB.fmt(data.minProfitPercent, 3) + '% (scanning…)</td></tr>';
        }
      }

      const tradeBody = $('d_tradeBody');
      if (tradeBody) {
        const trades = fp.trades || [];
        if (trades.length) {
          tradeBody.innerHTML = trades.slice(0, 12).map(function (t) {
            const tme = t.openedAt || t.executedAt ? new Date(t.openedAt || t.executedAt).toLocaleTimeString() : '—';
            return '<tr><td class="muted mono">' + tme + '</td>' +
              '<td class="mono" style="color:var(--blue)">' + t.symbol + '</td>' +
              '<td>' + AB.fmtUsd(t.realizedPnlUsd != null ? t.realizedPnlUsd : t.netPnlQuote) + '</td>' +
              '<td class="muted">' + (t.status || '—') + '</td></tr>';
          }).join('');
        } else {
          tradeBody.innerHTML = '<tr><td colspan="4" class="empty">No paper trades yet</td></tr>';
        }
      }

      const posEl = $('d_positions');
      if (posEl) {
        const positions = fp.positions || [];
        posEl.innerHTML = positions.length
          ? positions.map(function (p) { return AB.posCardHtml(p); }).join('')
          : '<div class="empty">No open hedges</div>';
      }
    } catch (e) {
      console.error('dashboard render', e);
      const b = $('d_banner');
      if (b) b.textContent = 'Dashboard error: ' + e.message;
    }
  },
  onShow: function (d) {
    if (d) this.render(d);
    else if (AB.state.snapshot) this.render(AB.state.snapshot);
    else AB.refreshSnapshot();
  }
};
