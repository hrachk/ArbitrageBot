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

AB.startSignalR = async () => {
  const connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/arbitrage')
    .withAutomaticReconnect([0, 1000, 2000, 5000, 10000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();
  connection.on('Snapshot', AB.onSnapshot);
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
