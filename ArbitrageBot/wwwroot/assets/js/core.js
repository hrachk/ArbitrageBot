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
AB.usdText = (n) => {
  if (n == null || !Number.isFinite(Number(n))) return '—';
  const v = Number(n);
  return (v >= 0 ? '+' : '') + v.toFixed(2);
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

AB.captureScroll = () => {
  const main = document.querySelector('.main');
  const nested = [];
  const page = document.querySelector('.page.active');
  if (page) {
    page.querySelectorAll('.scroll, .scroll-signals, .scroll-trades, .pos-list').forEach((el) => {
      if (el.scrollTop || el.scrollLeft)
        nested.push({ el, top: el.scrollTop, left: el.scrollLeft });
    });
  }
  return {
    x: window.scrollX,
    y: window.scrollY,
    mainTop: main ? main.scrollTop : 0,
    nested
  };
};

AB.restoreScroll = (snap) => {
  if (!snap) return;
  const main = document.querySelector('.main');
  if (main && main.scrollTop !== snap.mainTop) main.scrollTop = snap.mainTop;
  if (window.scrollX !== snap.x || window.scrollY !== snap.y) window.scrollTo(snap.x, snap.y);
  (snap.nested || []).forEach((n) => {
    if (!n.el || !n.el.isConnected) return;
    if (n.el.scrollTop !== n.top) n.el.scrollTop = n.top;
    if (n.el.scrollLeft !== n.left) n.el.scrollLeft = n.left;
  });
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
    const scroll = AB.captureScroll();
    try { page.render(data); } catch (e) {
      console.error('page render failed', AB.state.page, e);
      const b = AB.$('d_banner');
      if (b) b.textContent = 'Render error: ' + e.message;
    }
    AB.restoreScroll(scroll);
    requestAnimationFrame(() => AB.restoreScroll(scroll));
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

AB.ui = AB.ui || {};
AB.ui.expandedPositions = AB.ui.expandedPositions || new Set();

AB.positionKey = (p) => {
  const id = p && (p.tradeId || p.TradeId || p.id || p.Id);
  if (id) return String(id);
  const sym = (p && (p.symbol || p.Symbol)) || '';
  const a = (p && (p.longExchange || p.LongExchange || p.exchange)) || '';
  const b = (p && (p.shortExchange || p.ShortExchange || p.side)) || '';
  const opened = (p && (p.openedAt || p.OpenedAt)) || '';
  return sym + '_' + a + '_' + b + '_' + opened;
};

AB.togglePosition = (key, ev) => {
  if (!key) return;
  const set = AB.ui.expandedPositions;
  if (set.has(key)) set.delete(key); else set.add(key);
  const card = ev && ev.currentTarget ? ev.currentTarget.closest('[data-k]') : null;
  if (card) card.classList.toggle('open', set.has(key));
};

AB.applyPosFields = (root, fields) => {
  if (!root || !fields) return;
  Object.keys(fields).forEach((name) => {
    const spec = fields[name];
    const n = root.querySelector('[data-f="' + name + '"]');
    if (!n || !spec) return;
    if (n.textContent !== spec.text) n.textContent = spec.text;
    if (spec.cls != null) {
      n.classList.toggle('pos', spec.cls === 'pos');
      n.classList.toggle('neg', spec.cls === 'neg');
    }
    if (spec.badge != null) n.className = 'leg-badge ' + spec.badge;
  });
};

/** Reuse card DOM by data-k. Snapshot only patches [data-f] text. Expand lives in AB.ui.expandedPositions. */
AB.mountKeyed = (container, entries) => {
  if (!container) return;
  const have = new Map();
  Array.from(container.children).forEach((child) => {
    const k = child.getAttribute('data-k');
    if (k) have.set(k, child);
  });
  const frag = document.createDocumentFragment();
  const seen = new Set();
  entries.forEach((e) => {
    seen.add(e.key);
    let node = have.get(e.key);
    if (node) have.delete(e.key);
    else {
      const tmp = document.createElement('div');
      tmp.innerHTML = e.html.trim();
      node = tmp.firstElementChild;
    }
    if (!node) return;
    if (e.fields) AB.applyPosFields(node, e.fields);
    if (node.classList.contains('leg-acc'))
      node.classList.toggle('open', AB.ui.expandedPositions.has(e.key));
    frag.appendChild(node);
  });
  const sc = container.scrollTop;
  container.replaceChildren(frag);
  if (container.scrollTop !== sc) container.scrollTop = sc;
  AB.ui.expandedPositions.forEach((k) => { if (!seen.has(k)) AB.ui.expandedPositions.delete(k); });
};

AB.legSplitHtml = (v) => {
  const px = (n) => (n > 0 ? AB.fmt(n, n < 1 ? 6 : 4) : '—');
  const pnl = (n) => (n == null || !isFinite(n) ? '—' : AB.usdText(n));
  const cls = (n) => (n == null ? '' : (n >= 0 ? 'pos' : 'neg'));
  const row = (k, field, val) => `<div class="leg-row"><span>${k}</span><b class="mono" data-f="${field}">${val}</b></div>`;
  const col = (side, ex, entry, mark, notional, upnl) => `<div class="leg leg-${side}">
      <div class="leg-hd"><span class="leg-badge ${side}">${side === 'long' ? 'LONG' : 'SHORT'}</span><span class="mono">${ex}</span></div>
      ${row('Entry', side + 'Entry', px(entry))}
      ${row('Mark', side + 'Mark', px(mark))}
      ${row('Qty', side + 'Qty', v.qty ? AB.fmt(v.qty, 4) : '—')}
      ${row('Notional', side + 'Ntl', notional ? ('$' + notional.toFixed(0)) : '—')}
      ${row('Lev · margin', side + 'Lev', v.lev + 'x · $' + (v.margin || 0).toFixed(2))}
      <div class="leg-row"><span>uPnL</span><b class="mono"><span data-f="${side}Pnl" class="${cls(upnl)}">${pnl(upnl)}</span></b></div>
    </div>`;
  const es = v.entrySpread == null ? '—' : ((v.entrySpread >= 0 ? '+' : '') + v.entrySpread.toFixed(3) + '%');
  const cs = v.curSpread == null ? '—' : ((v.curSpread >= 0 ? '+' : '') + v.curSpread.toFixed(3) + '%');
  return `<div class="leg-split">
      ${col('long', v.longEx, v.longE, v.longMark, v.notionalL, v.longPnl)}
      ${col('short', v.shortEx, v.shortE, v.shortMark, v.notionalS, v.shortPnl)}
    </div>
    <div class="leg-meta">entry spread <b data-f="entrySpread">${es}</b> · now <b data-f="curSpread">${cs}</b> · funding <b data-f="funding" class="${cls(v.funding)}">${pnl(v.funding)}</b></div>`;
};

AB.posFields = (p, books) => {
  const v = AB.legMetrics(p, books);
  const u = p.unrealizedPnlUsd != null ? p.unrealizedPnlUsd
    : (p.unrealizedPnl != null ? p.unrealizedPnl
      : ((v.longPnl != null || v.shortPnl != null) ? (Number(v.longPnl) || 0) + (Number(v.shortPnl) || 0) : null));
  const hold = p.holdSeconds != null
    ? (Math.floor(p.holdSeconds / 60) + 'm ' + (p.holdSeconds % 60) + 's')
    : '—';
  const px = (n) => (n > 0 ? AB.fmt(n, n < 1 ? 6 : 4) : '—');
  const pnl = (n) => (n == null || !isFinite(Number(n)) ? '—' : AB.usdText(n));
  const cls = (n) => (n == null || !isFinite(Number(n)) ? 'flat' : (Number(n) >= 0 ? 'pos' : 'neg'));
  const es = v.entrySpread == null ? '—' : ((v.entrySpread >= 0 ? '+' : '') + v.entrySpread.toFixed(3) + '%');
  const cs = v.curSpread == null ? '—' : ((v.curSpread >= 0 ? '+' : '') + v.curSpread.toFixed(3) + '%');
  const decision = (p.lastHoldDecision || '').toString().toUpperCase();
  const tag = decision.includes('CLOSE') ? 'CLOSE' : (decision ? 'HOLD' : '');
  return {
    hold: { text: 'hold ' + hold + ' · width ' + AB.fmt(p.currentWidthPercent, 3) + '%' },
    upnl: { text: pnl(u), cls: cls(u) },
    holdTag: { text: tag, badge: tag === 'CLOSE' ? 'short' : 'long' },
    longEntry: { text: px(v.longE) },
    longMark: { text: px(v.longMark) },
    longQty: { text: v.qty ? AB.fmt(v.qty, 4) : '—' },
    longNtl: { text: v.notionalL ? ('$' + v.notionalL.toFixed(0)) : '—' },
    longLev: { text: v.lev + 'x · $' + (v.margin || 0).toFixed(2) },
    longPnl: { text: pnl(v.longPnl), cls: cls(v.longPnl) },
    shortEntry: { text: px(v.shortE) },
    shortMark: { text: px(v.shortMark) },
    shortQty: { text: v.qty ? AB.fmt(v.qty, 4) : '—' },
    shortNtl: { text: v.notionalS ? ('$' + v.notionalS.toFixed(0)) : '—' },
    shortLev: { text: v.lev + 'x · $' + (v.margin || 0).toFixed(2) },
    shortPnl: { text: pnl(v.shortPnl), cls: cls(v.shortPnl) },
    entrySpread: { text: es },
    curSpread: { text: cs },
    funding: { text: pnl(v.funding), cls: cls(v.funding) },
    reason: { text: p.lastHoldDecisionReason || '' }
  };
};

AB.posCardHtml = (p, books) => {
  if (!p) return '';
  const key = AB.positionKey(p);
  const id = p.tradeId || p.TradeId || p.id || '';
  const live = p.source === 'ledger' || p.source === 'live';
  const v = AB.legMetrics(p, books);
  const fields = AB.posFields(p, books);
  const open = AB.ui.expandedPositions.has(key) ? ' open' : '';
  const safeKey = String(key).replace(/"/g, '');
  return `<div class="pos-item leg-acc${open}" data-k="${safeKey}">
    <button type="button" class="leg-acc-hd" onclick="AB.togglePosition(this.closest('[data-k]').getAttribute('data-k'), event)">
      <div>
        <div class="mono" style="font-weight:700;font-size:14px">${p.symbol || '?'}</div>
        <div style="margin-top:4px;font-size:12px">
          <span class="side-long">LONG ${v.longEx}</span>
          &nbsp;·&nbsp;
          <span class="side-short">SHORT ${v.shortEx}</span>
          <span class="leg-badge ${fields.holdTag.badge}" data-f="holdTag">${fields.holdTag.text}</span>
        </div>
        <div class="muted mono" style="margin-top:4px;font-size:11px" data-f="hold">${fields.hold.text}</div>
      </div>
      <div style="text-align:right;display:flex;flex-direction:column;align-items:flex-end;gap:4px">
        <div style="font-size:16px;font-weight:700" data-f="upnl" class="${fields.upnl.cls === 'flat' ? '' : fields.upnl.cls}">${fields.upnl.text}</div>
        <span class="leg-chev">details</span>
      </div>
    </button>
    <div class="leg-acc-body">
      ${AB.legSplitHtml(v)}
      <div class="pos-foot" style="margin-top:8px">
        <span class="muted" style="font-size:11px" data-f="reason">${fields.reason.text}</span>
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
