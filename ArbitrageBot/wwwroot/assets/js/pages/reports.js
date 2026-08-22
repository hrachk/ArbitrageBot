AB.pages.reports = {
  async loadDays() {
    try {
      const days = await AB.api.get('/api/analytics/days?maxDays=10');
      const body = AB.$('r_daysBody');
      if (!body) return;
      body.innerHTML = (days || []).map(d => {
        const day = d.dayUtc || d.DayUtc || '—';
        return `<tr>
          <td class="mono">${day}</td>
          <td class="mono">${d.scans ?? d.Scans ?? 0}</td>
          <td class="mono">${d.opens ?? d.Opens ?? 0}</td>
          <td class="mono">${d.closes ?? d.Closes ?? 0}</td>
          <td class="mono">${d.skips ?? d.Skips ?? 0}</td>
          <td>${AB.fmtUsd(d.realizedPnlUsd ?? d.RealizedPnlUsd ?? 0)}</td>
          <td class="mono">${AB.fmt(d.bestOpenPctSeen ?? d.BestOpenPctSeen, 3)}%</td>
        </tr>`;
      }).join('') || '<tr><td colspan="7" class="muted" style="text-align:center">No history yet</td></tr>';
    } catch (e) {
      console.warn(e);
    }
  },
  render(data) {
    const fp = data.futuresPaper || data.paper || {};
    const trades = fp.trades || [];
    const positions = fp.positions || [];

    AB.$('r_realized').innerHTML = AB.fmtUsd(fp.realizedPnlUsd ?? fp.realizedPnl);
    AB.$('r_day').innerHTML = AB.fmtUsd(fp.dailyRealizedPnlUsd);
    AB.$('r_attempts').textContent = fp.tradeCount ?? fp.tradeAttempts ?? 0;
    AB.$('r_open').textContent = positions.length;
    AB.$('r_lev').textContent = (fp.leverage || 5) + 'x';
    AB.$('r_stop').textContent = fp.stopLossUsd ?? '—';

    // margins: free vs locked vs equity
    const bd = fp.marginBreakdown || {};
    const bal = fp.margin || fp.balances || {};
    const start = fp.paperStartingQuote || 50000;
    if (AB.$('r_margin')) {
      if (Object.keys(bd).length) {
        let sumEq = 0, sumFree = 0, sumLocked = 0, n = 0;
        const cards = Object.entries(bd).map(([ex, m]) => {
          const free = Number(m.free ?? 0);
          const locked = Number(m.locked ?? 0);
          const equity = Number(m.equity ?? free + locked);
          const delta = Number(m.deltaFromStart ?? equity - start);
          sumEq += equity; sumFree += free; sumLocked += locked; n++;
          return `<div class="kpi" style="margin:0">
            <div class="kpi-l">${ex} · итоговый баланс</div>
            <div class="mono" style="font-size:18px;font-weight:700;margin-top:6px">${AB.fmt(equity, 2)} <span class="muted" style="font-size:11px">USDT</span></div>
            <div class="muted mono" style="font-size:11px;margin-top:6px;line-height:1.5">
              free ${AB.fmt(free, 2)} · locked ${AB.fmt(locked, 2)}<br/>
              start ${AB.fmt(start, 0)} · Δ <span class="${delta>=0?'pos':'neg'}">${delta >= 0 ? '+' : ''}${AB.fmt(delta, 2)}</span>
            </div>
          </div>`;
        }).join('');
        const totalStart = start * Math.max(n, 1);
        const totalDelta = sumEq - totalStart;
        const totalCard = `<div class="kpi" style="margin:0;border-color:rgba(45,212,191,0.35)">
            <div class="kpi-l">ALL VENUES · total equity</div>
            <div class="mono" style="font-size:20px;font-weight:700;margin-top:6px;color:var(--accent)">${AB.fmt(sumEq, 2)} USDT</div>
            <div class="muted mono" style="font-size:11px;margin-top:6px;line-height:1.5">
              free ${AB.fmt(sumFree, 2)} · locked ${AB.fmt(sumLocked, 2)}<br/>
              start ${AB.fmt(totalStart, 0)} · Δ <span class="${totalDelta>=0?'pos':'neg'}">${totalDelta >= 0 ? '+' : ''}${AB.fmt(totalDelta, 2)}</span>
              · realized ${AB.fmt(fp.realizedPnlUsd ?? fp.realizedPnl ?? 0, 2)} · <b>истина = Δ equity</b>
            </div>
          </div>`;
        AB.$('r_margin').innerHTML = totalCard + cards;
      } else if (Object.keys(bal).length) {
        AB.$('r_margin').innerHTML = Object.entries(bal).map(([ex, v]) => {
          const usdt = typeof v === 'object' ? (v.USDT ?? Object.values(v)[0]) : v;
          return `<div class="kpi" style="margin:0"><div class="kpi-l">${ex}</div>
            <div class="mono" style="font-size:16px;margin-top:6px">${AB.fmt(usdt, 2)} free</div>
            <div class="muted" style="font-size:11px;margin-top:4px">start ${AB.fmt(start, 0)} (locked not shown)</div></div>`;
        }).join('');
      } else {
        AB.$('r_margin').innerHTML = '<div class="empty">No margin data</div>';
      }
    }

    AB.$('r_posBody').innerHTML = positions.length ? positions.map(p => `<tr>
      <td class="mono">${p.symbol}</td>
      <td class="mono" style="font-size:11px">${p.longExchange}→${p.shortExchange}</td>
      <td class="mono">${AB.fmt(p.baseQty,6)}</td>
      <td>${AB.fmtUsd(p.unrealizedPnlUsd ?? p.unrealizedPnl)}</td>
      <td class="mono muted">${AB.fmt(p.currentWidthPercent,3)}%</td>
      <td class="muted">${p.openedAt ? new Date(p.openedAt).toLocaleString() : '—'}</td>
    </tr>`).join('') : '<tr><td colspan="6" class="muted" style="text-align:center;padding:20px">No open hedges</td></tr>';

    // analytics from snapshot
    const an = data.paperAnalytics || {};
    if (AB.$('r_daySummary')) {
      AB.$('r_daySummary').innerHTML = [
        `<div><span class="muted">Day</span> ${an.dayUtc || '—'}</div>`,
        `<div>scans <b>${an.scans ?? 0}</b> · avg candidates <b>${an.avgCandidates ?? 0}</b></div>`,
        `<div>opens <b class="pos">${an.opens ?? 0}</b> · closes <b>${an.closes ?? 0}</b> · skips <b>${an.skips ?? 0}</b></div>`,
        `<div>realized ${AB.fmtUsd(an.realizedPnlUsd)} · best open ${AB.fmt(an.bestOpenPctSeen,3)}% · best RT ${AB.fmt(an.bestRtPctSeen,3)}%</div>`,
      ].join('');
    }
    const reasons = an.skipReasons || [];
    if (AB.$('r_skipReasons')) {
      AB.$('r_skipReasons').innerHTML = reasons.length
        ? reasons.map(r => `<div class="mono" style="margin:4px 0"><span class="muted">${r.reason}</span> × <b>${r.count}</b></div>`).join('')
        : '<span class="muted">No skips recorded yet</span>';
    }
    const skips = data.paperRecentSkips || [];
    if (AB.$('r_skipBody')) {
      AB.$('r_skipBody').innerHTML = skips.length ? skips.map(s => {
        const t = s.utc ? new Date(s.utc).toISOString().slice(11,19) : '—';
        return `<tr>
          <td class="muted mono">${t}</td>
          <td style="font-size:11px">${s.reason || ''}</td>
          <td class="mono" style="color:var(--cyan)">${s.symbol || '—'}</td>
          <td class="mono">${s.openNet != null ? Number(s.openNet).toFixed(3) : '—'}</td>
          <td class="mono">${s.rtNet != null ? Number(s.rtNet).toFixed(3) : '—'}</td>
        </tr>`;
      }).join('') : '<tr><td colspan="5" class="muted" style="text-align:center;padding:16px">Waiting for scans…</td></tr>';
    }

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
  onShow(d) { if (d) this.render(d); this.loadDays(); }
};


AB.pages.reports.loadLiveBalances = async function () {
  const el = AB.$('r_liveBal');
  if (!el) return;
  el.innerHTML = '<div class="empty">Loading live balances…</div>';
  try {
    const data = await AB.api.get('/api/live/balances');
    const rows = data.exchanges || [];
    if (!rows.length) {
      el.innerHTML = '<div class="empty">No exchanges</div>';
      return;
    }
    el.innerHTML = rows.map(function (x) {
      if (!x.ok) {
        return '<div class="kpi" style="margin:0;border-color:rgba(248,113,113,0.25)">' +
          '<div class="kpi-l">' + x.exchange + '</div>' +
          '<div class="neg" style="margin-top:6px;font-size:12px;word-break:break-word">' + (x.error || 'fail') + '</div>' +
          (x.detail ? '<div class="muted mono" style="font-size:10px;margin-top:4px;word-break:break-all">' + x.detail + '</div>' : '') +
          (x.hint ? '<div class="muted" style="font-size:10px;margin-top:4px">' + x.hint + '</div>' : '') +
          '</div>';
      }
      const usdt = x.usdtTotal != null ? x.usdtTotal : 0;
      const posN = Array.isArray(x.positions) ? x.positions.length : 0;
      return '<div class="kpi" style="margin:0;border-color:rgba(56,189,248,0.25)">' +
        '<div class="kpi-l">' + x.exchange + ' · live</div>' +
        '<div class="mono" style="font-size:18px;font-weight:700;margin-top:6px;color:var(--blue)">' + AB.fmt(usdt, 2) + ' USDT</div>' +
        '<div class="muted mono" style="font-size:11px;margin-top:6px">perm ' + (x.permission || '—') +
        ' · positions ' + posN + '</div></div>';
    }).join('') +
      '<div class="kpi" style="margin:0;border-color:rgba(45,212,191,0.3)">' +
      '<div class="kpi-l">SUM USDT (approx)</div>' +
      '<div class="mono" style="font-size:18px;font-weight:700;margin-top:6px;color:var(--accent)">' +
      AB.fmt(data.totalUsdtApprox || 0, 2) + '</div></div>';
  } catch (e) {
    el.innerHTML = '<div class="empty neg">' + (e.message || e) + '</div>';
  }
};

document.getElementById('btnRefreshLiveBal')?.addEventListener('click', () => AB.pages.reports.loadLiveBalances());
const _repShow = AB.pages.reports.onShow;
AB.pages.reports.onShow = function (d) {
  if (typeof _repShow === 'function') _repShow.call(this, d);
  else if (d && this.render) this.render(d);
  this.loadLiveBalances();
};
