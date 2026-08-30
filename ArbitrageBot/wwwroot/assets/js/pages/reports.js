// ── Reports page — чёткое разделение LIVE vs PAPER ────────────────────────

AB.pages.reports = {
  _perfDays: 7,

  // ── вызывается из SignalR при каждом тике ────────────────────────────────
  render(data) {
    const mode = (data.mode || 'PAPER').toUpperCase();
    const isLive = mode === 'LIVE';

    // Показываем/скрываем секции
    const liveSection  = document.getElementById('rep_liveSection');
    const paperSection = document.getElementById('rep_paperSection');
    if (liveSection)  liveSection.style.display  = isLive ? '' : 'none';
    if (paperSection) paperSection.style.display = isLive ? 'none' : '';

    if (isLive) this._renderLive(data);
    else        this._renderPaper(data);
  },

  // ── LIVE render ──────────────────────────────────────────────────────────
  _renderLive(data) {
    const lp = data.livePositions || {};
    const ledger = Array.isArray(lp.ledger) ? lp.ledger
                 : Array.isArray(lp.open)   ? lp.open : [];
    const closed  = Array.isArray(lp.closed)  ? lp.closed  : [];
    const allTrades = [...closed, ...ledger];

    // KPI
    const realized = lp.realizedPnlUsd ?? lp.realizedPnl ?? 0;
    const el = (id) => AB.$(id);
    if (el('r_realized')) el('r_realized').innerHTML = AB.fmtUsd(realized);
    if (el('r_open'))     el('r_open').textContent   = ledger.length;
    if (el('r_attempts')) el('r_attempts').textContent = closed.length;
    if (el('r_liveOpenCount'))  el('r_liveOpenCount').textContent  = ledger.length + ' open';
    if (el('r_liveLedgerCount')) el('r_liveLedgerCount').textContent = closed.length + ' closed · ' + ledger.length + ' open';

    // Open hedges table
    el('r_posBody').innerHTML = ledger.length
      ? ledger.map(p => {
          const sym   = p.symbol  || p.Symbol  || '—';
          const lEx   = p.longExchange  || p.LongExchange  || '?';
          const sEx   = p.shortExchange || p.ShortExchange || '?';
          const qty   = p.baseQty || p.BaseQty || 0;
          const upnl  = p.unrealizedPnlUsd ?? p.unrealizedPnl;
          const upnlS = upnl != null ? AB.fmtUsd(upnl) : '—';
          const upnlC = upnl != null ? (upnl >= 0 ? 'pos' : 'neg') : '';
          const opened = (p.openedAt || p.OpenedAt)
            ? new Date(p.openedAt || p.OpenedAt).toLocaleString() : '—';
          return `<tr>
            <td class="mono">${sym}</td>
            <td class="mono" style="font-size:11px">${lEx} → ${sEx}</td>
            <td class="mono">${AB.fmt(qty, 6)}</td>
            <td class="mono ${upnlC}">${upnlS}</td>
            <td class="muted">${opened}</td>
          </tr>`;
        }).join('')
      : '<tr><td colspan="5" class="muted" style="text-align:center;padding:20px">No open live hedges</td></tr>';

    // Live trade ledger (closed + open)
    el('r_tradeBody').innerHTML = allTrades.length
      ? allTrades.map(t => {
          const pnl  = t.realizedPnlUsd ?? t.netPnlQuote;
          const pnlN = pnl != null ? Number(pnl) : null;
          const pnlS = pnlN != null ? ((pnlN >= 0 ? '+' : '') + AB.fmt(pnlN, 2)) : '—';
          const pnlC = pnlN != null ? (pnlN > 0 ? 'pos' : pnlN < 0 ? 'neg' : '') : '';
          return `<tr>
            <td class="muted">${(t.openedAt||t.OpenedAt||'').toString().slice(0,19)}</td>
            <td class="mono">${t.symbol||t.Symbol||'—'}</td>
            <td class="muted" style="font-size:11px">${t.longExchange||t.LongExchange||'?'}→${t.shortExchange||t.ShortExchange||'?'}</td>
            <td class="mono">${AB.fmt(t.baseQty||t.BaseQty||0, 4)}</td>
            <td class="mono ${pnlC}">${pnlS}</td>
            <td style="font-size:11px">${t.status||t.Status||'—'}</td>
            <td class="muted" style="font-size:11px;max-width:180px;overflow:hidden;text-overflow:ellipsis">${t.message||t.Message||''}</td>
          </tr>`;
        }).join('')
      : '<tr><td colspan="7" class="muted" style="text-align:center;padding:24px">No trades in live ledger yet</td></tr>';
  },

  // ── PAPER render ─────────────────────────────────────────────────────────
  _renderPaper(data) {
    const fp     = data.futuresPaper || data.paper || {};
    const trades = fp.trades    || [];
    const pos    = fp.positions || [];

    // Paper KPIs
    const el = (id) => AB.$(id);
    if (el('r_paper_realized')) el('r_paper_realized').innerHTML = AB.fmtUsd(fp.realizedPnlUsd ?? fp.realizedPnl);
    if (el('r_day'))            el('r_day').innerHTML            = AB.fmtUsd(fp.dailyRealizedPnlUsd);
    if (el('r_paper_closes'))   el('r_paper_closes').textContent = fp.tradeCount ?? fp.tradeAttempts ?? 0;
    if (el('r_paper_open'))     el('r_paper_open').textContent   = pos.length;
    if (el('r_lev'))            el('r_lev').textContent          = (fp.leverage || 5) + 'x';
    if (el('r_stop'))           el('r_stop').textContent         = fp.stopLossUsd ?? '—';

    // Paper open positions
    if (el('r_paperPosBody')) {
      el('r_paperPosBody').innerHTML = pos.length
        ? pos.map(p => `<tr>
            <td class="mono">${p.symbol}</td>
            <td class="mono" style="font-size:11px">${p.longExchange}→${p.shortExchange}</td>
            <td class="mono">${AB.fmt(p.baseQty, 6)}</td>
            <td>${AB.fmtUsd(p.unrealizedPnlUsd ?? p.unrealizedPnl)}</td>
            <td class="mono muted">${AB.fmt(p.currentWidthPercent, 3)}%</td>
            <td class="muted">${p.openedAt ? new Date(p.openedAt).toLocaleString() : '—'}</td>
          </tr>`).join('')
        : '<tr><td colspan="6" class="muted" style="text-align:center;padding:20px">No open paper hedges</td></tr>';
    }

    // Paper trade history
    if (el('r_paperTradeBody')) {
      el('r_paperTradeBody').innerHTML = trades.length
        ? trades.map(t => {
            const pnl  = t.realizedPnlUsd ?? t.netPnlQuote;
            const pnlN = pnl != null ? Number(pnl) : null;
            const pnlS = pnlN != null ? ((pnlN >= 0 ? '+' : '') + AB.fmt(pnlN, 2)) : '—';
            const pnlC = pnlN != null ? (pnlN > 0 ? 'pos' : pnlN < 0 ? 'neg' : '') : '';
            return `<tr>
              <td class="muted">${t.openedAt ? new Date(t.openedAt).toLocaleString() : '—'}</td>
              <td class="mono">${t.symbol}</td>
              <td class="muted" style="font-size:11px">${t.longExchange||t.buyExchange}→${t.shortExchange||t.sellExchange}</td>
              <td class="mono">${AB.fmt(t.baseQty, 6)}</td>
              <td class="mono ${pnlC}">${pnlS}</td>
              <td style="font-size:11px">${t.status||''}</td>
              <td class="muted" style="font-size:11px">${t.message||''}</td>
            </tr>`;
          }).join('')
        : '<tr><td colspan="7" class="muted" style="text-align:center;padding:24px">No paper trade history yet</td></tr>';
    }

    // Margin by venue (paper)
    const bd    = fp.marginBreakdown || {};
    const bal   = fp.margin || fp.balances || {};
    const start = fp.paperStartingQuote || 50000;
    if (el('r_margin')) {
      if (Object.keys(bd).length) {
        let sumEq = 0, sumFree = 0, sumLocked = 0, n = 0;
        const cards = Object.entries(bd).map(([ex, m]) => {
          const free  = Number(m.free   ?? 0);
          const locked = Number(m.locked ?? 0);
          const equity = Number(m.equity ?? free + locked);
          const delta  = Number(m.deltaFromStart ?? equity - start);
          sumEq += equity; sumFree += free; sumLocked += locked; n++;
          return `<div class="kpi" style="margin:0">
            <div class="kpi-l">${ex}</div>
            <div class="mono" style="font-size:18px;font-weight:700;margin-top:6px">${AB.fmt(equity, 2)} <span class="muted" style="font-size:11px">USDT</span></div>
            <div class="muted mono" style="font-size:11px;margin-top:6px;line-height:1.5">
              free ${AB.fmt(free, 2)} · locked ${AB.fmt(locked, 2)}<br/>
              Δ <span class="${delta >= 0 ? 'pos' : 'neg'}">${delta >= 0 ? '+' : ''}${AB.fmt(delta, 2)}</span>
            </div>
          </div>`;
        }).join('');
        const totalDelta = sumEq - start * Math.max(n, 1);
        el('r_margin').innerHTML = `<div class="kpi" style="margin:0;border-color:rgba(45,212,191,0.35)">
          <div class="kpi-l">ALL VENUES</div>
          <div class="mono" style="font-size:20px;font-weight:700;margin-top:6px;color:var(--accent)">${AB.fmt(sumEq, 2)} USDT</div>
          <div class="muted mono" style="font-size:11px;margin-top:6px;line-height:1.5">
            free ${AB.fmt(sumFree, 2)} · locked ${AB.fmt(sumLocked, 2)}<br/>
            Δ <span class="${totalDelta >= 0 ? 'pos' : 'neg'}">${totalDelta >= 0 ? '+' : ''}${AB.fmt(totalDelta, 2)}</span>
          </div>
        </div>` + cards;
      } else if (Object.keys(bal).length) {
        el('r_margin').innerHTML = Object.entries(bal).map(([ex, v]) => {
          const usdt = typeof v === 'object' ? (v.USDT ?? Object.values(v)[0]) : v;
          return `<div class="kpi" style="margin:0"><div class="kpi-l">${ex}</div>
            <div class="mono" style="font-size:16px;margin-top:6px">${AB.fmt(usdt, 2)} free</div></div>`;
        }).join('');
      } else {
        el('r_margin').innerHTML = '<div class="empty">No margin data</div>';
      }
    }

    // Day summary
    const an = data.paperAnalytics || {};
    if (el('r_daySummary')) {
      el('r_daySummary').innerHTML = [
        `<div><span class="muted">Day</span> ${an.dayUtc || '—'}</div>`,
        `<div>scans <b>${an.scans ?? 0}</b> · avg candidates <b>${an.avgCandidates ?? 0}</b></div>`,
        `<div>opens <b class="pos">${an.opens ?? 0}</b> · closes <b>${an.closes ?? 0}</b> · skips <b>${an.skips ?? 0}</b></div>`,
        `<div>realized ${AB.fmtUsd(an.realizedPnlUsd)} · best open ${AB.fmt(an.bestOpenPctSeen, 3)}% · best RT ${AB.fmt(an.bestRtPctSeen, 3)}%</div>`,
      ].join('');
    }

    // Skip reasons
    const reasons = an.skipReasons || [];
    if (el('r_skipReasons')) {
      el('r_skipReasons').innerHTML = reasons.length
        ? reasons.map(r => `<div class="mono" style="margin:4px 0"><span class="muted">${r.reason}</span> × <b>${r.count}</b></div>`).join('')
        : '<span class="muted">No skips recorded yet</span>';
    }

    // Recent skips
    const skips = data.paperRecentSkips || [];
    if (el('r_skipBody')) {
      el('r_skipBody').innerHTML = skips.length
        ? skips.map(s => {
            const t = s.utc ? new Date(s.utc).toISOString().slice(11, 19) : '—';
            return `<tr>
              <td class="muted mono">${t}</td>
              <td style="font-size:11px">${s.reason || ''}</td>
              <td class="mono" style="color:var(--cyan)">${s.symbol || '—'}</td>
              <td class="mono">${s.openNet != null ? Number(s.openNet).toFixed(3) : '—'}</td>
              <td class="mono">${s.rtNet  != null ? Number(s.rtNet).toFixed(3)  : '—'}</td>
            </tr>`;
          }).join('')
        : '<tr><td colspan="5" class="muted" style="text-align:center;padding:16px">Waiting for scans…</td></tr>';
    }
  },

  // ── Multi-day table ──────────────────────────────────────────────────────
  async loadDays() {
    try {
      const days = await AB.api.get('/api/analytics/days?maxDays=10');
      const body = AB.$('r_daysBody');
      if (!body) return;
      body.innerHTML = (days || []).map(d => {
        const day = d.dayUtc || d.DayUtc || '—';
        return `<tr>
          <td class="mono">${day}</td>
          <td class="mono">${d.scans  ?? d.Scans  ?? 0}</td>
          <td class="mono">${d.opens  ?? d.Opens  ?? 0}</td>
          <td class="mono">${d.closes ?? d.Closes ?? 0}</td>
          <td class="mono">${d.skips  ?? d.Skips  ?? 0}</td>
          <td>${AB.fmtUsd(d.realizedPnlUsd ?? d.RealizedPnlUsd ?? 0)}</td>
          <td class="mono">${AB.fmt(d.bestOpenPctSeen ?? d.BestOpenPctSeen, 3)}%</td>
        </tr>`;
      }).join('') || '<tr><td colspan="7" class="muted" style="text-align:center">No history yet</td></tr>';
    } catch (e) { console.warn(e); }
  },

  // ── Performance panel (paper analytics API) ──────────────────────────────
  async loadPerformance(days) {
    if (days) this._perfDays = days;
    const d  = this._perfDays || 7;
    const k1 = AB.$('perfKpis');
    const k2 = AB.$('perfKpis2');
    if (!k1) return;
    k1.innerHTML = '<div class="empty">Loading…</div>';
    try {
      const p      = await AB.api.get('/api/analytics/performance?days=' + d);
      const trades = await AB.api.get('/api/analytics/trades?take=60');
      const kpi = (label, val, cls) =>
        `<div class="kpi" style="margin:0"><div class="kpi-l">${label}</div>
         <div class="mono ${cls||''}" style="font-size:18px;font-weight:700;margin-top:6px">${val}</div></div>`;
      const pn = (v, dig) => {
        const n = Number(v) || 0;
        return { s: (n >= 0 ? '+' : '') + AB.fmt(n, dig ?? 2), cls: n > 0 ? 'pos' : (n < 0 ? 'neg' : '') };
      };
      const net = pn(p.netPnl);
      k1.innerHTML = [
        kpi('NET PNL',      net.s + ' USDT', net.cls),
        kpi('WIN RATE',     AB.fmt(p.winRate, 1) + '%', ''),
        kpi('TOTAL TRADES', p.totalTrades, ''),
        kpi('AVG WIN',      pn(p.avgWin).s, 'pos'),
      ].join('');
      k2.innerHTML = [
        kpi('AVG LOSS',      pn(p.avgLoss).s, 'neg'),
        kpi('PROFIT FACTOR', AB.fmt(p.profitFactor, 2), ''),
        kpi('MAX DRAWDOWN',  AB.fmt(p.maxDrawdown, 2), 'neg'),
        kpi('BEST TRADE',    p.bestTrade  ? pn(p.bestTrade.pnl).s  : '—', 'pos'),
        kpi('WORST TRADE',   p.worstTrade ? pn(p.worstTrade.pnl).s : '—', 'neg'),
        kpi('AVG DURATION',  AB.fmt(p.avgDurationMin, 1) + ' m', ''),
        kpi('EXPECTANCY',    pn(p.expectancy).s, pn(p.expectancy).cls),
        kpi('AVG R:R',       AB.fmt(p.avgRr, 2), ''),
        kpi('CONSEC WINS',   p.consecWins, 'pos'),
        kpi('CONSEC LOSS',   p.consecLoss, 'neg'),
      ].join('');

      // Equity curve
      const svg = AB.$('perfCurve');
      if (svg && p.equityCurve && p.equityCurve.length) {
        const pts  = p.equityCurve;
        const ys   = pts.map(x => x.equity);
        const minY = Math.min(0, ...ys), maxY = Math.max(0, ...ys);
        const span = (maxY - minY) || 1;
        const w = 640, h = 160, pad = 12;
        const xy = pts.map((pt, i) => [
          pad + (i / Math.max(pts.length - 1, 1)) * (w - 2 * pad),
          h - pad - ((pt.equity - minY) / span) * (h - 2 * pad)
        ]);
        const poly  = xy.map(p => p[0].toFixed(1) + ',' + p[1].toFixed(1)).join(' ');
        const zeroY = h - pad - ((0 - minY) / span) * (h - 2 * pad);
        svg.innerHTML = `<line x1="0" y1="${zeroY}" x2="${w}" y2="${zeroY}" stroke="rgba(148,163,184,0.25)" stroke-dasharray="4"/>
          <polyline fill="none" stroke="#2dd4bf" stroke-width="2" points="${poly}"/>`;
      } else if (svg) {
        svg.innerHTML = '<text x="20" y="80" fill="#94a3b8" font-size="12">No closed trades in range</text>';
      }

      // Daily calendar
      const cal = AB.$('perfCalendar');
      if (cal) {
        const daily = p.daily || [];
        cal.innerHTML = daily.length ? daily.map(d => {
          const n  = Number(d.pnl) || 0;
          const bg = n > 0 ? 'rgba(45,212,191,0.2)' : (n < 0 ? 'rgba(248,113,113,0.2)' : 'rgba(148,163,184,0.1)');
          return `<div style="background:${bg};border-radius:8px;padding:8px;text-align:center">
            <div class="muted" style="font-size:10px">${d.day.slice(5)}</div>
            <div class="mono ${n > 0 ? 'pos' : n < 0 ? 'neg' : ''}" style="font-size:12px;font-weight:600">${n >= 0 ? '+' : ''}${AB.fmt(n, 1)}</div>
            <div class="muted" style="font-size:10px">${d.trades}t</div>
          </div>`;
        }).join('') : '<div class="empty">No daily data</div>';
      }

      // Trades table
      const box  = AB.$('perfTrades');
      if (box) {
        const rows = Array.isArray(trades) ? trades : [];
        if (!rows.length) {
          box.innerHTML = '<div class="empty">No trades in ledger yet</div>';
        } else {
          box.innerHTML = `<table style="width:100%;border-collapse:collapse;font-size:12px">
            <thead><tr class="muted" style="text-align:left">
              <th style="padding:6px">Status</th><th>Symbol</th><th>Route</th><th>Qty</th><th>PnL</th><th>Opened</th><th>Msg</th>
            </tr></thead>
            <tbody>${rows.map(t => {
              const pnl  = t.realizedPnlUsd;
              const pnlN = pnl != null ? Number(pnl) : null;
              const pnlS = pnlN != null ? ((pnlN >= 0 ? '+' : '') + AB.fmt(pnlN, 2)) : '—';
              const cls  = pnlN != null ? (pnlN > 0 ? 'pos' : pnlN < 0 ? 'neg' : '') : '';
              return `<tr style="border-top:1px solid rgba(148,163,184,0.12)">
                <td style="padding:6px">${t.status||'—'}</td>
                <td class="mono">${t.symbol||t.Symbol||'—'}</td>
                <td class="muted">${t.longExchange||t.LongExchange||'?'}→${t.shortExchange||t.ShortExchange||'?'}</td>
                <td class="mono">${AB.fmt(t.baseQty||t.BaseQty||0, 4)}</td>
                <td class="mono ${cls}">${pnlS}</td>
                <td class="muted">${(t.openedAt||t.OpenedAt||'').toString().slice(0,19)}</td>
                <td class="muted" style="max-width:180px;overflow:hidden;text-overflow:ellipsis">${t.message||t.Message||''}</td>
              </tr>`;
            }).join('')}</tbody></table>`;
        }
      }
    } catch (e) {
      k1.innerHTML = '<div class="empty neg">' + (e.message || e) + '</div>';
    }
  },

  // ── Live balances + positions (REST) ─────────────────────────────────────
  async loadLiveBalances() {
    const el = AB.$('r_liveBal');
    if (!el) return;
    el.innerHTML = '<div class="empty">Loading live balances…</div>';
    try {
      const data = await AB.api.get('/api/live/balances');
      const rows = data.exchanges || [];
      if (!rows.length) {
        el.innerHTML = '<div class="empty">No exchanges configured — add API keys in Settings</div>';
        return;
      }

      const cards = rows.map(x => {
        if (!x.ok) {
          return `<div class="kpi" style="margin:0;border-color:rgba(248,113,113,0.3)">
            <div class="kpi-l">${x.exchange} <span class="neg" style="font-size:10px">● error</span></div>
            <div class="neg" style="margin-top:6px;font-size:12px;word-break:break-word">${x.error || 'fail'}</div>
            ${x.hint ? `<div class="muted" style="font-size:10px;margin-top:4px">${x.hint}</div>` : ''}
          </div>`;
        }
        const usdt = x.usdtTotal != null ? x.usdtTotal : 0;
        const positions = Array.isArray(x.positions) ? x.positions.filter(p => p.quantity !== 0) : [];
        const posHtml = positions.length
          ? `<table style="width:100%;border-collapse:collapse;font-size:10px;margin-top:8px">
              <thead><tr class="muted">
                <th style="text-align:left;padding:2px 4px">Symbol</th>
                <th style="padding:2px 4px">Side</th>
                <th style="padding:2px 4px">Qty</th>
                <th style="padding:2px 4px">Entry</th>
                <th style="padding:2px 4px">uPnL</th>
                <th style="padding:2px 4px">Lev</th>
              </tr></thead>
              <tbody>${positions.map(p => {
                const pnl  = p.unrealizedPnl != null ? Number(p.unrealizedPnl) : null;
                const pnlC = pnl == null ? '' : (pnl >= 0 ? 'pos' : 'neg');
                const pnlS = pnl == null ? '—' : ((pnl >= 0 ? '+' : '') + AB.fmt(pnl, 2));
                const isLong = p.side === 'Buy' || p.side === 'Long';
                return `<tr style="border-top:1px solid rgba(148,163,184,0.1)">
                  <td class="mono" style="padding:2px 4px">${p.symbol || '—'}</td>
                  <td class="${isLong ? 'pos' : 'neg'}" style="padding:2px 4px">${p.side || '—'}</td>
                  <td class="mono" style="padding:2px 4px">${AB.fmt(p.quantity, 4)}</td>
                  <td class="mono" style="padding:2px 4px">${p.entryPrice ? AB.fmt(p.entryPrice, 2) : '—'}</td>
                  <td class="mono ${pnlC}" style="padding:2px 4px">${pnlS}</td>
                  <td class="muted" style="padding:2px 4px">${p.leverage ? p.leverage + 'x' : '—'}</td>
                </tr>`;
              }).join('')}</tbody>
            </table>`
          : `<div class="muted" style="font-size:11px;margin-top:8px">No open positions</div>`;

        return `<div class="kpi" style="margin:0;border-color:rgba(56,189,248,0.3)">
          <div class="kpi-l">${x.exchange} <span class="pos" style="font-size:10px">● live</span></div>
          <div class="mono" style="font-size:22px;font-weight:700;margin-top:4px;color:var(--blue)">${AB.fmt(usdt, 2)} <span class="muted" style="font-size:11px">USDT</span></div>
          <div class="muted mono" style="font-size:11px;margin-top:4px">perm: ${x.permission||'—'} · ${x.accountMode||'—'}</div>
          <div><span class="muted" style="font-size:11px">Positions (${positions.length})</span>${posHtml}</div>
        </div>`;
      }).join('');

      const sum = `<div class="kpi" style="margin:0;border-color:rgba(45,212,191,0.4)">
        <div class="kpi-l" style="color:#f87171;font-weight:700">🔴 LIVE TOTAL</div>
        <div class="mono" style="font-size:24px;font-weight:800;margin-top:4px;color:var(--accent)">${AB.fmt(data.totalUsdtApprox || 0, 2)} <span class="muted" style="font-size:12px">USDT</span></div>
        <div class="muted" style="font-size:11px;margin-top:4px">${data.utc ? new Date(data.utc).toLocaleTimeString() : '—'} UTC · ${rows.filter(r => r.ok).length}/${rows.length} exchanges OK</div>
      </div>`;

      el.innerHTML = sum + cards;
    } catch (e) {
      el.innerHTML = '<div class="empty neg">' + (e.message || e) + '</div>';
    }
  },

  onShow(d) {
    if (d) this.render(d);
    this.loadDays();
    this.loadPerformance(this._perfDays || 7);
    this.loadLiveBalances();
  }
};

// ── Event listeners ─────────────────────────────────────────────────────────
document.getElementById('btnRefreshLiveBal')
  ?.addEventListener('click', () => AB.pages.reports.loadLiveBalances());

document.querySelectorAll('[data-perf-days]').forEach(btn => {
  btn.addEventListener('click', () => {
    document.querySelectorAll('[data-perf-days]').forEach(b => b.classList.remove('active'));
    btn.classList.add('active');
    AB.pages.reports.loadPerformance(parseInt(btn.getAttribute('data-perf-days'), 10));
  });
});
