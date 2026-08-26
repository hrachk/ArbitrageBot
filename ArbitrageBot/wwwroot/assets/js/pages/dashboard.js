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
    
      // Live spread bars
      const bars = document.getElementById('d_spreadBars');
      if (bars) {
        const list = opps.slice(0, 8);
        if (!list.length) {
          bars.innerHTML = '<div class="empty">No signals ≥ threshold — watch best net in banner</div>';
        } else {
          const maxN = Math.max(...list.map(o => Math.abs(Number(o.netSpreadPercent || o.netProfitPercent || 0))), 0.01);
          bars.innerHTML = list.map(o => {
            const n = Number(o.netSpreadPercent != null ? o.netSpreadPercent : o.netProfitPercent) || 0;
            const w = Math.min(100, (Math.abs(n) / maxN) * 100);
            const col = n >= 0 ? '#34d399' : '#f87171';
            const route = (o.longExchange || o.buyExchange || '?') + '→' + (o.shortExchange || o.sellExchange || '?');
            return '<div style="display:grid;grid-template-columns:90px 1fr 70px;gap:8px;align-items:center;margin:6px 0">' +
              '<span class="mono" style="color:var(--blue)">' + (o.symbol || '') + '</span>' +
              '<div style="background:rgba(148,163,184,0.08);border-radius:4px;height:10px;overflow:hidden">' +
              '<div style="width:' + w + '%;height:100%;background:' + col + ';border-radius:4px"></div></div>' +
              '<span class="mono" style="color:' + col + ';text-align:right">' + (n >= 0 ? '+' : '') + n.toFixed(3) + '%</span>' +
              '<span class="muted" style="grid-column:1/-1;font-size:11px">' + route +
              (o.fullyFilled === false ? ' · partial fill' : ' · full') + '</span></div>';
          }).join('');
        }
      }

      // Venue mids for top opportunity or first symbol
      const vm = document.getElementById('d_venueMids');
      const mini = document.getElementById('d_overlayMini');
      const focusSym = (opps[0] && opps[0].symbol) || (symbols[0]) || '';
      const bt = (data.bookTickers || {})[focusSym] || {};
      const venues = Object.keys(bt);
      if (vm) {
        if (!venues.length) vm.textContent = focusSym ? (focusSym + ': no quotes yet') : '—';
        else {
          const rows = venues.map(ex => {
            const b = bt[ex];
            const mid = (Number(b.bestBid) + Number(b.bestAsk)) / 2;
            return ex + ' ' + AB.fmt(mid, mid < 1 ? 6 : 4);
          });
          let delta = '';
          if (venues.length >= 2) {
            const mids = venues.map(ex => (Number(bt[ex].bestBid) + Number(bt[ex].bestAsk)) / 2).filter(x => x > 0);
            const lo = Math.min(...mids), hi = Math.max(...mids);
            delta = ' · Δ ' + (((hi - lo) / lo) * 100).toFixed(3) + '%';
          }
          vm.innerHTML = '<b>' + focusSym + '</b> ' + rows.join(' · ') + delta;
        }
      }
      if (mini && venues.length) {
        const dpr = window.devicePixelRatio || 1;
        const w = mini.clientWidth || 800, h = 120;
        mini.width = Math.floor(w * dpr); mini.height = Math.floor(h * dpr);
        const ctx = mini.getContext('2d');
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        ctx.fillStyle = '#0a0e14'; ctx.fillRect(0, 0, w, h);
        const mids = venues.map((ex, i) => {
          const b = bt[ex];
          return { ex, mid: (Number(b.bestBid) + Number(b.bestAsk)) / 2, i };
        }).filter(x => x.mid > 0);
        if (mids.length) {
          const lo = Math.min(...mids.map(x => x.mid));
          const hi = Math.max(...mids.map(x => x.mid));
          const colors = ['#2dd4bf', '#60a5fa', '#f472b6', '#fbbf24'];
          mids.forEach((m, i) => {
            const x = ((i + 0.5) / mids.length) * w;
            const y = h - 20 - ((m.mid - lo) / ((hi - lo) || 1)) * (h - 40);
            ctx.fillStyle = colors[i % colors.length];
            ctx.beginPath(); ctx.arc(x, y, 6, 0, Math.PI * 2); ctx.fill();
            ctx.fillStyle = '#94a3b8';
            ctx.font = '10px monospace';
            ctx.fillText(m.ex, x - 16, h - 6);
            ctx.fillText(AB.fmt(m.mid, m.mid < 1 ? 5 : 3), x - 18, y - 10);
          });
          // line connecting
          ctx.strokeStyle = 'rgba(45,212,191,0.35)';
          ctx.beginPath();
          mids.forEach((m, i) => {
            const x = ((i + 0.5) / mids.length) * w;
            const y = h - 20 - ((m.mid - lo) / ((hi - lo) || 1)) * (h - 40);
            if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
          });
          ctx.stroke();
        }
      }

    
      // Paper quality + skip taxonomy
      const qel = document.getElementById('d_quality');
      const sel = document.getElementById('d_skips');
      const pa = data.paperAnalytics || data.PaperAnalytics || {};
      const q = pa.quality || {};
      if (qel) {
        qel.innerHTML =
          'Win rate <b>' + (q.winRate != null ? q.winRate : '—') + '%</b> · ' +
          'closed ' + (q.closed != null ? q.closed : (pa.closes != null ? pa.closes : '—')) + ' · ' +
          'avg PnL <b>' + AB.fmtUsd(q.avgPnl != null ? q.avgPnl : 0) + '</b> · ' +
          'avg hold ' + (q.avgHoldSec != null ? Math.round(q.avgHoldSec) + 's' : '—') + '<br>' +
          'scans ' + (pa.scans != null ? pa.scans : '—') +
          ' · opens ' + (pa.opens != null ? pa.opens : '—') +
          ' · skips ' + (pa.skips != null ? pa.skips : '—') +
          ' · best RT seen ' + (pa.bestRtPctSeen != null ? AB.fmt(pa.bestRtPctSeen, 3) + '%' : '—');
      }
      if (sel) {
        const reasons = pa.skipReasons || [];
        if (!reasons.length) sel.innerHTML = '<span class="muted">No skip reasons yet</span>';
        else {
          const maxC = Math.max(...reasons.map(r => Number(r.count) || 0), 1);
          sel.innerHTML = reasons.slice(0, 8).map(r => {
            const c = Number(r.count) || 0;
            const w = Math.min(100, (c / maxC) * 100);
            return '<div style="display:grid;grid-template-columns:1fr 48px;gap:6px;align-items:center;margin:4px 0">' +
              '<div><div class="mono" style="font-size:11px">' + (r.reason || '') + '</div>' +
              '<div style="height:6px;background:rgba(148,163,184,0.1);border-radius:3px"><div style="width:' + w +
              '%;height:100%;background:#fbbf24;border-radius:3px"></div></div></div>' +
              '<span class="mono">' + c + '</span></div>';
          }).join('');
        }
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
