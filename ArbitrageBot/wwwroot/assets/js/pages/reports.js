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
        const sessR = Number(fp.realizedPnlUsd ?? fp.realizedPnl ?? 0);
        const expected = start * Math.max(n, 1) + sessR;
        const drift = sumEq - expected;
        el('r_margin').innerHTML = `<div class="kpi" style="margin:0;border-color:rgba(45,212,191,0.35)">
          <div class="kpi-l">ALL VENUES</div>
          <div class="mono" style="font-size:20px;font-weight:700;margin-top:6px;color:var(--accent)">${AB.fmt(sumEq, 2)} USDT</div>
          <div class="muted mono" style="font-size:11px;margin-top:6px;line-height:1.5">
            free ${AB.fmt(sumFree, 2)} · locked ${AB.fmt(sumLocked, 2)}<br/>
            start ${AB.fmt(start * Math.max(n, 1), 0)} (${AB.fmt(start, 0)}×${n}) · Δ wallet
            <span class="${totalDelta >= 0 ? 'pos' : 'neg'}">${totalDelta >= 0 ? '+' : ''}${AB.fmt(totalDelta, 2)}</span><br/>
            session realized <span class="${sessR >= 0 ? 'pos' : 'neg'}">${sessR >= 0 ? '+' : ''}${AB.fmt(sessR, 2)}</span>
            · expected ${AB.fmt(expected, 2)}
            ${Math.abs(drift) > 0.5 ? ' · drift <span class="neg">' + (drift >= 0 ? '+' : '') + AB.fmt(drift, 2) + '</span>' : ''}
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
        if (el('repSkipBars')) el('repSkipBars').innerHTML = '<div class="muted">No skips recorded yet</div>';
      } else {
        const maxC = Math.max(...reasons.map(r => Number(r.count) || 0), 1);
        const sorted = reasons.slice().sort((a, b) => (Number(b.count) || 0) - (Number(a.count) || 0));
        const top = sorted.slice(0, 12);
        const barsHtml = top.map(r => {
          const c = Number(r.count) || 0;
          const pct = Math.round(c / maxC * 100);
          const reason = (r.reason || '—').toString();
          return `<div class="bar-row" title="${reason.replace(/"/g, '&quot;')} × ${c}">
            <div>${reason.length > 28 ? reason.slice(0, 26) + '…' : reason}</div>
            <div class="bar-track"><div class="bar-fill" style="width:${pct}%"></div></div>
            <div class="n">${c >= 1000 ? (c / 1000).toFixed(1) + 'k' : c}</div>
          </div>`;
        }).join('') + (sorted.length > 12
          ? `<div class="muted" style="font-size:11px;padding:6px 0">+ ещё ${sorted.length - 12} типов</div>`
          : '');
        el('r_skipReasons').innerHTML = barsHtml;
        if (el('repSkipBars')) el('repSkipBars').innerHTML = barsHtml;
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
      const trades = await AB.api.get('/api/analytics/trades?take=5000&skip=0');
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
      const eqBase = Number(p.equityBase) || (Number(p.equityBasePerVenue) || 1000) * (Number(p.venueCount) || 6) || 6000;
      const netEq = pctStr(p.netPctOfEquity);
      const feeOfG = (Number(p.feesPctOfGross) || 0).toFixed(1) + '% of gross';
      const fundOfG = pctStr(p.fundingPctOfGross, 1) + ' of gross';
      const grossSub = '100% of gross · ' + pctStr(p.grossPctOfEquity) + ' eq';
      k1.innerHTML = [
        kpi('NET PNL', net.s + ' USDT', net.cls,
          (netEq ? netEq + ' of equity' : '') +
          (p.grossPnl != null
            ? `<div class="split-pnl">
                <span>Gross <b class="${gross.cls}">${gross.s}</b></span>
                <span>Fees <b class="neg">${fees.s}</b></span>
                <span>Funding <b class="${fund.cls}">${fund.s}</b></span>
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
        const eqB = Number(p.equityBase) || (Number(p.equityBasePerVenue) || 1000) * (Number(p.venueCount) || 6) || 6000;
        const selDay = this._dayFilter || '';
        const absMax = Math.max(...daily.map(x => Math.abs(Number(x.pnl) || 0)), 1);
        cal.innerHTML = daily.length ? daily.map(d0 => {
          const dayKey = (d0.day || d0.Day || '').toString();
          const n  = Number(d0.pnl) || 0;
          const tr = Number(d0.trades || d0.Trades || 0);
          const sc = Number(d0.scans || 0);
          const wr = d0.winRate != null ? Number(d0.winRate) : (d0.wins != null && tr ? (Number(d0.wins) / tr * 100) : null);
          const active = d0.hasActivity || tr > 0 || sc > 0 || n !== 0;
          const dayLbl = dayKey.length >= 10 ? dayKey.slice(8, 10) : (dayKey.slice(5) || '—');
          const sel = selDay && selDay === dayKey ? ' selected' : '';
          let hm = 'hm-empty';
          if (active && n > 0) hm = n / absMax > 0.66 ? 'hm-pos-3' : (n / absMax > 0.33 ? 'hm-pos-2' : 'hm-pos-1');
          else if (active && n < 0) hm = Math.abs(n) / absMax > 0.5 ? 'hm-neg-2' : 'hm-neg-1';
          else if (active) hm = '';
          const tip = dayKey + ': PnL ' + (n >= 0 ? '+' : '') + AB.fmt(n, 2) +
            ' · trades ' + tr + (wr != null ? ' · WR ' + wr.toFixed(0) + '%' : '');
          return `<div class="day-cell hm-cell ${hm}${sel}" data-day="${dayKey}" title="${tip}">
            <div class="muted" style="font-size:10px">${dayLbl}</div>
            <div class="p mono" style="font-size:13px;font-weight:700">${active ? ((n >= 0 ? '+' : '') + AB.fmt(n, 1)) : '—'}</div>
            <div class="muted" style="font-size:9px">${tr ? tr + 'tr' : '0 tr'}${wr != null && tr ? ' · ' + wr.toFixed(0) + '%' : ''}</div>
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

  _filterRoutes: [],
  _filterSymbols: [],

  _fillFilterOptions(trades) {
    const routes = new Set(), symbols = new Set();
    (trades || []).forEach(t => {
      const r = (t.longExchange || t.LongExchange || '?') + '→' + (t.shortExchange || t.ShortExchange || '?');
      routes.add(r);
      symbols.add(t.symbol || t.Symbol || '?');
    });
    this._allRoutes = [...routes].sort();
    this._allSymbols = [...symbols].sort();
    const fillAdd = (id, values, selected) => {
      const el = AB.$(id);
      if (!el) return;
      const sel = new Set(selected || []);
      el.innerHTML = '<option value="">+ add…</option>' +
        values.filter(v => !sel.has(v)).map(v =>
          `<option value="${String(v).replace(/"/g, '')}">${v}</option>`).join('');
    };
    fillAdd('repFilterRouteAdd', this._allRoutes, this._filterRoutes);
    fillAdd('repFilterSymbolAdd', this._allSymbols, this._filterSymbols);
    this._renderFilterChips();
  },

  _renderFilterChips() {
    const mk = (containerId, list, kind) => {
      const el = AB.$(containerId);
      if (!el) return;
      if (!list.length) {
        el.innerHTML = '<span class="muted" style="font-size:11px">all</span>';
        return;
      }
      el.innerHTML = list.map(v =>
        `<span class="rep-tag" data-kind="${kind}" data-val="${String(v).replace(/"/g, '')}">${v}<button type="button" aria-label="remove">×</button></span>`
      ).join('');
      el.querySelectorAll('.rep-tag button').forEach(btn => {
        btn.addEventListener('click', (e) => {
          e.stopPropagation();
          const tag = btn.closest('.rep-tag');
          const val = tag.getAttribute('data-val');
          const k = tag.getAttribute('data-kind');
          if (k === 'route') this._filterRoutes = this._filterRoutes.filter(x => x !== val);
          else this._filterSymbols = this._filterSymbols.filter(x => x !== val);
          this._fillFilterOptions(this._lastTrades || []);
          this._renderTradeTable(this._filterTrades(this._lastTrades || []));
        });
      });
    };
    mk('repRouteChips', this._filterRoutes, 'route');
    mk('repSymbolChips', this._filterSymbols, 'symbol');
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
    if (this._filterRoutes.length) {
      rows = rows.filter(t => {
        const r = (t.longExchange || t.LongExchange || '?') + '→' + (t.shortExchange || t.ShortExchange || '?');
        return this._filterRoutes.includes(r);
      });
    }
    if (this._filterSymbols.length) {
      rows = rows.filter(t => this._filterSymbols.includes(t.symbol || t.Symbol || '?'));
    }
    return rows;
  },

  _statusBadge(status, msg) {
    const s = String(status || '') + ' ' + String(msg || '');
    if (/open/i.test(status) && !/close/i.test(status)) return { cls: 'st-open', label: 'Open' };
    if (/tp|take.?profit|converg/i.test(s)) return { cls: 'st-tp', label: /converg/i.test(s) ? 'Closed (converge)' : 'Closed (TP)' };
    if (/sl|stop/i.test(s)) return { cls: 'st-sl', label: 'Closed (SL)' };
    if (/manual/i.test(s)) return { cls: 'st-manual', label: 'Closed (manual)' };
    if (/close/i.test(s)) return { cls: 'st-tp', label: status || 'Closed' };
    return { cls: 'st-open', label: status || '—' };
  },

  _parseTradeFees(t) {
    let openF = Number(t.openFeesUsd || t.OpenFeesUsd || 0);
    let closeF = Number(t.closeFeesUsd || t.CloseFeesUsd || 0);
    if (!openF && !closeF && (t.message || t.Message)) {
      const msg = String(t.message || t.Message);
      const mo = msg.match(/openFee\s*=\s*([-+]?[0-9]*\.?[0-9]+)/i);
      const mc = msg.match(/closeFee\s*=\s*([-+]?[0-9]*\.?[0-9]+)/i);
      if (mo) openF = parseFloat(mo[1]) || 0;
      if (mc) closeF = parseFloat(mc[1]) || 0;
    }
    return { openF, closeF, total: openF + closeF };
  },

  _renderTradeTable(rows) {
    const box = AB.$('perfTrades');
    const tc = AB.$('tableCount');
    if (tc) tc.textContent = (rows ? rows.length : 0) + ' trades · click → legs';
    if (!box) return;
    if (!rows || !rows.length) {
      box.innerHTML = '<div class="empty" style="padding:20px">No trades for current filters</div>';
      return;
    }

    const cards = rows.map((t, i) => {
      const id = 'trcard_' + i;
      const st = this._statusBadge(t.status, t.message || t.Message);
      const sym = t.symbol || t.Symbol || '—';
      const longEx = t.longExchange || t.LongExchange || '?';
      const shortEx = t.shortExchange || t.ShortExchange || '?';
      const route = longEx + '→' + shortEx;
      const bq = Number(t.baseQty || t.BaseQty || 0);
      const le = Number(t.longEntry || t.LongEntry || 0);
      const se = Number(t.shortEntry || t.ShortEntry || 0);
      const lx = Number(t.longExit || t.LongExit || 0);
      const sx = Number(t.shortExit || t.ShortExit || 0);
      const notional = bq && le ? Math.abs(bq * le) : (Number(t.notionalUsd || t.sizeUsd) || 0);
      const sizeS = notional ? ('$' + AB.fmt(notional, 0)) : AB.fmt(bq, 4);
      const fees = this._parseTradeFees(t);
      const fund = Number(t.fundingUsd || t.FundingUsd || 0);
      const pnlN = t.realizedPnlUsd != null ? Number(t.realizedPnlUsd) : null;
      const pnlS = pnlN != null ? ((pnlN >= 0 ? '+' : '') + AB.fmt(pnlN, 2)) : '—';
      const pnlCls = pnlN != null ? (pnlN > 0 ? 'pos' : pnlN < 0 ? 'neg' : '') : 'muted';
      const grossN = t.grossPnlUsd != null ? Number(t.grossPnlUsd)
        : (pnlN != null ? pnlN + fees.total - fund : null);
      const grossS = grossN != null ? ((grossN >= 0 ? '+' : '') + AB.fmt(grossN, 2)) : '—';
      const opened = (t.openedAt || t.OpenedAt || '').toString().replace('T', ' ').slice(0, 16);
      let dur = t.durationMin != null ? AB.fmt(t.durationMin, 1) + 'm' : '—';
      if (dur === '—' && t.openedAt && t.closedAt) {
        const ms = new Date(t.closedAt) - new Date(t.openedAt);
        if (isFinite(ms) && ms > 0) dur = AB.fmt(ms / 60000, 1) + 'm';
      }
      const msg = String(t.message || t.Message || '');
      const slipM = msg.match(/slip(?:page)?[^\d]*([0-9]*\.?[0-9]+)/i);
      const slip = slipM ? slipM[1] + ' bps' : (t.slippageBps != null ? t.slippageBps + ' bps' : '—');

      return `<div class="tl-card" data-id="${id}">
        <div class="tl-row" role="button" tabindex="0">
          <div class="tl-c st-c"><span class="st ${st.cls}">${st.label}</span></div>
          <div class="tl-c mono tl-sym">${sym}</div>
          <div class="tl-c muted tl-route">${route}</div>
          <div class="tl-c mono">${sizeS}</div>
          <div class="tl-c mono ${grossN > 0 ? 'pos' : grossN < 0 ? 'neg' : 'muted'}">${grossS}</div>
          <div class="tl-c mono muted">${fees.total ? AB.fmt(fees.total, 2) : '—'}</div>
          <div class="tl-c mono muted">${fund ? ((fund >= 0 ? '+' : '') + AB.fmt(fund, 2)) : '—'}</div>
          <div class="tl-c mono ${pnlCls} tl-pnl">${pnlS}</div>
          <div class="tl-c muted">${dur}</div>
          <div class="tl-c muted mono tl-time">${opened}</div>
          <div class="tl-c tl-chev">▸</div>
        </div>
        <div class="tl-detail" id="${id}" hidden>
          <div class="acc-grid">
            <div>
              <div class="acc-h">Leg A · Long ${longEx}</div>
              Entry <code>${le ? AB.fmt(le, 6) : '—'}</code>${lx ? ' · Exit <code>' + AB.fmt(lx, 6) + '</code>' : ''}<br/>
              Open fee <code>${fees.openF ? AB.fmt(fees.openF, 2) : '—'}</code> USDT
            </div>
            <div>
              <div class="acc-h">Leg B · Short ${shortEx}</div>
              Entry <code>${se ? AB.fmt(se, 6) : '—'}</code>${sx ? ' · Exit <code>' + AB.fmt(sx, 6) + '</code>' : ''}<br/>
              Close fee <code>${fees.closeF ? AB.fmt(fees.closeF, 2) : '—'}</code> USDT
            </div>
            <div class="tl-note">
              Slippage <code>${slip}</code> · Qty <code>${AB.fmt(bq, 4)}</code>
              ${msg ? ' · ' + msg.slice(0, 140) : ''}
            </div>
          </div>
        </div>
      </div>`;
    }).join('');

    box.innerHTML =
      `<div class="tl-head">
        <div class="tl-c">Status</div><div class="tl-c">Symbol</div><div class="tl-c">Route</div>
        <div class="tl-c">Size</div><div class="tl-c">Gross</div><div class="tl-c">Fees</div>
        <div class="tl-c">Fund</div><div class="tl-c">Net PnL</div><div class="tl-c">Dur</div>
        <div class="tl-c">Opened</div><div class="tl-c"></div>
      </div>
      <div class="tl-list">${cards}</div>`;

    box.querySelectorAll('.tl-card .tl-row').forEach(row => {
      row.addEventListener('click', () => {
        const card = row.closest('.tl-card');
        const det = card.querySelector('.tl-detail');
        const open = !det.hidden;
        box.querySelectorAll('.tl-detail').forEach(d => { d.hidden = true; });
        box.querySelectorAll('.tl-card.open').forEach(c => c.classList.remove('open'));
        box.querySelectorAll('.tl-chev').forEach(c => { c.textContent = '▸'; });
        if (!open) {
          det.hidden = false;
          card.classList.add('open');
          const chev = row.querySelector('.tl-chev');
          if (chev) chev.textContent = '▾';
        }
      });
    });
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
      const fees = this._parseTradeFees(t).total;
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

  _generatePdf() {
    const p = this._lastPerf || {};
    const rows = this._filterTrades(this._lastTrades || []);
    const fund = (document.getElementById('pdfFund') || {}).value || 'ArbitrageBot';
    const inv = (document.getElementById('pdfInvestor') || {}).value || '—';
    const note = (document.getElementById('pdfNote') || {}).value || '';
    const esc = (s) => String(s || '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
    const net = Number(p.netPnl) || 0;
    const fees = Number(p.totalFees) || 0;
    const gross = Number(p.grossPnl) || (net + fees);
    const funding = Number(p.totalFunding) || 0;
    const eq = Number(p.equityBase) || 0;
    const netPctEq = eq > 0 ? (net / eq * 100) : 0;
    const feesPctG = gross !== 0 ? (Math.abs(fees) / Math.abs(gross) * 100) : 0;
    const genAt = new Date().toISOString().slice(0, 19) + 'Z';
    const from = p.fromUtc || '—';
    const to = p.toUtc || '—';

    const daily = p.daily || [];
    const dayRows = daily.filter(d => d.hasActivity || Number(d.pnl) || Number(d.trades)).map(d0 => {
      const n = Number(d0.pnl) || 0;
      const tr = Number(d0.trades || d0.Trades || 0);
      return `<tr>
        <td>${esc(d0.day || d0.Day || '')}</td>
        <td class="${n >= 0 ? 'pos' : 'neg'}" style="text-align:right">${n >= 0 ? '+' : ''}${n.toFixed(2)}</td>
        <td style="text-align:right">${tr}</td>
      </tr>`;
    }).join('');

    const tradeRows = rows.slice(0, 120).map(t => {
      const feesT = this._parseTradeFees(t).total;
      const pnl = t.realizedPnlUsd != null ? Number(t.realizedPnlUsd) : null;
      const st = this._statusBadge(t.status, t.message || t.Message).label;
      const route = (t.longExchange || t.LongExchange || '?') + '→' + (t.shortExchange || t.ShortExchange || '?');
      const bq = Number(t.baseQty || t.BaseQty || 0);
      const le = Number(t.longEntry || t.LongEntry || 0);
      const size = bq && le ? Math.abs(bq * le) : 0;
      return `<tr>
        <td>${esc(st)}</td>
        <td><b>${esc(t.symbol || t.Symbol || '')}</b></td>
        <td>${esc(route)}</td>
        <td style="text-align:right">${size ? size.toFixed(0) : '—'}</td>
        <td class="${pnl != null && pnl >= 0 ? 'pos' : 'neg'}" style="text-align:right">${pnl != null ? ((pnl >= 0 ? '+' : '') + pnl.toFixed(2)) : '—'}</td>
        <td style="text-align:right">${feesT ? feesT.toFixed(2) : '—'}</td>
        <td>${esc((t.openedAt || t.OpenedAt || '').toString().replace('T', ' ').slice(0, 16))}</td>
      </tr>`;
    }).join('');

    const html = `<!DOCTYPE html><html><head><meta charset="utf-8"/>
<title>${esc(fund)} — Performance Statement</title>
<style>
  @page{size:A4;margin:16mm}
  *{box-sizing:border-box}
  body{font-family:Inter,Segoe UI,system-ui,sans-serif;color:#0f172a;margin:0;padding:28px 32px;background:#fff}
  .hdr{display:flex;justify-content:space-between;align-items:flex-start;border-bottom:2px solid #0f172a;padding-bottom:16px;margin-bottom:20px}
  .brand{font-size:11px;letter-spacing:.12em;text-transform:uppercase;color:#64748b;margin-bottom:4px}
  h1{font-size:22px;margin:0;font-weight:700;letter-spacing:-.02em}
  .meta{text-align:right;font-size:11px;color:#64748b;line-height:1.55}
  .meta b{color:#0f172a;font-weight:600}
  .sec{margin:22px 0 10px;font-size:12px;font-weight:700;text-transform:uppercase;letter-spacing:.06em;color:#334155}
  .kpi{display:grid;grid-template-columns:repeat(4,1fr);gap:10px;margin:8px 0 6px}
  .kpi>div{border:1px solid #e2e8f0;border-radius:10px;padding:12px 14px;background:#fafbfc}
  .kpi .l{font-size:9px;text-transform:uppercase;letter-spacing:.05em;color:#64748b}
  .kpi .v{font-size:17px;font-weight:700;margin-top:4px;font-variant-numeric:tabular-nums}
  .kpi .s{font-size:10px;color:#94a3b8;margin-top:2px}
  .pos{color:#15803d}.neg{color:#b91c1c}
  .split{display:grid;grid-template-columns:1.1fr .9fr;gap:16px;margin-top:8px}
  .box{border:1px solid #e2e8f0;border-radius:10px;padding:14px}
  .box h3{margin:0 0 10px;font-size:12px;text-transform:uppercase;letter-spacing:.04em;color:#64748b}
  .row{display:flex;justify-content:space-between;padding:7px 0;border-bottom:1px solid #f1f5f9;font-size:12px}
  .row:last-child{border:0;padding-top:10px;font-weight:700;font-size:14px}
  table{width:100%;border-collapse:collapse;font-size:10.5px;margin-top:6px}
  th{text-align:left;border-bottom:2px solid #e2e8f0;padding:8px 6px;color:#64748b;font-size:9px;text-transform:uppercase;letter-spacing:.04em}
  td{border-bottom:1px solid #f1f5f9;padding:6px;font-variant-numeric:tabular-nums;vertical-align:top}
  .note{margin-top:24px;padding:12px 14px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;font-size:10.5px;color:#475569;line-height:1.5}
  .foot{margin-top:20px;font-size:9px;color:#94a3b8;border-top:1px solid #e2e8f0;padding-top:10px}
  .noprint{margin-top:18px}
  .noprint button{background:#0f172a;color:#fff;border:0;border-radius:8px;padding:10px 16px;font-size:13px;cursor:pointer}
  @media print{body{padding:0}.noprint{display:none}}
</style></head><body>
  <div class="hdr">
    <div>
      <div class="brand">Cross-exchange arbitrage · performance statement</div>
      <h1>${esc(fund)}</h1>
    </div>
    <div class="meta">
      Prepared for: <b>${esc(inv)}</b><br/>
      Period: <b>${esc(from)}</b> → <b>${esc(to)}</b><br/>
      Generated: ${esc(genAt)}<br/>
      Mode: Paper / Demo · filtered ledger
    </div>
  </div>

  <div class="sec">Executive summary</div>
  <div class="kpi">
    <div><div class="l">Net PnL</div><div class="v ${net>=0?'pos':'neg'}">${net>=0?'+':''}${net.toFixed(2)} <span style="font-size:11px">USDT</span></div>
      <div class="s">${netPctEq>=0?'+':''}${netPctEq.toFixed(2)}% of equity base</div></div>
    <div><div class="l">Win rate</div><div class="v">${(Number(p.winRate)||0).toFixed(1)}%</div>
      <div class="s">${p.wins!=null?(p.wins+' / '+(p.totalTrades||0)+' closed'):'—'}</div></div>
    <div><div class="l">Total trades</div><div class="v">${p.totalTrades||rows.length}</div>
      <div class="s">in selected window / filters</div></div>
    <div><div class="l">Profit factor</div><div class="v">${(Number(p.profitFactor)||0).toFixed(2)}</div>
      <div class="s">max DD ${(Number(p.maxDrawdown)||0).toFixed(2)} USDT</div></div>
  </div>

  <div class="split">
    <div class="box">
      <h3>Gross vs Net breakdown</h3>
      <div class="row"><span>Gross (leg A/B price Δ)</span><span class="${gross>=0?'pos':'neg'}">${gross>=0?'+':''}${gross.toFixed(2)}</span></div>
      <div class="row"><span>− Total fees (open+close)</span><span class="neg">−${Math.abs(fees).toFixed(2)} <span style="color:#94a3b8;font-weight:400">(${feesPctG.toFixed(1)}% gross)</span></span></div>
      <div class="row"><span>± Funding</span><span>${funding>=0?'+':''}${funding.toFixed(2)}</span></div>
      <div class="row"><span>= Net PnL</span><span class="${net>=0?'pos':'neg'}">${net>=0?'+':''}${net.toFixed(2)} USDT</span></div>
    </div>
    <div class="box">
      <h3>Risk &amp; expectancy</h3>
      <div class="row"><span>Equity base</span><span>${eq.toFixed(0)} USDT</span></div>
      <div class="row"><span>Avg win / avg loss</span><span>${(Number(p.avgWin)||0).toFixed(2)} / ${(Number(p.avgLoss)||0).toFixed(2)}</span></div>
      <div class="row"><span>Expectancy</span><span>${(Number(p.expectancy)||0)>=0?'+':''}${(Number(p.expectancy)||0).toFixed(2)}</span></div>
      <div class="row"><span>Avg duration</span><span>${(Number(p.avgDurationMin)||0).toFixed(1)} min</span></div>
    </div>
  </div>

  ${dayRows ? `<div class="sec">Daily realized</div>
  <table><thead><tr><th>Day (UTC)</th><th style="text-align:right">Net PnL</th><th style="text-align:right">Trades</th></tr></thead>
  <tbody>${dayRows}</tbody></table>` : ''}

  <div class="sec">Trade ledger (${Math.min(rows.length, 120)}${rows.length > 120 ? ' of ' + rows.length : ''})</div>
  <table>
    <thead><tr>
      <th>Status</th><th>Symbol</th><th>Route</th><th style="text-align:right">Size</th>
      <th style="text-align:right">Net</th><th style="text-align:right">Fees</th><th>Opened</th>
    </tr></thead>
    <tbody>${tradeRows || '<tr><td colspan="7">No trades in range</td></tr>'}</tbody>
  </table>

  <div class="note"><b>Disclaimer / notes</b><br/>${esc(note)}</div>
  <div class="foot">ArbitrageBot · paper analytics · not investment advice · figures from local ledger for selected TimeRange and filters.</div>
  <p class="noprint" style="margin-top:18px;padding:12px;background:#f1f5f9;border-radius:8px;font-size:12px;color:#334155">
    <b>Как сохранить PDF:</b> откройте этот файл в браузере → Ctrl+P (Cmd+P) → принтер «Save as PDF» / «Сохранить как PDF».
  </p>
</body></html>`;

    // Download as .html — no pop-up (browser print → Save as PDF)
    const stamp = genAt.replace(/[:Z]/g, '-').slice(0, 15);
    const safeName = String(fund).replace(/[^\w\-]+/g, '_').slice(0, 40) || 'ArbitrageBot';
    const filename = 'statement-' + safeName + '-' + stamp + '.html';
    const blob = new Blob([html], { type: 'text/html;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    a.rel = 'noopener';
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 2000);

    // Optional toast in UI
    const hint = document.getElementById('repPdfHint');
    if (hint) {
      hint.textContent = 'Скачан: ' + filename + ' · откройте файл → Ctrl+P → Save as PDF';
      hint.style.display = 'block';
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
  r._filterRoutes = [];
  r._filterSymbols = [];
  const st = document.getElementById('repFilterStatus');
  if (st) st.value = 'all';
  const mode = document.getElementById('repFilterMode');
  if (mode) mode.value = 'paper';
  const lbl = document.getElementById('repDayFilterLbl');
  if (lbl) lbl.textContent = '';
  r._fillFilterOptions(r._lastTrades || []);
  r._renderTradeTable(r._filterTrades(r._lastTrades || []));
});
document.getElementById('repFilterRouteAdd')?.addEventListener('change', (e) => {
  const v = e.target.value;
  if (!v) return;
  const r = AB.pages.reports;
  if (!r._filterRoutes.includes(v)) r._filterRoutes.push(v);
  e.target.value = '';
  r._fillFilterOptions(r._lastTrades || []);
  r._renderTradeTable(r._filterTrades(r._lastTrades || []));
});
document.getElementById('repFilterSymbolAdd')?.addEventListener('change', (e) => {
  const v = e.target.value;
  if (!v) return;
  const r = AB.pages.reports;
  if (!r._filterSymbols.includes(v)) r._filterSymbols.push(v);
  e.target.value = '';
  r._fillFilterOptions(r._lastTrades || []);
  r._renderTradeTable(r._filterTrades(r._lastTrades || []));
});
document.getElementById('repFilterStatus')?.addEventListener('change', () => {
  const r = AB.pages.reports;
  r._renderTradeTable(r._filterTrades(r._lastTrades || []));
});
const openPdfModal = () => {
  const m = document.getElementById('repPdfModal');
  if (m) {
    m.classList.add('show');
    m.style.display = 'flex';
  }
};
const closePdfModal = () => {
  const m = document.getElementById('repPdfModal');
  if (m) {
    m.classList.remove('show');
    m.style.display = 'none';
  }
};
['repExportCsv', 'repExportCsvTop'].forEach(id =>
  document.getElementById(id)?.addEventListener('click', () => AB.pages.reports._exportTrades('csv')));
['repExportJson', 'repExportJsonTop'].forEach(id =>
  document.getElementById(id)?.addEventListener('click', () => AB.pages.reports._exportTrades('json')));
['repExportPdf', 'repExportPdfTop'].forEach(id =>
  document.getElementById(id)?.addEventListener('click', openPdfModal));
document.getElementById('repPdfCancel')?.addEventListener('click', closePdfModal);
document.getElementById('repPdfGo')?.addEventListener('click', () => {
  closePdfModal();
  AB.pages.reports._generatePdf();
});
document.getElementById('repPdfModal')?.addEventListener('click', (e) => {
  if (e.target.id === 'repPdfModal') closePdfModal();
});

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
