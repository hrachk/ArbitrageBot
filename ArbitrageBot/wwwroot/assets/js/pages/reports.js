// ── Reports page — чёткое разделение LIVE vs PAPER ────────────────────────

AB.pages.reports = {
  _perfDays: 7,

  // ── вызывается из SignalR при каждом тике ────────────────────────────────
  render(data) {
    if (!data) return;
    const mode = String(data.mode || 'PAPER').toUpperCase();
    // LIVE / LIVE_READONLY / LIVE-RO / LIVE_FULL — anything with LIVE
    const isLive = mode.indexOf('LIVE') >= 0 && mode.indexOf('PAPER') < 0;
    // In paper (and live-ro while paper engine still runs) show paper analytics
    const liveSection  = document.getElementById('rep_liveSection');
    const paperSection = document.getElementById('rep_paperSection');
    // Always keep BOTH visible: live block = real open hedges + balances; paper = paper stats
    if (liveSection)  liveSection.style.display  = '';
    if (paperSection) paperSection.style.display = '';

    this._renderOpenHedges(data);   // real open rows → r_posBody
    this._renderLive(data);
    this._renderPaper(data);
    this.renderFunding(data.fundingRates || data.FundingRates);
    this.renderHoldDecisions(data.livePositions);
  },

  /** Unified open hedges: paper positions + live ledger + exchange legs (no mock). */
  _renderOpenHedges(data) {
    const el = (id) => AB.$(id);
    const fp = data.futuresPaper || data.paper || {};
    const paper = Array.isArray(fp.positions) ? fp.positions : [];
    const lp = data.livePositions || {};
    const ledger = Array.isArray(lp.ledger) ? lp.ledger
                 : (Array.isArray(lp.open) ? lp.open : []);
    const legs = Array.isArray(lp.exchangeLegs) ? lp.exchangeLegs : [];

    const rows = [];
    paper.forEach(p => {
      rows.push({
        symbol: p.symbol || p.Symbol,
        route: (p.longExchange || p.LongExchange || '?') + ' → ' + (p.shortExchange || p.ShortExchange || '?'),
        qty: Number(p.baseQty || p.BaseQty || 0),
        upnl: p.unrealizedPnlUsd != null ? Number(p.unrealizedPnlUsd)
            : (p.unrealizedPnl != null ? Number(p.unrealizedPnl) : null),
        opened: p.openedAt || p.OpenedAt,
        src: 'paper'
      });
    });
    ledger.forEach(p => {
      rows.push({
        symbol: p.symbol || p.Symbol,
        route: (p.longExchange || p.LongExchange || '?') + ' → ' + (p.shortExchange || p.ShortExchange || '?'),
        qty: Number(p.baseQty || p.BaseQty || 0),
        upnl: p.unrealizedPnlUsd != null ? Number(p.unrealizedPnlUsd)
            : (p.unrealizedPnl != null ? Number(p.unrealizedPnl) : null),
        opened: p.openedAt || p.OpenedAt,
        src: 'ledger'
      });
    });
    legs.forEach(p => {
      const qty = Number(p.quantity || p.Quantity || 0);
      if (!qty) return;
      rows.push({
        symbol: p.symbol || p.Symbol,
        route: (p.exchange || p.Exchange || '?') + ' ' + (p.side || p.Side || ''),
        qty: qty,
        upnl: p.unrealizedPnl != null ? Number(p.unrealizedPnl) : null,
        opened: null,
        src: 'exchange'
      });
    });

    if (el('r_liveOpenCount'))
      el('r_liveOpenCount').textContent = rows.length + ' open · paper ' + paper.length +
        ' · ledger ' + ledger.length + ' · ex ' + legs.length;
    if (el('r_open')) el('r_open').textContent = rows.length;

    const body = el('r_posBody');
    if (!body) return;
    if (!rows.length) {
      body.innerHTML = '<tr><td colspan="5" class="muted" style="text-align:center;padding:20px">' +
        'No open hedges (paper + live ledger + exchange legs empty)</td></tr>';
      return;
    }
    body.innerHTML = rows.map(p => {
      const upnlS = p.upnl != null ? AB.fmtUsd(p.upnl) : '—';
      const upnlC = p.upnl != null ? (p.upnl >= 0 ? 'pos' : 'neg') : '';
      const opened = p.opened ? new Date(p.opened).toLocaleString() : '—';
      const badge = p.src === 'paper' ? '📄' : (p.src === 'ledger' ? '🔴' : '🏦');
      return '<tr>' +
        '<td class="mono">' + badge + ' ' + (p.symbol || '—') + '</td>' +
        '<td class="mono" style="font-size:11px">' + p.route + '</td>' +
        '<td class="mono">' + AB.fmt(p.qty, 6) + '</td>' +
        '<td class="mono ' + upnlC + '">' + upnlS + '</td>' +
        '<td class="muted">' + opened + '</td></tr>';
    }).join('');
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

    // Open hedges table filled by _renderOpenHedges (paper+ledger+exchange)

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
    if (el('r_paper_closes')) {
      const closedN = Array.isArray(trades)
        ? trades.filter(t => /clos/i.test(String(t.status || t.Status || ''))).length
        : 0;
      el('r_paper_closes').textContent = closedN || fp.closedCount || fp.tradeCount || 0;
    }
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

    // Skip reasons (aggregated, readable bars)
    const reasons = an.skipReasons || [];
    const totalSkips = reasons.reduce((s, r) => s + (Number(r.count) || 0), 0)
      || Number(an.skips) || 0;
    if (el('r_skipAggSub')) {
      el('r_skipAggSub').textContent = reasons.length
        ? (reasons.length + ' типов · ' + totalSkips.toLocaleString() + ' skips · клик свернуть')
        : 'нет данных';
    }
    if (el('r_skipReasons')) {
      if (!reasons.length) {
        el('r_skipReasons').innerHTML = '<div class="muted" style="padding:8px 0">No skips recorded yet</div>';
      } else {
        const maxC = Math.max(...reasons.map(r => Number(r.count) || 0), 1);
        const sorted = reasons.slice().sort((a, b) => (Number(b.count) || 0) - (Number(a.count) || 0));
        const top = sorted.slice(0, 12);
        el('r_skipReasons').innerHTML = top.map(r => {
          const c = Number(r.count) || 0;
          const pct = Math.round(c / maxC * 100);
          const reason = (r.reason || '—').toString();
          return `<div class="skip-row" title="${reason.replace(/"/g, '&quot;')} × ${c}">
            <div class="skip-reason">${reason}</div>
            <div class="skip-count">×${c.toLocaleString()}</div>
            <div class="skip-bar-track"><div class="skip-bar-fill" style="width:${pct}%"></div></div>
          </div>`;
        }).join('') + (sorted.length > 12
          ? `<div class="muted" style="font-size:11px;padding:6px 0">+ ещё ${sorted.length - 12} типов (см. recent)</div>`
          : '');
      }
    }

    // Recent skips (table, default collapsed)
    const skips = data.paperRecentSkips || [];
    if (el('r_skipRecentSub')) {
      el('r_skipRecentSub').textContent = skips.length
        ? (skips.length + ' последних · клик открыть')
        : 'пусто';
    }
    if (el('r_skipBody')) {
      el('r_skipBody').innerHTML = skips.length
        ? skips.slice(0, 40).map(s => {
            const t = s.utc ? new Date(s.utc).toISOString().slice(11, 19) : '—';
            const reason = (s.reason || '').toString();
            const short = reason.length > 64 ? reason.slice(0, 62) + '…' : reason;
            return `<tr title="${reason.replace(/"/g, '&quot;')}">
              <td class="muted mono">${t}</td>
              <td style="font-size:11px">${short}</td>
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
      const days = await AB.api.get('/api/analytics/days?maxDays=60');
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
      const trades = await AB.api.get('/api/analytics/trades?take=250');
      const pctStr = (v, dig) => {
        const n = Number(v);
        if (!isFinite(n)) return '';
        const s = (n > 0 ? '+' : '') + AB.fmt(n, dig ?? 2) + '%';
        return s;
      };
      const kpi = (label, val, cls, sub) =>
        `<div class="kpi" style="margin:0"><div class="kpi-l">${label}</div>
         <div class="mono ${cls||''}" style="font-size:18px;font-weight:700;margin-top:6px;line-height:1.15">${val}</div>
         ${sub ? `<div class="mono muted" style="font-size:11px;margin-top:3px;opacity:0.9">${sub}</div>` : ''}</div>`;
      const pn = (v, dig) => {
        const n = Number(v) || 0;
        return { s: (n >= 0 ? '+' : '') + AB.fmt(n, dig ?? 2), cls: n > 0 ? 'pos' : (n < 0 ? 'neg' : ''), n };
      };
      const net = pn(p.netPnl);
      const gross = pn(p.grossPnl);
      const fees = pn(-(Math.abs(Number(p.totalFees) || 0)));
      const fund = pn(p.totalFunding);
      const eqBase = Number(p.equityBase) || 40000;
      const netEq = pctStr(p.netPctOfEquity);
      const feeOfG = (Number(p.feesPctOfGross) || 0).toFixed(1) + '% of gross';
      const fundOfG = pctStr(p.fundingPctOfGross, 1) + ' of gross';
      const grossSub = '100% of gross · ' + pctStr(p.grossPctOfEquity) + ' eq';
      k1.innerHTML = [
        kpi('NET PNL', net.s + ' USDT', net.cls,
          (netEq ? netEq + ' of equity' : '') +
          (p.grossPnl != null
            ? `<div style="margin-top:6px;font-size:11px;line-height:1.45;font-weight:500">
                <span>Gross <b class="${gross.cls}">${gross.s}</b> <span class="muted">${grossSub}</span></span><br/>
                <span>Fees <b class="neg">${fees.s}</b> <span class="muted">${feeOfG}</span></span><br/>
                <span>Funding <b class="${fund.cls}">${fund.s}</b> <span class="muted">${fundOfG}</span></span>
              </div>`
            : '')),
        kpi('WIN RATE', AB.fmt(p.winRate, 1) + '%', '',
          (p.wins != null ? (p.wins + ' / ' + (p.totalTrades || 0) + ' closed') : '')),
        kpi('TOTAL TRADES', p.totalTrades, ''),
        kpi('AVG WIN', pn(p.avgWin).s, 'pos'),
      ].join('');
      k2.innerHTML = [
        kpi('AVG LOSS', pn(p.avgLoss).s, 'neg'),
        kpi('PROFIT FACTOR', AB.fmt(p.profitFactor, 2), '',
          p.netPctOfGross != null ? pctStr(p.netPctOfGross, 0) + ' net/gross' : ''),
        kpi('MAX DRAWDOWN', AB.fmt(p.maxDrawdown, 2), 'neg',
          p.maxDrawdownPct != null ? pctStr(Math.abs(Number(p.maxDrawdownPct)), 2) + ' of equity' : ''),
        kpi('BEST TRADE', p.bestTrade ? pn(p.bestTrade.pnl).s : '—', 'pos'),
        kpi('WORST TRADE', p.worstTrade ? pn(p.worstTrade.pnl).s : '—', 'neg'),
        kpi('AVG DURATION', AB.fmt(p.avgDurationMin, 1) + ' m', ''),
        kpi('EXPECTANCY', pn(p.expectancy).s, pn(p.expectancy).cls,
          p.expectancyPctOfSize != null ? pctStr(p.expectancyPctOfSize) + ' of size' : ''),
        kpi('AVG R:R', AB.fmt(p.avgRr, 2), ''),
        kpi('CONSEC WINS', p.consecWins, 'pos'),
        kpi('CONSEC LOSS', p.consecLoss, 'neg'),
        kpi('EQUITY BASE', AB.fmt(eqBase, 0) + ' USDT', '', 'PaperStartingQuote × venues'),
      ].join('');

      // Gross vs Net breakdown card (mockup Layer 2)
      const gnb = AB.$('repGrossNetBody');
      if (gnb) {
        gnb.innerHTML =
          `<div class="row"><span>Gross (leg A/B price Δ)</span><span><b class="${gross.cls}">${gross.s}</b> <span class="muted" style="font-size:10px">100% gross</span></span></div>
           <div class="row"><span>− Total fees</span><span><b class="neg">${fees.s}</b> <span class="muted" style="font-size:10px">${feeOfG}</span></span></div>
           <div class="row"><span>± Funding</span><span><b class="${fund.cls}">${fund.s}</b> <span class="muted" style="font-size:10px">${fundOfG}</span></span></div>
           <div class="row" style="border:0;padding-top:12px"><span style="color:#e8eef4;font-weight:600">= Net PnL</span>
             <span class="text-right"><b class="${net.cls}" style="font-size:18px">${net.s}</b>
             <div class="muted mono" style="font-size:11px">${netEq || ''} of equity · ${pctStr(p.netPctOfGross,1)} of gross</div></span></div>`;
      }
      const tctx = AB.$('repTimeCtx');
      if (tctx) {
        tctx.innerHTML = 'TimeRangeContext = { start: <strong>' + (p.fromUtc || '—') +
          '</strong>, end: <strong>' + (p.toUtc || '—') + '</strong> } · ' + d + 'D · equity base ' +
          AB.fmt(eqBase, 0) + ' USDT';
      }
      this._lastPerf = p;
      this._lastTrades = Array.isArray(trades) ? trades : [];
      this._fillFilterOptions(this._lastTrades);

      // Equity curve
      const pts = (p.equityCurve && p.equityCurve.length) ? p.equityCurve : [];
      const sub = AB.$('perfCurveSub');
      if (sub) sub.textContent = pts.length ? (pts.length + ' points · ' + d + 'D') : 'нет данных';
      this._drawEquityCanvas(pts);
      const svg = AB.$('perfCurve');
      if (svg) svg.innerHTML = '';

      // Daily heatmap (click → day filter)
      const cal = AB.$('perfCalendar');
      if (cal) {
        const daily = p.daily || [];
        const eqB = Number(p.equityBase) || 40000;
        const selDay = this._dayFilter || '';
        cal.innerHTML = daily.length ? daily.map(d0 => {
          const dayKey = (d0.day || d0.Day || '').toString();
          const n  = Number(d0.pnl) || 0;
          const tr = Number(d0.trades || d0.Trades || 0);
          const sc = Number(d0.scans || 0);
          const active = d0.hasActivity || tr > 0 || sc > 0;
          const pctEq = eqB > 0 ? (n / eqB * 100) : 0;
          const pctS = active ? ((pctEq >= 0 ? '+' : '') + pctEq.toFixed(2) + '%') : '';
          const bg = n > 0 ? 'rgba(34,197,94,0.18)' : (n < 0 ? 'rgba(239,68,68,0.18)' : (active ? 'rgba(148,163,184,0.12)' : 'rgba(148,163,184,0.05)'));
          const dayLbl = dayKey.slice(5);
          const sel = selDay && selDay === dayKey ? ' selected' : '';
          const tip = dayKey + ': PnL ' + (n >= 0 ? '+' : '') + AB.fmt(n, 2) +
            (pctS ? ' (' + pctS + ' eq)' : '') +
            ' · closes ' + tr + ' · scans ' + sc;
          return `<div class="day-cell${sel}" data-day="${dayKey}" style="background:${bg};opacity:${active ? 1 : 0.55}" title="${tip}">
            <div class="muted" style="font-size:10px">${dayLbl || '—'}</div>
            <div class="mono ${n > 0 ? 'pos' : n < 0 ? 'neg' : ''}" style="font-size:12px;font-weight:600">${active ? ((n >= 0 ? '+' : '') + AB.fmt(n, 1)) : '·'}</div>
            <div class="muted" style="font-size:9px">${tr ? tr + 't' : (sc ? sc + 's' : '—')}${pctS ? ' · ' + pctS : ''}</div>
          </div>`;
        }).join('') : '<div class="empty">No daily data</div>';
        cal.querySelectorAll('.day-cell[data-day]').forEach(cell => {
          cell.addEventListener('click', () => {
            const day = cell.getAttribute('data-day');
            this._dayFilter = (this._dayFilter === day) ? null : day;
            const lbl = AB.$('repDayFilterLbl');
            if (lbl) lbl.textContent = this._dayFilter
              ? ('Selected day: ' + this._dayFilter + ' · ledger отфильтрован')
              : '';
            this._renderTradeTable(this._filterTrades(this._lastTrades || []));
            cal.querySelectorAll('.day-cell').forEach(c => c.classList.toggle('selected', c.getAttribute('data-day') === this._dayFilter));
          });
        });
        if (p.fromUtc) {
          if (sub) sub.textContent = (p.equityCurve && p.equityCurve.length ? p.equityCurve.length + ' pts · ' : '') +
            p.fromUtc + ' → ' + (p.toUtc || '') + ' · ' + d + 'D';
        }
      }

      this._renderTradeTable(this._filterTrades(this._lastTrades));
    } catch (e) {
      k1.innerHTML = '<div class="empty neg">' + (e.message || e) + '</div>';
    }
  },

  _fillFilterOptions(trades) {
    const routes = new Set(), symbols = new Set();
    (trades || []).forEach(t => {
      const r = (t.longExchange || t.LongExchange || '?') + '→' + (t.shortExchange || t.ShortExchange || '?');
      routes.add(r);
      symbols.add(t.symbol || t.Symbol || '?');
    });
    const fill = (id, values) => {
      const el = AB.$(id);
      if (!el) return;
      const prev = new Set([...el.selectedOptions].map(o => o.value));
      el.innerHTML = [...values].sort().map(v =>
        `<option value="${v.replace(/"/g, '')}" ${prev.has(v) ? 'selected' : ''}>${v}</option>`).join('');
    };
    fill('repFilterRoute', routes);
    fill('repFilterSymbol', symbols);
  },

  _filterTrades(trades) {
    let rows = Array.isArray(trades) ? trades.slice() : [];
    if (this._dayFilter) {
      rows = rows.filter(t => {
        const op = (t.openedAt || t.OpenedAt || t.closedAt || t.ClosedAt || '').toString();
        return op.startsWith(this._dayFilter);
      });
    }
    const st = (AB.$('repFilterStatus') || {}).value || 'all';
    if (st === 'closed') rows = rows.filter(t => /close|tp|sl|stop|manual|converg/i.test(String(t.status || '')));
    if (st === 'open') rows = rows.filter(t => /^open$/i.test(String(t.status || '').trim()) || t.realizedPnlUsd == null);
    if (st === 'tp') rows = rows.filter(t => /tp|take/i.test(String(t.status || '') + String(t.message || '')));
    if (st === 'sl') rows = rows.filter(t => /sl|stop/i.test(String(t.status || '') + String(t.message || '')));
    const selRoutes = AB.$('repFilterRoute') ? [...AB.$('repFilterRoute').selectedOptions].map(o => o.value) : [];
    const selSyms = AB.$('repFilterSymbol') ? [...AB.$('repFilterSymbol').selectedOptions].map(o => o.value) : [];
    if (selRoutes.length) {
      rows = rows.filter(t => {
        const r = (t.longExchange || t.LongExchange || '?') + '→' + (t.shortExchange || t.ShortExchange || '?');
        return selRoutes.includes(r);
      });
    }
    if (selSyms.length) {
      rows = rows.filter(t => selSyms.includes(t.symbol || t.Symbol || '?'));
    }
    return rows;
  },

  _renderTradeTable(rows) {
    const box = AB.$('perfTrades');
    const tc = AB.$('tableCount');
    if (tc) tc.textContent = (rows ? rows.length : 0) + ' trades (filtered)';
    if (!box) return;
    if (!rows || !rows.length) {
      box.innerHTML = '<div class="empty" style="padding:20px">No trades for current filters</div>';
      return;
    }
    box.innerHTML = `<table style="width:100%;border-collapse:collapse;font-size:12px">
      <thead><tr class="muted" style="text-align:left">
        <th style="padding:8px 12px">Status</th><th>Symbol</th><th>Route</th><th>Qty</th><th>Net PnL</th><th>Fees</th><th>Opened</th><th>Msg</th>
      </tr></thead>
      <tbody>${rows.map(t => {
        const pnl  = t.realizedPnlUsd;
        const pnlN = pnl != null ? Number(pnl) : null;
        const pnlS = pnlN != null ? ((pnlN >= 0 ? '+' : '') + AB.fmt(pnlN, 2)) : '—';
        const cls  = pnlN != null ? (pnlN > 0 ? 'pos' : pnlN < 0 ? 'neg' : '') : '';
        const bq = Number(t.baseQty || t.BaseQty || 0);
        const le = Number(t.longEntry || t.LongEntry || 0);
        const notional = bq && le ? Math.abs(bq * le) : 0;
        const pctSz = (pnlN != null && notional > 0)
          ? ((pnlN / notional * 100 >= 0 ? '+' : '') + (pnlN / notional * 100).toFixed(2) + '%')
          : '';
        let fees = Number(t.openFeesUsd || t.OpenFeesUsd || 0) + Number(t.closeFeesUsd || t.CloseFeesUsd || 0);
        if (!fees && (t.message || t.Message)) {
          const msg = String(t.message || t.Message);
          const mo = msg.match(/openFee\s*=\s*([-+]?[0-9]*\.?[0-9]+)/i);
          const mc = msg.match(/closeFee\s*=\s*([-+]?[0-9]*\.?[0-9]+)/i);
          if (mo) fees += parseFloat(mo[1]) || 0;
          if (mc) fees += parseFloat(mc[1]) || 0;
        }
        const feesS = fees ? AB.fmt(fees, 2) : '—';
        return `<tr style="border-top:1px solid rgba(148,163,184,0.12)">
          <td style="padding:8px 12px"><span class="chip" style="font-size:10px">${t.status||'—'}</span></td>
          <td class="mono">${t.symbol||t.Symbol||'—'}</td>
          <td class="muted">${t.longExchange||t.LongExchange||'?'}→${t.shortExchange||t.ShortExchange||'?'}</td>
          <td class="mono">${AB.fmt(bq, 4)}</td>
          <td class="mono ${cls}">${pnlS}${pctSz ? '<div class="muted" style="font-size:10px">' + pctSz + ' size</div>' : ''}</td>
          <td class="mono muted">${feesS}</td>
          <td class="muted">${(t.openedAt||t.OpenedAt||'').toString().slice(0,19)}</td>
          <td class="muted" style="max-width:180px;overflow:hidden;text-overflow:ellipsis">${t.message||t.Message||''}</td>
        </tr>`;
      }).join('')}</tbody></table>`;
  },

  _exportTrades(fmt) {
    const rows = this._filterTrades(this._lastTrades || []);
    if (!rows.length) return;
    if (fmt === 'json') {
      const blob = new Blob([JSON.stringify(rows, null, 2)], { type: 'application/json' });
      const a = document.createElement('a');
      a.href = URL.createObjectURL(blob);
      a.download = 'trades-export.json';
      a.click();
      return;
    }
    const cols = ['status','symbol','route','qty','netPnl','fees','opened'];
    const lines = [cols.join(',')];
    rows.forEach(t => {
      const route = (t.longExchange||t.LongExchange||'?') + '→' + (t.shortExchange||t.ShortExchange||'?');
      let fees = Number(t.openFeesUsd || 0) + Number(t.closeFeesUsd || 0);
      lines.push([
        t.status||'', t.symbol||t.Symbol||'', route,
        t.baseQty||t.BaseQty||0, t.realizedPnlUsd ?? '', fees,
        (t.openedAt||t.OpenedAt||'')
      ].map(x => '"' + String(x).replace(/"/g,'""') + '"').join(','));
    });
    const blob = new Blob([lines.join('\n')], { type: 'text/csv' });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = 'trades-export.csv';
    a.click();
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


  _drawEquityCanvas(pts) {
    const canvas = document.getElementById('perfCurveCanvas');
    if (!canvas) return;
    const tip = document.getElementById('repTip');
    const ctx = canvas.getContext('2d');
    const dpr = window.devicePixelRatio || 1;
    const w = canvas.clientWidth || 640;
    const h = canvas.clientHeight || 220;
    canvas.width = w * dpr;
    canvas.height = h * dpr;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);
    if (!pts || !pts.length) {
      ctx.fillStyle = '#8b9cb3';
      ctx.font = '12px system-ui,sans-serif';
      ctx.fillText('No closed trades in range', 16, h / 2);
      canvas.onmousemove = null;
      return;
    }
    const ys = pts.map(x => Number(x.equity) || 0);
    let minY = Math.min(0, ...ys), maxY = Math.max(0, ...ys);
    if (minY === maxY) { minY -= 1; maxY += 1; }
    const span = maxY - minY;
    const pad = { t: 14, r: 10, b: 26, l: 48 };
    const plotW = w - pad.l - pad.r, plotH = h - pad.t - pad.b;
    const xAt = i => pad.l + (pts.length === 1 ? plotW / 2 : (i / (pts.length - 1)) * plotW);
    const yAt = v => pad.t + plotH - ((v - minY) / span) * plotH;

    ctx.strokeStyle = 'rgba(148,163,184,0.15)';
    ctx.fillStyle = '#8b9cb3';
    ctx.font = '10px system-ui,sans-serif';
    ctx.textAlign = 'right';
    for (let g = 0; g <= 4; g++) {
      const v = minY + (span * g) / 4;
      const y = yAt(v);
      ctx.beginPath(); ctx.moveTo(pad.l, y); ctx.lineTo(w - pad.r, y); ctx.stroke();
      ctx.fillText((v >= 0 ? '+' : '') + v.toFixed(1), pad.l - 4, y + 3);
    }
    const zeroY = yAt(0);
    ctx.strokeStyle = 'rgba(148,163,184,0.35)';
    ctx.setLineDash([4, 4]);
    ctx.beginPath(); ctx.moveTo(pad.l, zeroY); ctx.lineTo(w - pad.r, zeroY); ctx.stroke();
    ctx.setLineDash([]);

    ctx.beginPath();
    pts.forEach((pt, i) => {
      const x = xAt(i), y = yAt(Number(pt.equity) || 0);
      if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
    });
    ctx.strokeStyle = '#2dd4bf';
    ctx.lineWidth = 2;
    ctx.stroke();
    ctx.lineTo(xAt(pts.length - 1), pad.t + plotH);
    ctx.lineTo(xAt(0), pad.t + plotH);
    ctx.closePath();
    ctx.fillStyle = 'rgba(45,212,191,0.12)';
    ctx.fill();

    ctx.fillStyle = '#2dd4bf';
    const step = Math.max(1, Math.floor(pts.length / 20));
    pts.forEach((pt, i) => {
      if (i % step && i !== pts.length - 1) return;
      ctx.beginPath();
      ctx.arc(xAt(i), yAt(Number(pt.equity) || 0), 2.5, 0, Math.PI * 2);
      ctx.fill();
    });

    canvas._hits = pts.map((pt, i) => ({
      x: xAt(i),
      equity: Number(pt.equity) || 0,
      label: pt.t || pt.time || pt.day || ('#' + (i + 1))
    }));
    canvas.onmousemove = (e) => {
      const hits = canvas._hits || [];
      if (!hits.length || !tip) return;
      const rect = canvas.getBoundingClientRect();
      const x = e.clientX - rect.left;
      let best = null, bd = 1e9;
      hits.forEach(h => { const d0 = Math.abs(h.x - x); if (d0 < bd) { bd = d0; best = h; } });
      if (!best || bd > 28) { tip.classList.remove('show'); return; }
      tip.innerHTML = '<strong>' + best.label + '</strong>Equity: ' +
        (best.equity >= 0 ? '+' : '') + best.equity.toFixed(2) + ' USDT';
      tip.classList.add('show');
      tip.style.left = (e.clientX + 12) + 'px';
      tip.style.top = (e.clientY + 12) + 'px';
    };
    canvas.onmouseleave = () => { if (tip) tip.classList.remove('show'); };
  },

  onShow(d) {
    if (d) this.render(d);
    this.loadDays();
    this.loadPerformance(this._perfDays || 7);
    this.loadLiveBalances();
    if (!this._resizeBound) {
      this._resizeBound = true;
      window.addEventListener('resize', () => {
        clearTimeout(this._rz);
        this._rz = setTimeout(() => this.loadPerformance(this._perfDays || 7), 200);
      });
    }
  }
};

// ── Event listeners ─────────────────────────────────────────────────────────

// Skip reasons collapse toggles
(function bindSkipToggles() {
  const bind = (toggleId, bodyId, chevId, openLabel, closedLabel) => {
    const t = document.getElementById(toggleId);
    const b = document.getElementById(bodyId);
    const c = document.getElementById(chevId);
    if (!t || !b || t._skipBound) return;
    t._skipBound = true;
    t.addEventListener('click', () => {
      const collapsed = b.classList.toggle('skip-collapsed');
      if (c) c.textContent = collapsed ? '▸' : '▾';
    });
  };
  bind('skipAggToggle', 'skipAggBody', 'skipAggChev');
  bind('skipRecentToggle', 'skipRecentBody', 'skipRecentChev');
})();

document.getElementById('btnRefreshLiveBal')
  ?.addEventListener('click', () => AB.pages.reports.loadLiveBalances());

document.querySelectorAll('[data-perf-days]').forEach(btn => {
  btn.addEventListener('click', () => {
    document.querySelectorAll('[data-perf-days]').forEach(b => b.classList.remove('active'));
    btn.classList.add('active');
    AB.pages.reports._dayFilter = null;
    const lbl = document.getElementById('repDayFilterLbl');
    if (lbl) lbl.textContent = '';
    AB.pages.reports.loadPerformance(parseInt(btn.getAttribute('data-perf-days'), 10));
  });
});

document.getElementById('repFilterApply')?.addEventListener('click', () => {
  const r = AB.pages.reports;
  r._renderTradeTable(r._filterTrades(r._lastTrades || []));
});
document.getElementById('repFilterReset')?.addEventListener('click', () => {
  const r = AB.pages.reports;
  r._dayFilter = null;
  const st = document.getElementById('repFilterStatus');
  if (st) st.value = 'all';
  ['repFilterRoute', 'repFilterSymbol'].forEach(id => {
    const el = document.getElementById(id);
    if (el) [...el.options].forEach(o => { o.selected = false; });
  });
  const lbl = document.getElementById('repDayFilterLbl');
  if (lbl) lbl.textContent = '';
  r._renderTradeTable(r._filterTrades(r._lastTrades || []));
});
document.getElementById('repExportCsv')?.addEventListener('click', () => AB.pages.reports._exportTrades('csv'));
document.getElementById('repExportJson')?.addEventListener('click', () => AB.pages.reports._exportTrades('json'));

// ── Funding Rates panel (Live mode) ──────────────────────────────────────────
AB.pages.reports.renderFunding = function (fundingData) {
  const el = document.getElementById('r_fundingRates');
  if (!el || !fundingData) return;

  const rows = fundingData.symbols || [];
  if (!rows.length) {
    el.innerHTML = '<div class="empty">Funding rates loading… (5 min poll)</div>';
    return;
  }

  const fmt = (v, dig) => v != null ? (Number(v) * 100).toFixed(dig ?? 4) + '%' : '—';
  const fmtApr = (v) => v != null ? (Number(v) * 100).toFixed(1) + '%' : '—';
  const trendIcon = (t) => t === 'expanding' ? '↑' : (t === 'converging' ? '↓' : '—');

  el.innerHTML = `<div class="scroll" style="max-height:400px">
    <table class="data" style="font-size:11px">
      <thead><tr>
        <th>Symbol</th>
        <th>Best delta</th>
        <th>APR</th>
        <th>Route (L→S)</th>
        <th>Trend</th>
        <th>Next funding</th>
        ${(rows[0]?.rates || []).map(r => `<th>${r.exchange}</th>`).join('')}
      </tr></thead>
      <tbody>
        ${rows.map(row => {
          const d = row.bestDelta;
          const apr = d?.apr != null ? Number(d.apr) * 100 : null;
          const aprCls = apr != null ? (apr > 5 ? 'pos' : apr > 1 ? '' : 'muted') : '';
          const next = d?.nextFundingUtc ? new Date(d.nextFundingUtc) : null;
          const nextStr = next ? next.toLocaleTimeString([], {hour:'2-digit', minute:'2-digit'}) : '—';
          return `<tr>
            <td class="mono" style="color:var(--cyan)">${row.symbol}</td>
            <td class="mono ${d?.deltaRate > 0 ? 'pos' : 'muted'}">${fmt(d?.deltaRate)}</td>
            <td class="mono ${aprCls}" style="font-weight:${apr > 10 ? '700' : '400'}">${fmtApr(d?.apr)}</td>
            <td class="muted" style="font-size:10px">${d ? d.longExchange + '→' + d.shortExchange : '—'}</td>
            <td style="font-size:13px">${trendIcon(d?.trend)} <span class="muted" style="font-size:10px">${d?.trend || '—'}</span></td>
            <td class="muted mono" style="font-size:10px">${nextStr}</td>
            ${(row.rates || []).map(r => {
              const rateN = r.rate != null ? Number(r.rate) * 100 : null;
              const cls = rateN != null ? (rateN > 0.02 ? 'pos' : rateN < 0 ? 'neg' : '') : 'muted';
              return `<td class="mono ${cls}" style="font-size:10px">${rateN != null ? rateN.toFixed(4) + '%' : '—'}</td>`;
            }).join('')}
          </tr>`;
        }).join('')}
      </tbody>
    </table>
  </div>
  <div class="muted" style="font-size:10px;margin-top:6px">
    Обновляется каждые 5 минут · delta = rate(short) - rate(long) · APR = delta × 3 × 365 · trend: ↑ expanding / ↓ converging
  </div>`;
};

// ── Hold decisions для open live hedges ──────────────────────────────────────
AB.pages.reports.renderHoldDecisions = function (livePositions) {
  const el = document.getElementById('r_holdDecisions');
  if (!el) return;
  const ledger = Array.isArray(livePositions?.ledger) ? livePositions.ledger
               : Array.isArray(livePositions?.open)   ? livePositions.open : [];
  if (!ledger.length) {
    el.innerHTML = '<div class="empty muted">No open live hedges</div>';
    return;
  }

  el.innerHTML = ledger.map(p => {
    const accFunding = p.accumulatedFundingPnlUsd ?? p.AccumulatedFundingPnlUsd ?? 0;
    const pType = p.positionType ?? p.PositionType ?? 'Spatial';
    const hold = p.shouldHold ?? p.ShouldHold;
    const reason = p.lastHoldDecisionReason ?? p.LastHoldDecisionReason ?? '—';
    const pricePnl = p.unrealizedPricePnlUsd ?? 0;
    const totalPnl = Number(accFunding) + Number(pricePnl);

    return `<div class="kpi" style="margin:0;border-color:${hold === false ? 'rgba(248,113,113,0.5)' : 'rgba(45,212,191,0.3)'}">
      <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:6px">
        <span class="mono" style="color:var(--cyan);font-weight:700">${p.symbol || p.Symbol}</span>
        <span style="font-size:12px;padding:2px 8px;border-radius:12px;background:${hold === false ? 'rgba(248,113,113,0.15)' : 'rgba(45,212,191,0.15)'};color:${hold === false ? '#f87171' : '#2dd4bf'}">
          ${hold === false ? '⚡ CLOSE' : '⏳ HOLD'}
        </span>
      </div>
      <div style="display:flex;gap:16px;flex-wrap:wrap;font-size:11px">
        <div><span class="muted">Route</span><br/><b>${p.longExchange || p.LongExchange} → ${p.shortExchange || p.ShortExchange}</b></div>
        <div><span class="muted">Type</span><br/><b>${pType}</b></div>
        <div><span class="muted">Funding PnL</span><br/><b class="${accFunding >= 0 ? 'pos' : 'neg'}">${accFunding >= 0 ? '+' : ''}${Number(accFunding).toFixed(3)} USD</b></div>
        <div><span class="muted">Price PnL</span><br/><b class="${pricePnl >= 0 ? 'pos' : 'neg'}">${pricePnl >= 0 ? '+' : ''}${Number(pricePnl).toFixed(3)} USD</b></div>
        <div><span class="muted">Total PnL</span><br/><b class="${totalPnl >= 0 ? 'pos' : 'neg'}" style="font-size:13px;font-weight:700">${totalPnl >= 0 ? '+' : ''}${totalPnl.toFixed(2)} USD</b></div>
        <div><span class="muted">Funding periods</span><br/><b>${p.fundingPeriodsSettled ?? 0}</b></div>
      </div>
      <div class="muted" style="font-size:10px;margin-top:6px;border-top:1px solid rgba(148,163,184,0.1);padding-top:6px">${reason}</div>
    </div>`;
  }).join('');
};
