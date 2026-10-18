window.AB = window.AB || {};
AB.state = { snapshot: null, settings: null, page: 'dashboard' };
AB.$ = (id) => document.getElementById(id);

AB.fmt = (n, d = 4) => {
  if (n == null || Number.isNaN(Number(n))) return '—';
  const x = Number(n);
  return x.toLocaleString('en-US', { minimumFractionDigits: d, maximumFractionDigits: d });
};
AB.fmtPct = (n) => {
  if (n == null || !Number.isFinite(Number(n))) return '—';
  const v = Number(n);
  return `<span class="mono ${v >= 0 ? 'pos' : 'neg'}">${v >= 0 ? '+' : ''}${v.toFixed(3)}%</span>`;
};
AB.fmtUsd = (n) => {
  if (n == null || !Number.isFinite(Number(n))) return '—';
  const v = Number(n);
  return `<span class="mono ${v >= 0 ? 'pos' : 'neg'}">${v >= 0 ? '+' : ''}${v.toFixed(2)}</span>`;
};
AB.timeAgo = (iso) => {
  if (!iso) return '—';
  const t = new Date(iso).getTime();
  if (!Number.isFinite(t)) return '—';
  const s = Math.floor((Date.now() - t) / 1000);
  if (s < 5) return 'now';
  if (s < 60) return s + 's';
  return Math.floor(s / 60) + 'm';
};

AB.api = {
  async get(url) {
    const r = await fetch(url);
    if (!r.ok) throw new Error(await r.text());
    return r.json();
  },
  async post(url, body) {
    const r = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: body ? JSON.stringify(body) : undefined
    });
    if (!r.ok) throw new Error(await r.text());
    return r.json();
  },
  async del(url) {
    const r = await fetch(url, { method: 'DELETE' });
    if (!r.ok) throw new Error(await r.text());
    const t = await r.text();
    return t ? JSON.parse(t) : {};
  }
};

AB.setPage = (name) => {
  AB.state.page = name || 'dashboard';
  document.querySelectorAll('.page').forEach(p => {
    p.classList.toggle('active', p.getAttribute('data-page') === AB.state.page);
  });
  document.querySelectorAll('.nav-item').forEach(a => {
    a.classList.toggle('active', a.getAttribute('data-page') === AB.state.page);
  });
  const titles = { dashboard: 'Dashboard', market: 'Market', reports: 'Reports', settings: 'Settings' };
  const title = AB.$('pageTitle');
  if (title) title.textContent = titles[AB.state.page] || AB.state.page;
  try { location.hash = AB.state.page; } catch (_) {}
  const page = AB.pages[AB.state.page];
  if (page && typeof page.onShow === 'function') {
    try { page.onShow(AB.state.snapshot); } catch (e) { console.error(e); }
  } else if (page && typeof page.render === 'function' && AB.state.snapshot) {
    try { page.render(AB.state.snapshot); } catch (e) { console.error(e); }
  }
};

AB.onSnapshot = (data) => {
  if (!data || typeof data !== 'object') return;
  AB.state.snapshot = data;

  try {
    const mode = data.mode || 'PAPER';
    const badge = AB.$('modeBadge');
    if (badge) {
      badge.textContent = mode;
      badge.className = 'pill ' + (mode === 'LIVE' ? 'live' : 'paper');
    }
    if (AB.$('lastScan')) AB.$('lastScan').textContent = 'scan ' + AB.timeAgo(data.lastScanUtc);
    if (AB.$('connDot')) AB.$('connDot').className = 'led on';
    if (AB.$('connText')) AB.$('connText').textContent = 'Live';
    if (AB.$('topHint')) {
      const n = (data.opportunities || []).length;
      const open = (data.futuresPaper && data.futuresPaper.positions) ? data.futuresPaper.positions.length : 0;
      AB.$('topHint').textContent = n + ' signals · ' + open + ' open · ' + (data.strategyMode || '');
    }
  } catch (e) {
    console.warn('topbar update', e);
  }

  // Always render active page
  const page = AB.pages[AB.state.page || 'dashboard'];
  if (page && typeof page.render === 'function') {
    try { page.render(data); } catch (e) {
      console.error('page render failed', AB.state.page, e);
      const b = AB.$('d_banner');
      if (b) b.textContent = 'Render error: ' + e.message;
    }
  }
};

AB.pages = AB.pages || {};

AB.closePaper = async (tradeId) => AB.closeHedge(tradeId, 'paper');

AB.closeHedge = async (tradeId, source) => {
  if (!tradeId || !confirm('Close this hedge at market marks?')) return;
  try {
    const url = source === 'live' ? '/api/live/close/' + tradeId : '/api/paper/close/' + tradeId;
    await AB.api.post(url);
    await AB.refreshSnapshot();
  } catch (e) {
    alert('Close failed: ' + e.message);
  }
};

AB.closeAllPaper = async () => {
  if (!confirm('Close ALL open paper positions?')) return;
  try {
    await AB.api.post('/api/paper/close-all');
    await AB.refreshSnapshot();
  } catch (e) {
    alert('Close all failed: ' + e.message);
  }
};

AB.legMetrics = (p, books) => {
  const qty = Number(p.baseQty ?? p.BaseQty ?? 0);
  const longE = Number(p.longEntry ?? p.LongEntry ?? 0);
  const shortE = Number(p.shortEntry ?? p.ShortEntry ?? 0);
  const lev = Number(p.leverage ?? p.Leverage ?? 5) || 5;
  const longEx = p.longExchange || p.LongExchange || '?';
  const shortEx = p.shortExchange || p.ShortExchange || '?';
  const sym = p.symbol || p.Symbol || '';
  let longMark = Number(p.markLongBid ?? p.MarkLongBid ?? 0);
  let shortMark = Number(p.markShortAsk ?? p.MarkShortAsk ?? 0);
  const bag = books && (books[sym] || books);
  if (bag && typeof bag === 'object') {
    const lb = bag[longEx];
    const sb = bag[shortEx];
    if (!longMark && lb) longMark = Number(lb.bestBid ?? lb.BestBid ?? 0);
    if (!shortMark && sb) shortMark = Number(sb.bestAsk ?? sb.BestAsk ?? 0);
  }
  const longPnl = p.longUnrealizedPnlUsd != null ? Number(p.longUnrealizedPnlUsd)
    : (longMark > 0 && longE > 0 && qty ? (longMark - longE) * qty : null);
  const shortPnl = p.shortUnrealizedPnlUsd != null ? Number(p.shortUnrealizedPnlUsd)
    : (shortMark > 0 && shortE > 0 && qty ? (shortE - shortMark) * qty : null);
  const entrySpread = longE > 0 ? ((shortE - longE) / longE) * 100 : null;
  const curSpread = (longMark > 0 && shortMark > 0)
    ? ((shortMark - longMark) / longMark) * 100
    : (p.currentWidthPercent != null ? Number(p.currentWidthPercent) : null);
  const notionalL = longE * qty;
  const notionalS = shortE * qty;
  const margin = Number(p.lockedMarginUsd ?? (lev > 0 && notionalL ? notionalL / lev : 0));
  const funding = Number(p.accumulatedFundingPnlUsd ?? 0);
  return { qty, longE, shortE, lev, longEx, shortEx, longMark, shortMark, longPnl, shortPnl, entrySpread, curSpread, notionalL, notionalS, margin, funding };
};

AB.legSplitHtml = (v) => {
  const px = (n) => (n > 0 ? AB.fmt(n, n < 1 ? 6 : 4) : '—');
  const pnl = (n) => (n == null || !isFinite(n) ? '—' : AB.fmtUsd(n));
  const cls = (n) => (n == null ? '' : (n >= 0 ? 'pos' : 'neg'));
  const row = (k, val) => `<div class="leg-row"><span>${k}</span><b class="mono">${val}</b></div>`;
  const col = (side, ex, entry, mark, notional, upnl) => `<div class="leg leg-${side}">
      <div class="leg-hd"><span class="leg-badge ${side}">${side === 'long' ? 'LONG' : 'SHORT'}</span><span class="mono">${ex}</span></div>
      ${row('Entry', px(entry))}
      ${row('Mark', px(mark))}
      ${row('Qty', v.qty ? AB.fmt(v.qty, 4) : '—')}
      ${row('Notional', notional ? ('$' + notional.toFixed(0)) : '—')}
      ${row('Lev · margin', v.lev + 'x · $' + (v.margin || 0).toFixed(2))}
      ${row('uPnL', `<span class="${cls(upnl)}">${pnl(upnl)}</span>`)}
    </div>`;
  const es = v.entrySpread == null ? '—' : ((v.entrySpread >= 0 ? '+' : '') + v.entrySpread.toFixed(3) + '%');
  const cs = v.curSpread == null ? '—' : ((v.curSpread >= 0 ? '+' : '') + v.curSpread.toFixed(3) + '%');
  return `<div class="leg-split">
      ${col('long', v.longEx, v.longE, v.longMark, v.notionalL, v.longPnl)}
      ${col('short', v.shortEx, v.shortE, v.shortMark, v.notionalS, v.shortPnl)}
    </div>
    <div class="leg-meta">entry spread <b>${es}</b> · now <b>${cs}</b> · funding <b class="${cls(v.funding)}">${pnl(v.funding)}</b></div>`;
};

AB.posCardHtml = (p, books) => {
  if (!p) return '';
  const u = p.unrealizedPnlUsd != null ? p.unrealizedPnlUsd : p.unrealizedPnl;
  const hold = p.holdSeconds != null
    ? (Math.floor(p.holdSeconds / 60) + 'm ' + (p.holdSeconds % 60) + 's')
    : '—';
  const id = p.tradeId || p.id || '';
  const live = p.source === 'ledger' || p.source === 'live';
  const v = AB.legMetrics(p, books);
  const holdTag = (p.lastHoldDecision || '').toString().toUpperCase().includes('CLOSE')
    ? '<span class="leg-badge short">CLOSE</span>'
    : (p.lastHoldDecision ? '<span class="leg-badge long">HOLD</span>' : '');
  return `<div class="pos-item leg-acc">
    <button type="button" class="leg-acc-hd" onclick="this.parentElement.classList.toggle('open')">
      <div>
        <div class="mono" style="font-weight:700;font-size:14px">${p.symbol || '?'}</div>
        <div style="margin-top:4px;font-size:12px">
          <span class="side-long">LONG ${v.longEx}</span>
          &nbsp;·&nbsp;
          <span class="side-short">SHORT ${v.shortEx}</span>
          ${holdTag}
        </div>
        <div class="muted mono" style="margin-top:4px;font-size:11px">hold ${hold} · width ${AB.fmt(p.currentWidthPercent, 3)}%</div>
      </div>
      <div style="text-align:right;display:flex;flex-direction:column;align-items:flex-end;gap:4px">
        <div style="font-size:16px;font-weight:700">${AB.fmtUsd(u)}</div>
        <span class="leg-chev">details</span>
      </div>
    </button>
    <div class="leg-acc-body">
      ${AB.legSplitHtml(v)}
      <div class="pos-foot" style="margin-top:8px">
        <span class="muted" style="font-size:11px">${p.lastHoldDecisionReason || ''}</span>
        <button type="button" class="btn sm danger" onclick="event.stopPropagation();AB.closeHedge('${id}','${live ? 'live' : 'paper'}')">Close</button>
      </div>
    </div>
  </div>`;
};

AB.refreshSnapshot = async () => {
  try {
    const data = await AB.api.get('/api/snapshot');
    AB.onSnapshot(data);
    return data;
  } catch (e) {
    console.warn('snapshot fetch failed', e);
    if (AB.$('connDot')) AB.$('connDot').className = 'led warn';
    if (AB.$('connText')) AB.$('connText').textContent = 'API error';
    return null;
  }
};

AB.startSignalR = () => {
  if (typeof signalR === 'undefined') {
    if (AB.$('connText')) AB.$('connText').textContent = 'No SignalR — polling';
    return;
  }
  try {
    const connection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/arbitrage')
      .withAutomaticReconnect()
      .build();

    connection.on('Snapshot', (data) => AB.onSnapshot(data));
    connection.on('MarketTick', (tick) => {
      if (!AB.state.snapshot) return;
      const merged = Object.assign({}, AB.state.snapshot, {
        bookTickers: tick.bookTickers || AB.state.snapshot.bookTickers,
        orderBookDepth: tick.orderBookDepth || AB.state.snapshot.orderBookDepth,
        connectionStatus: tick.connectionStatus || AB.state.snapshot.connectionStatus,
        streamsLive: tick.streamsLive,
        streamsTotal: tick.streamsTotal
      });
      AB.state.snapshot = merged;
      if (AB.state.page === 'market' && AB.pages.market && AB.pages.market.onTick) {
        try { AB.pages.market.onTick(merged, tick); } catch (e) { console.warn(e); }
      }
    });

    connection.start()
      .then(() => {
        if (AB.$('connDot')) AB.$('connDot').className = 'led on';
        if (AB.$('connText')) AB.$('connText').textContent = 'Live';
        return connection.invoke('RequestSnapshot').catch(() => {});
      })
      .catch(() => {
        if (AB.$('connDot')) AB.$('connDot').className = 'led warn';
        if (AB.$('connText')) AB.$('connText').textContent = 'Polling…';
        setTimeout(AB.startSignalR, 5000);
      });

    connection.onreconnecting(() => {
      if (AB.$('connDot')) AB.$('connDot').className = 'led warn';
      if (AB.$('connText')) AB.$('connText').textContent = 'Reconnecting…';
    });
    connection.onreconnected(() => {
      if (AB.$('connDot')) AB.$('connDot').className = 'led on';
      if (AB.$('connText')) AB.$('connText').textContent = 'Live';
      connection.invoke('RequestSnapshot').catch(() => {});
    });
  } catch (e) {
    console.warn('SignalR init failed', e);
  }
};

// Nav
document.querySelectorAll('.nav-item').forEach(a => {
  a.addEventListener('click', () => AB.setPage(a.getAttribute('data-page')));
});
window.addEventListener('hashchange', () => {
  const p = (location.hash || '').replace('#', '');
  if (p && AB.pages[p]) AB.setPage(p);
});

document.getElementById('btnPause')?.addEventListener('click', async () => {
  try { await AB.api.post('/api/control/pause'); await AB.refreshSnapshot(); } catch (e) { console.warn(e); }
});
document.getElementById('btnResetPaper')?.addEventListener('click', async () => {
  if (!confirm('Reset all paper positions and balances?')) return;
  try {
    await AB.api.post('/api/control/reset-paper');
    await AB.refreshSnapshot();
  } catch (e) { alert(e.message); }
});

// Boot: wait for page modules, then poll forever (backup if SignalR fails)
AB._booted = false;
AB.boot = () => {
  if (AB._booted) return;
  AB._booted = true;
  const hash = (location.hash || '').replace('#', '');
  if (hash && AB.pages[hash]) AB.state.page = hash;
  // ensure dashboard section visible
  AB.setPage(AB.state.page || 'dashboard');
  AB.startSignalR();
  AB.refreshSnapshot();
  // hard poll every 2s so Dashboard never stays empty
  setInterval(() => { AB.refreshSnapshot(); }, 2000);
};

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', () => setTimeout(AB.boot, 50));
} else {
  setTimeout(AB.boot, 50);
}
window.addEventListener('load', () => setTimeout(AB.boot, 100));

document.addEventListener('DOMContentLoaded', () => {
  const r = document.getElementById('btnRefreshTop');
  if (r) r.addEventListener('click', () => { if (AB.refreshSnapshot) AB.refreshSnapshot(); if (AB.pages.funding) AB.pages.funding.load(); if (AB.pages.live) AB.pages.live.load(); });
});
