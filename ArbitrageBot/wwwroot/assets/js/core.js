window.AB = window.AB || {};
AB.state = { snapshot: null, settings: null, page: 'dashboard' };
AB.$ = (id) => document.getElementById(id);

AB.fmt = (n, d = 4) => {
  if (n == null || Number.isNaN(Number(n))) return '—';
  return Number(n).toLocaleString('en-US', { minimumFractionDigits: d, maximumFractionDigits: d });
};
AB.fmtPct = (n) => {
  if (n == null) return '—';
  const v = Number(n);
  return `<span class="mono ${v >= 0 ? 'pos' : 'neg'}">${v >= 0 ? '+' : ''}${v.toFixed(3)}%</span>`;
};
AB.fmtUsd = (n) => {
  if (n == null) return '—';
  const v = Number(n);
  return `<span class="mono ${v >= 0 ? 'pos' : 'neg'}">${v >= 0 ? '+' : ''}${v.toFixed(2)}</span>`;
};
AB.timeAgo = (iso) => {
  if (!iso) return '—';
  const s = Math.floor((Date.now() - new Date(iso).getTime()) / 1000);
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
  }
};

AB.setPage = (name) => {
  AB.state.page = name;
  document.querySelectorAll('.page').forEach(p => p.classList.toggle('active', p.dataset.page === name));
  document.querySelectorAll('.nav-item').forEach(a => a.classList.toggle('active', a.dataset.page === name));
  const titles = { dashboard: 'Dashboard', market: 'Market', reports: 'Reports', settings: 'Settings' };
  if (AB.$('pageTitle')) AB.$('pageTitle').textContent = titles[name] || name;
  location.hash = name;
  if (AB.pages[name]?.onShow) AB.pages[name].onShow(AB.state.snapshot);
};

AB.onSnapshot = (data) => {
  if (!data) return;
  AB.state.snapshot = data;
  const mode = data.mode || 'PAPER';
  const badge = AB.$('modeBadge');
  if (badge) {
    badge.textContent = mode;
    badge.className = 'pill ' + (mode === 'LIVE' ? 'live' : 'paper');
  }
  if (AB.$('lastScan')) AB.$('lastScan').textContent = 'scan ' + AB.timeAgo(data.lastScanUtc);
  if (AB.$('connDot')) AB.$('connDot').className = 'led on';
  if (AB.$('connText')) AB.$('connText').textContent = 'Connected';
  if (AB.$('topHint')) {
    const n = (data.opportunities || []).length;
    const open = (data.futuresPaper?.positions || []).length;
    AB.$('topHint').textContent = `${n} signals · ${open} open · ${data.strategyMode || ''}`;
  }
  const page = AB.pages[AB.state.page];
  if (page?.render) page.render(data);
};

AB.pages = {};

AB.closePaper = async (tradeId) => {
  if (!tradeId || !confirm('Close this paper hedge at market marks?')) return;
  try {
    await AB.api.post('/api/paper/close/' + tradeId);
    const s = await AB.api.get('/api/snapshot');
    AB.onSnapshot(s);
  } catch (e) {
    alert('Close failed: ' + e.message);
  }
};

AB.posCardHtml = (p) => {
  const u = p.unrealizedPnlUsd ?? p.unrealizedPnl;
  const hold = p.holdSeconds != null
    ? (Math.floor(p.holdSeconds / 60) + 'm ' + (p.holdSeconds % 60) + 's')
    : '—';
  const id = p.tradeId || p.id || '';
  return `<div class="pos-item">
    <div>
      <div class="mono" style="font-weight:700;font-size:14px">${p.symbol}</div>
      <div style="margin-top:6px;font-size:12px">
        <span class="side-long">LONG ${p.longExchange}</span>
        &nbsp;·&nbsp;
        <span class="side-short">SHORT ${p.shortExchange}</span>
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

AB.startSignalR = () => {
  if (typeof signalR === 'undefined') {
    if (AB.$('connText')) AB.$('connText').textContent = 'SignalR missing';
    return;
  }
  const connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/arbitrage')
    .withAutomaticReconnect()
    .build();

  connection.on('Snapshot', (data) => AB.onSnapshot(data));
  connection.on('MarketTick', (tick) => {
    if (!AB.state.snapshot) return;
    const merged = {
      ...AB.state.snapshot,
      bookTickers: tick.bookTickers || AB.state.snapshot.bookTickers,
      orderBookDepth: tick.orderBookDepth || AB.state.snapshot.orderBookDepth,
      connectionStatus: tick.connectionStatus || AB.state.snapshot.connectionStatus,
      streamsLive: tick.streamsLive,
      streamsTotal: tick.streamsTotal
    };
    AB.state.snapshot = merged;
    if (AB.state.page === 'market' && AB.pages.market?.onTick)
      AB.pages.market.onTick(merged, tick);
  });

  connection.start()
    .then(() => {
      if (AB.$('connDot')) AB.$('connDot').className = 'led on';
      if (AB.$('connText')) AB.$('connText').textContent = 'Live';
    })
    .catch(() => {
      if (AB.$('connDot')) AB.$('connDot').className = 'led warn';
      if (AB.$('connText')) AB.$('connText').textContent = 'Reconnect…';
      setTimeout(AB.startSignalR, 3000);
    });

  connection.onreconnecting(() => {
    if (AB.$('connDot')) AB.$('connDot').className = 'led warn';
    if (AB.$('connText')) AB.$('connText').textContent = 'Reconnecting…';
  });
  connection.onreconnected(() => {
    if (AB.$('connDot')) AB.$('connDot').className = 'led on';
    if (AB.$('connText')) AB.$('connText').textContent = 'Live';
  });
};

document.querySelectorAll('.nav-item').forEach(a => {
  a.addEventListener('click', () => AB.setPage(a.dataset.page));
});

window.addEventListener('hashchange', () => {
  const p = location.hash.replace('#', '');
  if (p && AB.pages[p]) AB.setPage(p);
});

document.getElementById('btnPause')?.addEventListener('click', async () => {
  try { await AB.api.post('/api/control/pause'); } catch (e) { console.warn(e); }
});
document.getElementById('btnResetPaper')?.addEventListener('click', async () => {
  if (!confirm('Reset all paper positions and balances?')) return;
  try {
    await AB.api.post('/api/control/reset-paper');
    const s = await AB.api.get('/api/snapshot');
    AB.onSnapshot(s);
  } catch (e) { alert(e.message); }
});

AB.startSignalR();
fetch('/api/snapshot').then(r => r.json()).then(AB.onSnapshot).catch(() => {
  if (AB.$('connText')) AB.$('connText').textContent = 'No snapshot';
});

if (location.hash) {
  const p = location.hash.replace('#', '');
  if (p) setTimeout(() => AB.setPage(p), 50);
}
