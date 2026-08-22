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

AB.closePaper = async (tradeId) => {
  if (!tradeId || !confirm('Close this paper hedge at market marks?')) return;
  try {
    await AB.api.post('/api/paper/close/' + tradeId);
    await AB.refreshSnapshot();
  } catch (e) {
    alert('Close failed: ' + e.message);
  }
};

AB.posCardHtml = (p) => {
  if (!p) return '';
  const u = p.unrealizedPnlUsd != null ? p.unrealizedPnlUsd : p.unrealizedPnl;
  const hold = p.holdSeconds != null
    ? (Math.floor(p.holdSeconds / 60) + 'm ' + (p.holdSeconds % 60) + 's')
    : '—';
  const id = p.tradeId || p.id || '';
  return `<div class="pos-item">
    <div>
      <div class="mono" style="font-weight:700;font-size:14px">${p.symbol || '?'}</div>
      <div style="margin-top:6px;font-size:12px">
        <span class="side-long">LONG ${p.longExchange || ''}</span>
        &nbsp;·&nbsp;
        <span class="side-short">SHORT ${p.shortExchange || ''}</span>
      </div>
      <div class="muted mono" style="margin-top:6px;font-size:11px">
        L ${AB.fmt(p.longEntry)} / S ${AB.fmt(p.shortEntry)} · qty ${AB.fmt(p.baseQty, 5)} · hold ${hold}
      </div>
      <div class="muted mono" style="font-size:11px">width ${AB.fmt(p.currentWidthPercent, 3)}% (entry ${AB.fmt(p.entryWidthPercent, 3)}%)</div>
    </div>
    <div style="text-align:right;display:flex;flex-direction:column;gap:8px;align-items:flex-end">
      <div style="font-size:16px;font-weight:700">${AB.fmtUsd(u)}</div>
      <button type="button" class="btn sm danger" onclick="AB.closePaper('${id}')">Close</button>
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
