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
  const cls = v >= 0 ? 'pos' : 'neg';
  return `<span class="mono ${cls}">${v >= 0 ? '+' : ''}${v.toFixed(3)}%</span>`;
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
  document.querySelectorAll('.nav a').forEach(a => a.classList.toggle('active', a.dataset.page === name));
  const titles = {
    dashboard: 'Dashboard',
    market: 'Market',
    reports: 'Reports',
    settings: 'Settings'
  };
  AB.$('pageTitle').textContent = titles[name] || name;
  location.hash = name;
  if (AB.pages[name]?.onShow) AB.pages[name].onShow(AB.state.snapshot);
};

AB.onSnapshot = (data) => {
  AB.state.snapshot = data;
  // topbar
  const mode = data.mode || 'PAPER';
  const badge = AB.$('modeBadge');
  badge.textContent = mode;
  badge.className = 'badge ' + (mode === 'LIVE' ? 'live' : 'paper');
  AB.$('lastScan').textContent = 'scan ' + AB.timeAgo(data.lastScanUtc);
  AB.$('connDot').className = 'dot on';
  AB.$('connText').textContent = 'Live';

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
  const hold = p.holdSeconds != null ? (Math.floor(p.holdSeconds/60) + 'm ' + (p.holdSeconds%60) + 's') : '—';
  const id = p.tradeId || p.id || p.Id || '';
  return `<div class="pos-card">
    <div>
      <div class="title mono">${p.symbol}</div>
      <div class="sub">
        <span class="pos-side long">LONG ${p.longExchange}</span>
        <span class="pos-side short">SHORT ${p.shortExchange}</span>
      </div>
      <div class="sub mono" style="margin-top:6px">
        entry L ${AB.fmt(p.longEntry)} / S ${AB.fmt(p.shortEntry)} · qty ${AB.fmt(p.baseQty, 5)}
      </div>
      <div class="sub">hold ${hold} · width ${AB.fmt(p.currentWidthPercent, 3)}% (entry ${AB.fmt(p.entryWidthPercent, 3)}%)</div>
    </div>
    <div style="text-align:right;display:flex;flex-direction:column;gap:8px;align-items:flex-end">
      <div style="font-size:16px;font-weight:700">${AB.fmtUsd(u)}</div>
      <button class="btn danger" style="padding:6px 10px" onclick="AB.closePaper('${id}')">Close</button>
    </div>
  </div>`;
};


AB.startSignalR = async () => {
  const connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/arbitrage')
    .withAutomaticReconnect([0, 1000, 2000, 5000, 10000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();
  connection.on('Snapshot', AB.onSnapshot);
  connection.on('MarketTick', (tick) => {
    AB.state.lastTick = tick;
    // merge live books into snapshot for UI without waiting for scan
    if (AB.state.snapshot && tick) {
      if (tick.bookTickers) AB.state.snapshot.bookTickers = tick.bookTickers;
      if (tick.orderBookDepth) AB.state.snapshot.orderBookDepth = tick.orderBookDepth;
      if (tick.connectionStatus) AB.state.snapshot.connectionStatus = tick.connectionStatus;
      AB.state.snapshot.streamsLive = tick.streamsLive;
      AB.state.snapshot.streamsTotal = tick.streamsTotal;
      AB.state.snapshot.wsTransport = tick.transport;
    }
    AB.$('connDot').className = 'dot on';
    const live = tick?.streamsLive ?? 0, total = tick?.streamsTotal ?? 0;
    AB.$('connText').textContent = live + '/' + total + ' WS';
    // market page: refresh books/tape only (cheap)
    if (AB.state.page === 'market' && AB.pages.market?.onTick)
      AB.pages.market.onTick(AB.state.snapshot, tick);
  });
  connection.onreconnecting(() => {
    AB.$('connDot').className = 'dot warn';
    AB.$('connText').textContent = '…';
  });
  connection.onreconnected(() => {
    AB.$('connDot').className = 'dot on';
    AB.$('connText').textContent = 'Live';
    connection.invoke('RequestSnapshot').catch(() => {});
  });
  connection.onclose(() => {
    AB.$('connDot').className = 'dot off';
    AB.$('connText').textContent = 'Off';
  });
  try {
    await connection.start();
    AB.$('connDot').className = 'dot on';
    AB.$('connText').textContent = 'Live';
  } catch {
    AB.$('connDot').className = 'dot off';
    AB.$('connText').textContent = 'REST';
    try {
      const snap = await AB.api.get('/api/snapshot');
      AB.onSnapshot(snap);
    } catch {}
    setTimeout(AB.startSignalR, 3000);
  }
  AB.hub = connection;
};

document.addEventListener('DOMContentLoaded', () => {
  document.querySelectorAll('.nav a[data-page]').forEach(a => {
    a.addEventListener('click', (e) => {
      e.preventDefault();
      AB.setPage(a.dataset.page);
    });
  });
  const hash = (location.hash || '#dashboard').replace('#', '');
  AB.setPage(hash || 'dashboard');
  AB.startSignalR();
  setInterval(() => {
    if (AB.state.snapshot?.lastScanUtc)
      AB.$('lastScan').textContent = 'scan ' + AB.timeAgo(AB.state.snapshot.lastScanUtc);
  }, 1000);
});
