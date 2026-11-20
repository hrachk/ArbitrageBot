/* ArbitrageBot operator console — single-file SPA over the existing REST + SignalR API.
   Transport: SignalR "Snapshot" + "MarketTick" when available, otherwise polling /api/snapshot. */
(() => {
'use strict';

// ───────────────────────── utilities ─────────────────────────
const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];
const esc = (v) => String(v ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const num = (v) => { const n = Number(v); return Number.isFinite(n) ? n : null; };
const lc = (o) => { if (!o || typeof o !== 'object') return o; const r = {}; for (const k in o) r[k.charAt(0).toLowerCase() + k.slice(1)] = o[k]; return r; };
const pick = (o, ...keys) => { for (const k of keys) { if (o && o[k] != null) return o[k]; } return null; };

const grp = (n, d) => n.toLocaleString('en-US', { minimumFractionDigits: d, maximumFractionDigits: d }).replace(/,/g, '\u2009');
const fmt = {
  pct(v, d = 3, sign = true) { const n = num(v); if (n == null) return '—'; return (sign && n > 0 ? '+' : '') + n.toFixed(d) + '%'; },
  usd(v, d = 2, sign = true) { const n = num(v); if (n == null) return '—'; const s = grp(Math.abs(n), d); return (n < 0 ? '−' : sign && n > 0 ? '+' : '') + '$' + s; },
  price(v) { const n = num(v); if (n == null || n === 0) return '—'; const d = n >= 1000 ? 2 : n >= 10 ? 3 : n >= 1 ? 4 : n >= 0.01 ? 5 : 7; return n.toLocaleString('en-US', { minimumFractionDigits: d, maximumFractionDigits: d }); },
  int(v) { const n = num(v); return n == null ? '—' : grp(Math.round(n), 0); },
  qty(v) { const n = num(v); if (n == null) return '—'; const a = Math.abs(n); if (a >= 1e6) return (n / 1e6).toFixed(2) + 'M'; if (a >= 1e4) return (n / 1e3).toFixed(1) + 'K'; return a >= 100 ? n.toFixed(1) : a >= 1 ? n.toFixed(3) : n.toPrecision(4); },
  dur(sec) { const s = num(sec); if (s == null) return '—'; if (s < 1) return (s * 1000).toFixed(0) + ' мс'; if (s < 60) return s.toFixed(s < 10 ? 1 : 0) + ' с'; if (s < 3600) return Math.floor(s / 60) + ' мин ' + Math.floor(s % 60) + ' с'; return Math.floor(s / 3600) + ' ч ' + Math.floor((s % 3600) / 60) + ' мин'; },
  ago(iso) { if (!iso) return '—'; const t = new Date(iso).getTime(); if (!Number.isFinite(t)) return '—'; const s = (Date.now() - t) / 1000; if (s < 2) return 'только что'; if (s < 60) return Math.floor(s) + ' с назад'; if (s < 3600) return Math.floor(s / 60) + ' мин назад'; if (s < 86400) return Math.floor(s / 3600) + ' ч назад'; return new Date(t).toLocaleDateString('ru-RU'); },
  time(iso) { if (!iso) return '—'; const d = new Date(iso); return Number.isFinite(d.getTime()) ? d.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit', second: '2-digit' }) : '—'; },
  dt(iso) { if (!iso) return '—'; const d = new Date(iso); return Number.isFinite(d.getTime()) ? d.toLocaleString('ru-RU', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' }) : '—'; },
};
const cls = (v) => { const n = num(v); return n == null || n === 0 ? '' : n > 0 ? 'pos' : 'neg'; };
const signed = (v, f = fmt.usd) => `<span class="num ${cls(v)}">${f(v)}</span>`;

const VENUE = { Binance: 'Binance', Bybit: 'Bybit', OKX: 'OKX', Bitget: 'Bitget', GateIo: 'Gate.io', Kucoin: 'KuCoin', KuCoin: 'KuCoin', Coinbase: 'Coinbase' };
const venue = (v) => VENUE[v] || v || '—';
const VENUES_ALL = ['Binance', 'Bybit', 'OKX', 'Bitget', 'GateIo', 'Kucoin'];
const ruPlural = (n, one, few, many) => { const a = Math.abs(n) % 100, b = a % 10; return a > 10 && a < 20 ? many : b === 1 ? one : b >= 2 && b <= 4 ? few : many; };
const baseOf = (s) => String(s || '').replace(/USDT$/i, '');

// ───────────────────────── icons ─────────────────────────
const I = (p, extra = '') => `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" ${extra}>${p}</svg>`;
const ICON = {
  overview: I('<path d="M4 13h6V4H4zM14 20h6v-9h-6zM4 20h6v-4H4zM14 7h6V4h-6z"/>'),
  market: I('<path d="M3 17l5-6 4 4 8-9"/><path d="M14 6h6v6"/>'),
  signals: I('<path d="M4 18h3l2-6 3 9 3-13 2 10h3"/>'),
  scanner: I('<circle cx="11" cy="11" r="6.5"/><path d="M20 20l-4-4"/><path d="M8.5 11h5M11 8.5v5"/>'),
  positions: I('<path d="M4 7h16M4 12h16M4 17h10"/>'),
  analytics: I('<path d="M4 20V10M10 20V4M16 20v-7M22 20H2"/>'),
  wallet: I('<path d="M3 7a2 2 0 012-2h13v4"/><rect x="3" y="7" width="18" height="13" rx="2"/><path d="M16 13.5h2"/>'),
  scalp: I('<path d="M13 3L5 14h6l-1 7 8-11h-6z"/>'),
  funding: I('<circle cx="12" cy="12" r="8.5"/><path d="M12 7v5l3 2"/>'),
  live: I('<path d="M12 3l8 4v5c0 4.5-3.4 8.3-8 9-4.6-.7-8-4.5-8-9V7z"/><path d="M9 12l2 2 4-4"/>'),
  settings: I('<circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 00.3 1.8l.1.1a2 2 0 11-2.8 2.8l-.1-.1a1.7 1.7 0 00-1.8-.3 1.7 1.7 0 00-1 1.5V21a2 2 0 11-4 0v-.1a1.7 1.7 0 00-1.1-1.6 1.7 1.7 0 00-1.8.3l-.1.1a2 2 0 11-2.8-2.8l.1-.1a1.7 1.7 0 00.3-1.8 1.7 1.7 0 00-1.5-1H3a2 2 0 110-4h.1a1.7 1.7 0 001.6-1.1 1.7 1.7 0 00-.3-1.8l-.1-.1a2 2 0 112.8-2.8l.1.1a1.7 1.7 0 001.8.3H9a1.7 1.7 0 001-1.5V3a2 2 0 114 0v.1a1.7 1.7 0 001 1.5 1.7 1.7 0 001.8-.3l.1-.1a2 2 0 112.8 2.8l-.1.1a1.7 1.7 0 00-.3 1.8V9a1.7 1.7 0 001.5 1H21a2 2 0 110 4h-.1a1.7 1.7 0 00-1.5 1z"/>'),
  help: I('<circle cx="12" cy="12" r="9"/><path d="M9.5 9a2.5 2.5 0 015 .8c0 1.7-2.5 2.2-2.5 3.7M12 17h.01"/>'),
  pause: I('<path d="M8 5v14M16 5v14"/>'),
  play: I('<path d="M7 4l13 8-13 8z"/>'),
  stop: I('<rect x="6" y="6" width="12" height="12" rx="2"/>'),
  arrow: I('<path d="M5 12h14M13 6l6 6-6 6"/>'),
  close: I('<path d="M6 6l12 12M18 6L6 18"/>'),
  ok: I('<path d="M5 12l5 5 9-10"/>'),
  bad: I('<path d="M6 6l12 12M18 6L6 18"/>'),
  unk: I('<circle cx="12" cy="12" r="8"/>'),
  warn: I('<path d="M12 4l9 16H3z"/><path d="M12 10v4M12 17h.01"/>'),
  info: I('<circle cx="12" cy="12" r="9"/><path d="M12 11v5M12 8h.01"/>'),
  search: I('<circle cx="11" cy="11" r="7"/><path d="M20 20l-3.5-3.5"/>'),
  refresh: I('<path d="M20 11a8 8 0 10-2.3 5.7M20 4v7h-7"/>'),
};

// ───────────────────────── state ─────────────────────────
const S = {
  snap: null,
  settings: null,
  perf: null, perfDays: 7,
  ledger: null,
  daysSummary: null,
  funding: null,
  liveBalances: null, liveBalancesAt: 0, liveBalancesLoading: false,
  scalp: null,
  livePositions: null,
  transport: 'connecting',
  lastSnapAt: 0,
  page: 'overview',
  drawer: null,
  market: { q: '', onlyExec: false, sort: 'net', dir: -1 },
  scanner: { q: '' },
  trades: { src: 'recent' },
  routeHist: new Map(),   // route -> [{t, g, n}]
  pnlHist: [],            // [{t, v}]
  firstHero: true,
  mk: { sym: null, bookEx: null, longEx: null, shortEx: null, size: null, lev: 2, view: 'dev', venuesKey: '' },
  midHist: new Map(),     // sym -> { ex -> [{t, m}] }
};

// ───────────────────────── api ─────────────────────────
const api = {
  async req(method, url, body) {
    const r = await fetch(url, { method, headers: body !== undefined ? { 'Content-Type': 'application/json' } : {}, body: body !== undefined ? JSON.stringify(body) : undefined });
    const text = await r.text();
    let data = null; try { data = text ? JSON.parse(text) : null; } catch { data = text; }
    if (!r.ok) { const msg = (data && (data.message || data.error || data.title)) || text || r.statusText; throw new Error(String(msg).slice(0, 300)); }
    return data;
  },
  get: (u) => api.req('GET', u),
  post: (u, b) => api.req('POST', u, b === undefined ? {} : b),
  del: (u) => api.req('DELETE', u),
};

// ───────────────────────── toasts & modal ─────────────────────────
function toast(msg, kind = 'ok') {
  const box = $('#toasts');
  const el = document.createElement('div');
  el.className = 'toast ' + kind;
  el.setAttribute('role', kind === 'err' ? 'alert' : 'status');
  el.innerHTML = (kind === 'err' ? ICON.warn : ICON.ok) + `<div>${esc(msg)}</div>`;
  box.appendChild(el);
  setTimeout(() => el.remove(), kind === 'err' ? 7000 : 3500);
}

function confirmModal({ title, body, confirm = 'Подтвердить', danger = false, input = null }) {
  return new Promise((resolve) => {
    const wrap = document.createElement('div');
    wrap.className = 'modal';
    wrap.innerHTML = `<div class="modal-card" role="dialog" aria-modal="true" aria-labelledby="mt">
      <h3 id="mt">${esc(title)}</h3><p>${body}</p>
      ${input ? `<div class="field"><label for="mi">${esc(input.label)}</label><input id="mi" class="input" autocomplete="off" placeholder="${esc(input.placeholder || '')}" value="${esc(input.value || '')}"></div>` : ''}
      ${input && input.toggle ? `<label class="switch" style="margin-top:12px"><input type="checkbox" id="mt2" ${input.toggle.checked ? 'checked' : ''}><span class="track"></span><span>${esc(input.toggle.label)}</span></label>` : ''}
      <div class="acts"><button class="btn ghost" data-a="no">Отмена</button><button class="btn ${danger ? 'solid-danger' : 'primary'}" data-a="yes">${esc(confirm)}</button></div></div>`;
    document.body.appendChild(wrap);
    const done = (v) => { wrap.remove(); document.removeEventListener('keydown', onKey); resolve(v); };
    const onKey = (e) => { if (e.key === 'Escape') done(null); if (e.key === 'Enter' && input) $('[data-a="yes"]', wrap).click(); };
    document.addEventListener('keydown', onKey);
    wrap.addEventListener('click', (e) => {
      if (e.target === wrap) return done(null);
      const a = e.target.closest('[data-a]')?.dataset.a;
      if (a === 'no') done(null);
      if (a === 'yes') done(input ? { value: $('#mi', wrap).value, toggle: $('#mt2', wrap)?.checked } : true);
    });
    ($('#mi', wrap) || $('[data-a="yes"]', wrap)).focus();
  });
}

async function act(fn, okMsg) {
  try { const r = await fn(); if (okMsg) toast(typeof okMsg === 'function' ? okMsg(r) : okMsg); return r; }
  catch (e) { toast(e.message || 'Ошибка запроса', 'err'); return null; }
}

// ───────────────────────── derived data ─────────────────────────
const snap = () => S.snap || {};
const fp = () => snap().futuresPaper || {};
const liveSt = () => snap().liveStatus || {};
const minEdge = () => Math.max(0.02, num(S.settings?.trading?.minProfitPercent) ?? num(snap().minProfitPercent) ?? 0.02);
const minTpUsd = () => num(S.settings?.trading?.minTakeProfitUsd) ?? 0.1;

function modeOf() {
  const st = liveSt();
  if (st.killed) return { key: 'killed', label: 'Kill switch', sub: st.killReason || '' };
  if (st.canPlaceOrders) return { key: 'live', label: 'LIVE · реальные ордера', sub: '' };
  if (st.enabled && st.readOnly) return { key: 'readonly', label: 'Live, только чтение', sub: '' };
  return { key: 'paper', label: 'Paper', sub: '' };
}

const GATES = [
  ['basis-warmup', 'Базис прогревается', 'Маршрут копит историю fair basis'],
  ['stale', 'Котировка устарела', 'Нога старше лимита, разнесена во времени или пришла из REST'],
  ['ban', 'Маршрут в бане', 'После стопа, мёртвой книги или структурного базиса'],
  ['spike', 'В пределах шума', 'Excess меньше обычного разброса пары'],
  ['touch', 'Мало на вершине', 'Лучший уровень меньше 25% размера'],
  ['gross', 'Gross ниже минимума', 'Сырой спред меньше порога'],
  ['rt', 'Чистый edge ниже минимума', 'После 4 комиссий, выхода и запаса'],
  ['tp', 'Ожидаемый $ мал', 'Долларовая прибыль ниже минимальной цели'],
  ['fund', 'Funding съест edge', 'Расчёт funding в горизонте удержания'],
  ['fill', 'Стакан не заполнит', 'Глубины не хватает на размер'],
  ['fade', 'Спред схлопывается', 'Gross падает с прошлого скана'],
  ['persist', 'Держится недолго', 'Сигнал ещё не простоял нужное время'],
  ['gate', 'Прочие гейты', ''],
];
const gateLabel = (g) => (GATES.find(x => x[0] === g) || [g, g || '—'])[1];
const gateIdx = (g) => GATES.findIndex(x => x[0] === g);

function opps() { return (snap().opportunities || []).map(lc); }

function decompose(o) {
  const gross = num(o.grossSpreadPercent) ?? 0;
  const fair = Math.max(0, num(o.fairBasisPercent) ?? 0);
  const fees = 2 * ((num(o.buyFeePercent) ?? 0.05) + (num(o.sellFeePercent) ?? 0.05));
  const exit = Math.max(0, num(o.exitCrossPercent) ?? 0);
  const net = num(o.netRoundTripPercent) ?? 0;
  const excess = num(o.excessPercent) ?? gross - fair;
  const reserve = Math.max(0, excess - fees - exit - net);
  return { gross, fair, fees, exit, reserve, net, excess };
}

function trackRoutes() {
  const now = Date.now();
  for (const o of opps()) {
    const k = `${o.symbol}|${o.longExchange}|${o.shortExchange}`;
    let h = S.routeHist.get(k); if (!h) { h = []; S.routeHist.set(k, h); }
    const last = h[h.length - 1];
    if (!last || now - last.t > 900) { h.push({ t: now, g: num(o.grossSpreadPercent), n: num(o.netRoundTripPercent) }); if (h.length > 240) h.shift(); }
  }
  if (S.routeHist.size > 400) { const keys = [...S.routeHist.keys()].slice(0, S.routeHist.size - 400); keys.forEach(k => S.routeHist.delete(k)); }
  const total = (num(fp().realizedPnlUsd) ?? 0) + (num(fp().unrealizedPnlUsd) ?? 0);
  const lp = S.pnlHist[S.pnlHist.length - 1];
  if (!lp || now - lp.t > 5000) { S.pnlHist.push({ t: now, v: total }); if (S.pnlHist.length > 720) S.pnlHist.shift(); }
}

// ───────────────────────── small components ─────────────────────────
const routeHtml = (l, s) => `<span class="route"><span class="l">${esc(venue(l))}</span>${ICON.arrow}<span class="s">${esc(venue(s))}</span></span>`;

function spark(points, { w = 120, h = 28, key = 'v', color = 'var(--long)', zero = false } = {}) {
  const vals = points.map(p => num(p[key])).filter(v => v != null);
  if (vals.length < 2) return `<svg class="chart" width="${w}" height="${h}"></svg>`;
  let lo = Math.min(...vals), hi = Math.max(...vals); if (zero) { lo = Math.min(lo, 0); hi = Math.max(hi, 0); }
  if (hi - lo < 1e-9) { hi += 1e-6; lo -= 1e-6; }
  const xs = (i) => (i / (vals.length - 1)) * (w - 2) + 1;
  const ys = (v) => h - 2 - ((v - lo) / (hi - lo)) * (h - 4);
  const d = vals.map((v, i) => `${i ? 'L' : 'M'}${xs(i).toFixed(1)},${ys(v).toFixed(1)}`).join('');
  const z = zero ? `<line x1="0" x2="${w}" y1="${ys(0)}" y2="${ys(0)}" stroke="var(--faint)" stroke-dasharray="2 3"/>` : '';
  return `<svg class="chart" width="${w}" height="${h}" viewBox="0 0 ${w} ${h}" aria-hidden="true">${z}<path d="${d}" fill="none" stroke="${color}" stroke-width="1.6"/></svg>`;
}

function waterfall(o, { legend = true } = {}) {
  const d = decompose(o);
  const me = minEdge();
  const costs = d.fair + d.fees + d.exit + d.reserve;
  const scale = Math.max(d.gross, costs + Math.max(d.net, 0), costs + me) * 1.04 || 1;
  const w = (v) => Math.max(0, v / scale * 100).toFixed(2) + '%';
  const netSeg = d.net > 0 ? `<div class="wf-seg net" style="width:${w(d.net)}" title="Чистый edge ${fmt.pct(d.net)}"></div>` : '';
  const shortSeg = d.net < 0 ? '' : '';
  const grossPos = Math.min(100, d.gross / scale * 100);
  const minPos = Math.min(100, (costs + me) / scale * 100);
  return `<div class="wf">
    <div class="wf-bar" role="img" aria-label="Разбор спреда: gross ${fmt.pct(d.gross)}, издержки ${fmt.pct(costs, 3, false)}, чистый ${fmt.pct(d.net)}">
      <div class="wf-track">
      <div class="wf-seg basis" style="width:${w(d.fair)}" title="Fair basis ${fmt.pct(d.fair, 3, false)}"></div>
      <div class="wf-seg fees" style="width:${w(d.fees)}" title="4 комиссии taker ${fmt.pct(d.fees, 3, false)}"></div>
      <div class="wf-seg exit" style="width:${w(d.exit)}" title="Ширина стаканов на выходе ${fmt.pct(d.exit, 3, false)}"></div>
      <div class="wf-seg reserve" style="width:${w(d.reserve)}" title="Запас на исполнение ${fmt.pct(d.reserve, 3, false)}"></div>
      ${netSeg}${shortSeg}
      </div>
      <div class="wf-min ${minPos > 82 ? 'edge-r' : ''}" style="left:${minPos}%"><span>порог входа</span></div>
      ${d.net < 0 ? `<div class="wf-min gross" style="left:${grossPos}%"><span>весь gross</span></div>` : ''}
    </div>
    ${legend ? `<div class="wf-legend">
      <div><span class="k">Gross спред</span><span class="v">${fmt.pct(d.gross, 3, false)}</span></div>
      <div><span class="k"><i style="background:rgb(242 184 75 / .35)"></i>Fair basis</span><span class="v cost">−${fmt.pct(d.fair, 3, false)}</span></div>
      <div><span class="k"><i style="background:rgb(242 184 75 / .62)"></i>4 комиссии</span><span class="v cost">−${fmt.pct(d.fees, 3, false)}</span></div>
      <div><span class="k"><i style="background:rgb(242 184 75 / .42)"></i>Выход по стакану</span><span class="v cost">−${fmt.pct(d.exit, 3, false)}</span></div>
      <div><span class="k"><i style="background:rgb(242 184 75 / .26)"></i>Запас</span><span class="v cost">−${fmt.pct(d.reserve, 3, false)}</span></div>
      <div><span class="k"><i style="background:${d.net >= 0 ? 'var(--profit)' : 'var(--loss)'}"></i>Чистый edge</span><span class="v ${cls(d.net)}">${fmt.pct(d.net)}</span></div>
    </div>` : ''}
  </div>`;
}

function checklist(o) {
  const d = decompose(o);
  const t = S.settings?.trading || {};
  const exec = !!o.isExecutable;
  const gi = gateIdx(o.gateNote);
  const cat = (key) => exec ? 'ok' : gi < 0 ? 'unk' : gateIdx(key) < gi ? 'ok' : gateIdx(key) === gi ? 'bad' : 'unk';
  const openFees = (num(o.buyFeePercent) ?? 0.05) + (num(o.sellFeePercent) ?? 0.05);
  const minGross = Math.max(num(t.minGrossSpreadPercent) || openFees + 0.04, openFees + 0.02);
  const items = [
    ['Fair basis прогрет', o.basisReady ? 'ok' : 'bad', o.basisReady ? `Базис ${fmt.pct(d.fair, 3, false)}` : 'Нужно ≥ ' + (t.basisMinSpanSeconds || 90) + ' с истории'],
    ['Свежие котировки', cat('stale'), 'Обе ноги из сокетов и моложе лимита'],
    ['Маршрут не в бане', cat('ban'), ''],
    ['Выше шума пары', cat('spike'), `Excess ${fmt.pct(d.excess)}`],
    ['Ликвидность на вершине', cat('touch'), '≥ 25% размера на лучшем уровне'],
    ['Gross ≥ минимума', d.gross >= minGross ? 'ok' : 'bad', `${fmt.pct(d.gross, 3, false)} из ${fmt.pct(minGross, 3, false)}`],
    ['Чистый edge ≥ порога', d.net >= minEdge() ? 'ok' : 'bad', `${fmt.pct(d.net)} из ${fmt.pct(minEdge(), 3, false)}`],
    ['Ожидаемый $ ≥ цели', (num(o.estNetPnlUsd) ?? -1) >= minTpUsd() ? 'ok' : 'bad', `${fmt.usd(o.estNetPnlUsd)} из ${fmt.usd(minTpUsd(), 2, false)}`],
    ['Funding не мешает', cat('fund'), o.expectedFundingPct != null ? `Дельта ${fmt.pct(o.expectedFundingPct, 4)}` : ''],
    ['Стакан заполняет размер', o.fullyFilled ? 'ok' : 'bad', `${fmt.usd(o.quoteSize, 0, false)} по VWAP`],
    ['Спред не схлопывается', cat('fade'), ''],
    ['Сигнал удержан', cat('persist'), (t.minSpreadPersistMs || 300) + ' мс подряд'],
  ];
  return `<div class="checks">${items.map(([tt, st, dd]) => `<div class="check ${st}">${st === 'ok' ? ICON.ok : st === 'bad' ? ICON.bad : ICON.unk}<div><div class="t">${esc(tt)}</div>${dd ? `<div class="d">${esc(dd)}</div>` : ''}</div></div>`).join('')}</div>`;
}

function ladder(sym, ex, side) {
  const dep = snap().orderBookDepth?.[sym]?.[ex];
  const tk = snap().bookTickers?.[sym]?.[ex];
  if (!dep && !tk) return `<div class="ladder"><h4>${esc(venue(ex))}</h4><div class="mut">Нет данных стакана</div></div>`;
  const asks = (dep?.asks || (tk ? [{ price: tk.bestAsk, qty: tk.askQuantity }] : [])).slice(0, 8).map(lc);
  const bids = (dep?.bids || (tk ? [{ price: tk.bestBid, qty: tk.bidQuantity }] : [])).slice(0, 8).map(lc);
  const maxQ = Math.max(1e-12, ...asks.map(a => num(a.price) * num(a.qty) || 0), ...bids.map(b => num(b.price) * num(b.qty) || 0));
  const row = (lv, k) => { const usd = (num(lv.price) || 0) * (num(lv.qty) || 0); return `<div class="lrow ${k}"><div class="bg" style="width:${(usd / maxQ * 100).toFixed(1)}%"></div><span>${fmt.price(lv.price)}</span><span>${fmt.usd(usd, 0, false)}</span></div>`; };
  const ba = num(asks[0]?.price), bb = num(bids[0]?.price);
  const width = ba && bb ? ((ba - bb) / bb * 100) : null;
  return `<div class="ladder"><h4><span class="${side === 'long' ? 'long' : 'short'}">${esc(venue(ex))} · ${side === 'long' ? 'покупаем ask' : 'продаём bid'}</span><span class="mut num">${esc(dep?.source || 'ticker')}</span></h4>
    ${asks.slice().reverse().map(a => row(a, 'ask')).join('')}
    <div class="lmid">ширина ${width != null ? fmt.pct(width, 3, false) : '—'}</div>
    ${bids.map(b => row(b, 'bid')).join('')}</div>`;
}

// ───────────────────────── pages ─────────────────────────
const PAGES = {
  overview: { title: 'Обзор', icon: 'overview' },
  market: { title: 'Рынок', icon: 'market' },
  signals: { title: 'Сигналы', icon: 'signals' },
  scanner: { title: 'Сканер рынка', icon: 'scanner' },
  positions: { title: 'Позиции и сделки', icon: 'positions' },
  balances: { title: 'Балансы', icon: 'wallet' },
  scalp: { title: 'Скальпер', icon: 'scalp' },
  analytics: { title: 'Аналитика', icon: 'analytics' },
  funding: { title: 'Funding', icon: 'funding' },
  live: { title: 'Live-торговля', icon: 'live' },
  settings: { title: 'Настройки', icon: 'settings' },
  help: { title: 'Как это работает', icon: 'help' },
};

// Overview ------------------------------------------------------
function renderOverview() {
  const list = opps();
  const best = list.find(o => o.isExecutable) || list[0];
  const p = fp();
  const an = lc(snap().paperAnalytics || {});
  const health = (snap().exchangeHealth || []).map(lc);

  const hero = best ? `
    <div class="lens-top">
      <div class="lens-route">
        <div class="mut small" style="margin-bottom:8px">${best.isExecutable ? 'Готов к входу сейчас' : 'Лучший маршрут, не прошёл: ' + esc(gateLabel(best.gateNote))}</div>
        <div class="lens-sym">${esc(baseOf(best.symbol))}<small>USDT perp</small></div>
        <div class="legs">
          <div class="leg l"><span class="k">Long · ${esc(venue(best.longExchange))}</span><span class="v">${fmt.price(best.buyPriceVwap)}</span></div>
          <div class="leg s"><span class="k">Short · ${esc(venue(best.shortExchange))}</span><span class="v">${fmt.price(best.sellPriceVwap)}</span></div>
        </div>
      </div>
      <div class="lens-verdict">
        <div class="lens-net ${cls(best.netRoundTripPercent)}">${fmt.pct(best.netRoundTripPercent)}</div>
        <div class="lens-net-lbl">чистый edge после всех издержек</div>
        <div class="lens-usd">${signed(best.estNetPnlUsd)} <span class="mut small">на ${fmt.usd(best.quoteSize, 0, false)}</span></div>
      </div>
    </div>
    ${waterfall(best)}` : `<div class="lens-empty"><strong>Сигналов пока нет</strong>Бот сканирует ${fmt.int((snap().symbols || []).length)} символов на ${fmt.int((snap().exchanges || []).length)} биржах. Маршруты появятся, когда в стаканах будет хотя бы две свежие котировки.</div>`;

  const blockers = {};
  for (const o of list) if (!o.isExecutable) blockers[o.gateNote || 'gate'] = (blockers[o.gateNote || 'gate'] || 0) + 1;
  const execN = list.filter(o => o.isExecutable).length;
  const bTotal = Math.max(1, list.length);
  const bRows = Object.entries(blockers).sort((a, b) => b[1] - a[1]).slice(0, 7);

  const positions = (p.positions || []).map(lc);
  const trades = (p.trades || []).map(lc).filter(t => !t.isOpen).slice(0, 8);

  return `
  <div class="grid cols-hero">
    <section class="panel lens ${S.firstHero ? 'enter' : ''}" aria-label="Лучший маршрут">${hero}</section>
    <section class="panel">
      <div class="panel-head"><h2>Результат</h2><span class="sub">${esc(an.dayUtc || '')} UTC</span></div>
      <div class="panel-body stack" style="gap:14px">
        <div><div class="mut small">Сегодня, реализовано</div><div class="num" style="font-size:30px;letter-spacing:-.03em">${signed(p.dailyRealizedPnlUsd)}</div></div>
        <div class="row between"><span class="mut small">Всего реализовано</span>${signed(p.realizedPnlUsd)}</div>
        <div class="row between"><span class="mut small">Нереализовано</span>${signed(p.unrealizedPnlUsd)}</div>
        <div class="row between"><span class="mut small">Открыто хеджей</span><span class="num">${fmt.int(p.openCount)}</span></div>
        <div>${S.pnlHist.length > 1 ? spark(S.pnlHist, { w: 320, h: 56, zero: true, color: 'var(--profit)' }) : '<div class="mut small" style="height:56px;display:flex;align-items:center">Кривая начнёт строиться через несколько секунд</div>'}</div>
        <div class="mut small">P&amp;L этой сессии браузера, точка раз в 5 с</div>
      </div>
    </section>
  </div>

  <section class="panel" style="margin-top:18px">
    <div class="kpis">
      <div class="kpi"><div class="k">Сканов за день</div><div class="v">${fmt.int(an.scans)}</div><div class="d">всего ${fmt.int(snap().scanCount)}</div></div>
      <div class="kpi"><div class="k">Входов / выходов</div><div class="v">${fmt.int(an.opens)} / ${fmt.int(an.closes)}</div><div class="d">за день UTC</div></div>
      <div class="kpi"><div class="k">Маршрутов сравнено</div><div class="v">${fmt.int(snap().lastPairsCompared)}</div><div class="d">книг готово ${fmt.int(snap().lastBooksReady)}</div></div>
      <div class="kpi"><div class="k">Лучший gross</div><div class="v">${fmt.pct(snap().lastBestGrossPercent, 3, false)}</div><div class="d">в последнем скане</div></div>
      <div class="kpi"><div class="k">Готовых сигналов</div><div class="v ${execN ? 'pos' : ''}">${fmt.int(execN)}</div><div class="d">из ${fmt.int(list.length)} в выдаче</div></div>
    </div>
  </section>

  <section style="margin-top:18px" aria-label="Биржи">
    <div class="venues">${health.length ? health.map(v => `<div class="venue"><div class="n"><span class="led ${v.state === 'live' ? 'ok' : v.state === 'error' ? 'bad' : 'warn'}"></span>${esc(venue(v.name))}</div><div class="m">${v.state === 'live' ? 'Стримы ' + fmt.int(v.liveStreams) + ' из ' + fmt.int(v.totalStreams) : v.state === 'error' ? 'Ошибка подключения' : v.state === 'idle' ? 'Не подключена' : 'Подключается'}</div></div>`).join('') : '<div class="venue"><div class="m">Нет данных о биржах</div></div>'}</div>
  </section>

  <div class="grid cols-2" style="margin-top:18px">
    <section class="panel">
      <div class="panel-head"><h2>Почему нет входа</h2><span class="sub">первый непройденный гейт по маршрутам</span></div>
      <div class="panel-body">${bRows.length || execN ? `<div class="bars">
        ${execN ? `<div class="bar-row"><span class="t">Готов к входу</span><div class="track"><div class="fill ok" style="width:${execN / bTotal * 100}%"></div></div><span class="c">${execN}</span></div>` : ''}
        ${bRows.map(([g, c]) => `<div class="bar-row"><span class="t" title="${esc((GATES.find(x => x[0] === g) || [])[2] || '')}">${esc(gateLabel(g))}</span><div class="track"><div class="fill" style="width:${c / bTotal * 100}%"></div></div><span class="c">${c}</span></div>`).join('')}
      </div>` : '<div class="empty">Нет маршрутов для разбора</div>'}</div>
    </section>
    <section class="panel">
      <div class="panel-head"><h2>Открытые хеджи</h2><div class="tools"><button class="btn sm ghost" data-go="positions">Все позиции</button></div></div>
      <div class="panel-body flush">${positions.length ? `<div class="tbl-wrap"><table class="tbl"><thead><tr><th>Символ</th><th>Маршрут</th><th class="r">Держим</th><th class="r">P&amp;L</th></tr></thead><tbody>
        ${positions.map(x => `<tr><td class="sym">${esc(baseOf(x.symbol))}</td><td>${routeHtml(x.longExchange, x.shortExchange)}</td><td class="r num">${fmt.dur(x.holdSeconds)}</td><td class="r">${signed(x.unrealizedPnlUsd)}</td></tr>`).join('')}
      </tbody></table></div>` : '<div class="empty"><strong>Позиций нет</strong>Хедж откроется, когда маршрут пройдёт все гейты.</div>'}</div>
    </section>
  </div>

  <section class="panel" style="margin-top:18px">
    <div class="panel-head"><h2>Последние сделки</h2><div class="tools"><button class="btn sm ghost" data-go="positions">Журнал</button></div></div>
    <div class="panel-body flush">${tradesTable(trades)}</div>
  </section>`;
}

function statusBadge(st) {
  const s = String(st || '');
  if (s === 'Open') return '<span class="badge long">открыта</span>';
  const m = /Closed\(([^)]+)\)/.exec(s); const r = m ? m[1] : s;
  const map = { tp: ['тейк', 'pos'], converge: ['схождение', 'pos'], trail: ['трейл', 'pos'], 'timeout-harvest': ['по времени', 'pos'], stop: ['стоп', 'neg'], manual: ['вручную', ''], orphan: ['сирота', 'neg'], 'stale-no-book': ['нет книги', 'neg'], 'leg-miss': ['промах ноги', 'neg'], reset: ['сброс', ''], failed: ['не сошлось', 'neg'] };
  const [t, c] = map[r] || [r, ''];
  return `<span class="badge ${c}">${esc(t)}</span>`;
}

function tradesTable(trades, { showOpen = false } = {}) {
  if (!trades.length) return '<div class="empty"><strong>Сделок ещё нет</strong>Закрытые хеджи появятся здесь.</div>';
  return `<div class="tbl-wrap"><table class="tbl"><thead><tr><th>Закрыта</th><th>Символ</th><th>Маршрут</th><th class="r">Вход L / S</th><th class="r">Держали</th><th class="r">Комиссии</th><th class="r">P&amp;L</th><th>Итог</th></tr></thead><tbody>
  ${trades.map(t => {
    const open = new Date(t.openedAt).getTime(), close = new Date(t.closedAt).getTime();
    const hold = Number.isFinite(open) && Number.isFinite(close) ? (close - open) / 1000 : (num(t.holdMin) != null ? num(t.holdMin) * 60 : null);
    const fees = (num(t.openFeesUsd) ?? 0) + (num(t.closeFeesUsd) ?? 0);
    return `<tr title="${esc(t.message || '')}"><td class="mut">${t.closedAt ? fmt.dt(t.closedAt) : showOpen ? 'открыта' : '—'}</td><td class="sym">${esc(baseOf(t.symbol))}</td><td>${routeHtml(t.longExchange, t.shortExchange)}</td><td class="r num">${fmt.price(t.longEntry)} / ${fmt.price(t.shortEntry)}</td><td class="r num">${hold != null ? fmt.dur(hold) : '—'}</td><td class="r num mut">${fees ? fmt.usd(fees, 2, false) : '—'}</td><td class="r">${signed(t.realizedPnlUsd)}</td><td>${statusBadge(t.status)}</td></tr>`;
  }).join('')}</tbody></table></div>`;
}

// Market / signals ----------------------------------------------
function renderMarket() {
  const m = S.market;
  let list = opps();
  if (m.onlyExec) list = list.filter(o => o.isExecutable);
  if (m.q) { const q = m.q.toLowerCase(); list = list.filter(o => (o.symbol + ' ' + o.longExchange + ' ' + o.shortExchange).toLowerCase().includes(q)); }
  const key = { net: 'netRoundTripPercent', gross: 'grossSpreadPercent', excess: 'excessPercent', usd: 'estNetPnlUsd', sym: 'symbol' }[m.sort] || 'netRoundTripPercent';
  list.sort((a, b) => { if (key === 'symbol') return m.dir * String(a.symbol).localeCompare(b.symbol); return m.dir * ((num(a[key]) ?? -1e9) - (num(b[key]) ?? -1e9)); });
  const th = (k, label, r = true) => `<th class="${r ? 'r' : ''}"><button data-sort="${k}" aria-label="Сортировать по ${esc(label)}">${esc(label)}${m.sort === k ? (m.dir < 0 ? ' ↓' : ' ↑') : ''}</button></th>`;
  return `
  <div class="toolbar">
    <div class="search">${ICON.search}<input class="input" id="mq" placeholder="Символ или биржа" value="${esc(m.q)}" aria-label="Фильтр сигналов"></div>
    <label class="switch"><input type="checkbox" id="mexec" ${m.onlyExec ? 'checked' : ''}><span class="track"></span><span>Только готовые к входу</span></label>
    <span class="grow"></span>
    <span class="mut small">Порог входа ${fmt.pct(minEdge(), 3, false)} · обновлено ${fmt.ago(snap().lastScanUtc)}</span>
  </div>
  <section class="panel">
    <div class="panel-body flush">${list.length ? `<div class="tbl-wrap"><table class="tbl"><thead><tr>
      ${th('sym', 'Символ', false)}<th>Маршрут</th><th class="r">Long ask</th><th class="r">Short bid</th>${th('gross', 'Gross')}<th class="r">Базис</th>${th('excess', 'Excess')}${th('net', 'Чистый')}${th('usd', '$ на размер')}<th class="r">Динамика</th><th>Статус</th></tr></thead><tbody>
      ${list.map(o => { const k = `${o.symbol}|${o.longExchange}|${o.shortExchange}`; return `<tr class="click ${o.isExecutable ? 'exec' : ''}" data-route="${esc(k)}" tabindex="0">
        <td class="sym">${esc(baseOf(o.symbol))}</td><td>${routeHtml(o.longExchange, o.shortExchange)}</td>
        <td class="r num long">${fmt.price(o.buyPriceVwap)}</td><td class="r num short">${fmt.price(o.sellPriceVwap)}</td>
        <td class="r num">${fmt.pct(o.grossSpreadPercent, 3, false)}</td><td class="r num cost">${o.basisReady ? fmt.pct(o.fairBasisPercent, 3, false) : '<span class="mut">прогрев</span>'}</td>
        <td class="r num">${fmt.pct(o.excessPercent)}</td><td class="r num ${cls(o.netRoundTripPercent)}">${fmt.pct(o.netRoundTripPercent)}</td>
        <td class="r">${signed(o.estNetPnlUsd)}</td><td class="r">${spark(S.routeHist.get(k) || [], { w: 90, h: 22, key: 'g', color: 'var(--text-2)' })}</td>
        <td>${o.isExecutable ? '<span class="badge pos">готов</span>' : `<span class="badge cost">${esc(gateLabel(o.gateNote))}</span>`}</td></tr>`; }).join('')}
    </tbody></table></div>` : `<div class="empty"><strong>${m.onlyExec ? 'Нет маршрутов, готовых к входу' : 'Нет сигналов'}</strong>${m.onlyExec ? 'Снимите фильтр, чтобы увидеть, какой гейт их держит.' : 'Ждём свежие стаканы минимум с двух бирж.'}</div>`}</div>
  </section>`;
}

function renderDrawer() {
  const d = S.drawer; if (!d) return;
  const o = opps().find(x => `${x.symbol}|${x.longExchange}|${x.shortExchange}` === d.route);
  const body = $('#drawerBody'); const head = $('#drawerTitle');
  if (!o) { head.textContent = d.route.split('|')[0]; body.innerHTML = '<div class="empty"><strong>Маршрут выпал из выдачи</strong>Спред ушёл ниже порога показа или стакан устарел.</div>'; return; }
  head.innerHTML = `${esc(baseOf(o.symbol))} <span class="mut" style="font-weight:500;font-size:14px">${routeHtml(o.longExchange, o.shortExchange)}</span>`;
  const hist = S.routeHist.get(d.route) || [];
  body.innerHTML = `
    <div class="row wrap" style="gap:16px">
      <div><div class="mut small">Чистый edge</div><div class="num ${cls(o.netRoundTripPercent)}" style="font-size:28px">${fmt.pct(o.netRoundTripPercent)}</div></div>
      <div><div class="mut small">Ожидаемый результат</div><div style="font-size:20px">${signed(o.estNetPnlUsd)}</div></div>
      <div><div class="mut small">Размер</div><div class="num" style="font-size:20px">${fmt.usd(o.quoteSize, 0, false)}</div></div>
      <div style="margin-left:auto" class="row">${o.isExecutable ? '<span class="badge pos">готов к входу</span>' : `<span class="badge cost">${esc(gateLabel(o.gateNote))}</span>`}<button class="btn sm" data-act="to-terminal" data-sym="${esc(o.symbol)}" data-l="${esc(o.longExchange)}" data-s="${esc(o.shortExchange)}">Открыть в терминале</button></div>
    </div>
    <section class="panel">${waterfall(o)}</section>
    <section><h3 style="font-size:15px;margin:0 0 10px">Гейты входа</h3>${checklist(o)}</section>
    <section class="panel"><div class="panel-head"><h2>Gross спред за сессию</h2><span class="sub">${hist.length} точек</span></div><div class="panel-body">${spark(hist, { w: 700, h: 90, key: 'g', color: 'var(--cost)' })}</div></section>
    <section><h3 style="font-size:15px;margin:0 0 10px">Стаканы ног</h3><div class="ladders">${ladder(o.symbol, o.longExchange, 'long')}${ladder(o.symbol, o.shortExchange, 'short')}</div></section>
    <section class="panel"><div class="panel-head"><h2>Все биржи по ${esc(baseOf(o.symbol))}</h2></div><div class="panel-body flush">${matrix(o.symbol)}</div></section>`;
}

function matrix(sym) {
  const books = snap().bookTickers?.[sym] || {};
  const rows = Object.entries(books).map(([ex, t]) => ({ ex, ...lc(t) })).filter(r => num(r.bestBid) > 0);
  if (!rows.length) return '<div class="empty">Нет котировок</div>';
  const maxBid = Math.max(...rows.map(r => num(r.bestBid)));
  const minAsk = Math.min(...rows.map(r => num(r.bestAsk)));
  return `<div class="tbl-wrap"><table class="tbl matrix"><thead><tr><th>Биржа</th><th class="r">Bid</th><th class="r">Ask</th><th class="r">Ширина</th><th class="r">Глубина вершины</th><th class="r">Возраст</th></tr></thead><tbody>
    ${rows.map(r => { const w = (num(r.bestAsk) - num(r.bestBid)) / num(r.bestBid) * 100; const age = (Date.now() - new Date(r.timestamp).getTime()) / 1000; return `<tr><td>${esc(venue(r.ex))}</td><td class="r num ${num(r.bestBid) === maxBid ? 'best-bid' : ''}">${fmt.price(r.bestBid)}</td><td class="r num ${num(r.bestAsk) === minAsk ? 'best-ask' : ''}">${fmt.price(r.bestAsk)}</td><td class="r num">${fmt.pct(w, 3, false)}</td><td class="r num mut">${fmt.usd(num(r.bestBid) * num(r.bidQuantity), 0, false)} / ${fmt.usd(num(r.bestAsk) * num(r.askQuantity), 0, false)}</td><td class="r num ${age > 1.5 ? 'cost' : 'mut'}">${Number.isFinite(age) ? fmt.dur(Math.max(0, age)) : '—'}</td></tr>`; }).join('')}
  </tbody></table></div><div class="mut small" style="padding:8px 12px 12px">Голубым — самый дешёвый ask (куда идёт long), лавандовым — самый дорогой bid (куда идёт short).</div>`;
}


// Market terminal --------------------------------------------------
function quotesFor(sym) {
  const bt = snap().bookTickers?.[sym] || {};
  return Object.entries(bt).map(([ex, t]) => ({ ex, ...lc(t) })).filter(r => num(r.bestBid) > 0 && num(r.bestAsk) > 0);
}
function symbolsList() {
  const syms = new Set([...(snap().symbols || []), ...Object.keys(snap().bookTickers || {})]);
  return [...syms].map(sym => {
    const q = quotesFor(sym); if (q.length < 2) return { sym, cross: null, n: q.length };
    const bb = Math.max(...q.map(r => num(r.bestBid))), ba = Math.min(...q.map(r => num(r.bestAsk)));
    return { sym, cross: (bb - ba) / ba * 100, n: q.length };
  }).sort((a, b) => (b.cross ?? -9) - (a.cross ?? -9));
}
function bestRoute(sym) {
  const q = quotesFor(sym); if (q.length < 2) return null;
  const l = q.reduce((a, b) => num(b.bestAsk) < num(a.bestAsk) ? b : a);
  const sq = q.filter(r => r.ex !== l.ex); const sh = sq.reduce((a, b) => num(b.bestBid) > num(a.bestBid) ? b : a);
  return { longEx: l.ex, shortEx: sh.ex };
}
function ensureMk() {
  const m = S.mk;
  const list = symbolsList();
  if (!m.sym || !list.find(x => x.sym === m.sym)) m.sym = list[0]?.sym || null;
  if (m.size == null) m.size = num(S.settings?.trading?.quoteSize) || num(snap().quoteSize) || 100;
  const q = m.sym ? quotesFor(m.sym) : [];
  const exs = q.map(r => r.ex);
  if (!m.longEx || !exs.includes(m.longEx) || !m.shortEx || !exs.includes(m.shortEx) || m.longEx === m.shortEx) {
    const br = m.sym ? bestRoute(m.sym) : null; if (br) { m.longEx = br.longEx; m.shortEx = br.shortEx; }
  }
  if (!m.bookEx || !exs.includes(m.bookEx)) m.bookEx = m.longEx || exs[0] || null;
  return { list, q };
}

function brushScore(sym) {
  const h = S.midHist.get(sym); if (!h) return null;
  const series = Object.values(h).filter(a => a.length);
  if (!series.length) return null;
  const n = Math.min(...series.map(a => a.length)); if (n < 12) return { score: 0, regime: 'cold', note: 'копим историю цены', rangePct: 0 };
  const ms = []; for (let i = 0; i < n; i++) { const v = series.map(a => a[a.length - n + i].m).sort((x, y) => x - y); ms.push(v[Math.floor(v.length / 2)]); }
  let hi = Math.max(...ms), lo = Math.min(...ms); const mid = (hi + lo) / 2, range = hi - lo, rangePct = mid > 0 ? range / mid * 100 : 0;
  let osc = 0, cross = 0;
  for (let i = 2; i < n; i++) { const d0 = ms[i - 1] - ms[i - 2], d1 = ms[i] - ms[i - 1]; if (d0 && d1 && d0 * d1 < 0) osc++; }
  for (let i = 1; i < n; i++) if ((ms[i - 1] - mid) * (ms[i] - mid) < 0) cross++;
  const trend = range > 0 ? Math.abs(ms[n - 1] - ms[0]) / range : 1;
  let rs = 0; if (rangePct >= 0.08 && rangePct <= 2.5) rs = rangePct <= 1.2 ? 30 : 22; else if (rangePct > 2.5 && rangePct <= 4) rs = 10; else if (rangePct > 0) rs = 6;
  let score = Math.round(Math.min(100, rs + Math.min(35, osc / Math.max(1, n - 2) * 120) + Math.min(20, cross / Math.max(1, n - 1) * 80) + Math.max(0, 15 * (1 - Math.min(1, trend)))));
  if (n < 30) score = Math.round(score * (0.55 + 0.45 * n / 30));
  const regime = score >= 70 && trend < 0.55 && rangePct >= 0.1 ? 'brush' : score >= 55 ? 'watch' : trend > 0.75 && rangePct > 0.3 ? 'trend' : 'cold';
  const note = { brush: 'пила в коридоре', watch: 'похоже на диапазон', trend: 'направленное движение', cold: 'нет выраженного диапазона' }[regime];
  return { score, regime, note, rangePct };
}

function priceChart(sym) {
  const h = S.midHist.get(sym); const m = S.mk;
  if (!h) return '<div class="empty">Копим историю цен…</div>';
  const exs = Object.keys(h).filter(ex => h[ex].length > 1);
  if (!exs.length) return '<div class="empty">Копим историю цен…</div>';
  const W = 900, H = 400, L = 8, R = 92, T = 14, Bm = 22;
  const tMax = Math.max(...exs.map(ex => h[ex][h[ex].length - 1].t));
  const tFirst = Math.min(...exs.map(ex => h[ex][0].t));
  const tMin = Math.max(tMax - 4 * 60 * 1000, Math.min(tFirst, tMax - 30 * 1000));
  // median across venues at each venue sample time (approx: latest-by-time per venue)
  const medAt = (t) => { const v = exs.map(ex => { const a = h[ex]; let best = null; for (let i = a.length - 1; i >= 0; i--) { if (a[i].t <= t) { best = a[i].m; break; } } return best; }).filter(x => x != null).sort((a, b) => a - b); return v.length ? v[Math.floor(v.length / 2)] : null; };
  const series = exs.map(ex => ({ ex, pts: h[ex].filter(p => p.t >= tMin).map(p => { if (m.view === 'price') return { t: p.t, v: p.m }; const md = medAt(p.t); return md ? { t: p.t, v: (p.m - md) / md * 10000 } : null; }).filter(Boolean) })).filter(s => s.pts.length > 1);
  if (!series.length) return '<div class="empty">Копим историю цен…</div>';
  let lo = Math.min(...series.flatMap(s => s.pts.map(p => p.v))), hi = Math.max(...series.flatMap(s => s.pts.map(p => p.v)));
  if (m.view !== 'price') { const a = Math.max(Math.abs(lo), Math.abs(hi), 2); lo = -a; hi = a; } else { const pd = (hi - lo) * 0.1 || hi * 0.0005; lo -= pd; hi += pd; }
  const x = (t) => L + (t - tMin) / (tMax - tMin) * (W - L - R);
  const y = (v) => T + (1 - (v - lo) / (hi - lo)) * (H - T - Bm);
  const order = series.sort((a, b) => (a.ex === m.longEx || a.ex === m.shortEx) - (b.ex === m.longEx || b.ex === m.shortEx));
  const lines = order.map(s => {
    const role = s.ex === m.longEx ? 'long' : s.ex === m.shortEx ? 'short' : '';
    const col = role === 'long' ? 'var(--long)' : role === 'short' ? 'var(--short)' : 'var(--faint)';
    const d = s.pts.map((p, i) => `${i ? 'L' : 'M'}${x(p.t).toFixed(1)},${y(p.v).toFixed(1)}`).join('');
    const last = s.pts[s.pts.length - 1];
    return `<path d="${d}" fill="none" stroke="${col}" stroke-width="${role ? 2.2 : 1.2}" opacity="${role ? 1 : .8}"/>
      <text x="${x(last.t) + 6}" y="${y(last.v) + 4}" class="axis" style="fill:${role ? col : 'var(--muted)'};font-family:var(--ui);font-size:11.5px">${esc(venue(s.ex))}</text>`;
  }).join('');
  const zero = m.view !== 'price' ? `<line class="zero" x1="${L}" x2="${W - R}" y1="${y(0)}" y2="${y(0)}"/><text class="axis" x="${L + 2}" y="${y(0) - 5}">медиана</text>` : '';
  const ticks = m.view !== 'price' ? [hi, hi / 2, -hi / 2, lo].map(v => `<text class="axis" x="${W - R + 50}" y="${y(v) + 4}" text-anchor="end">${v > 0 ? '+' : ''}${v.toFixed(1)}</text>`).join('') : [hi, (hi + lo) / 2, lo].map(v => `<text class="axis" x="${W - 4}" y="${y(v) + 4}" text-anchor="end">${fmt.price(v)}</text>`).join('');
  return `<svg class="chart" viewBox="0 0 ${W} ${H}" role="img" aria-label="Цены ${esc(baseOf(sym))} по биржам">
    ${[0.25, 0.5, 0.75].map(f => `<line class="grid-line" x1="${L}" x2="${W - R}" y1="${T + f * (H - T - Bm)}" y2="${T + f * (H - T - Bm)}"/>`).join('')}
    ${zero}${lines}${ticks}
    <text class="axis" x="${L}" y="${H - 5}">−${fmt.dur((tMax - tMin) / 1000)}</text><text class="axis" x="${W - R}" y="${H - 5}" text-anchor="end">сейчас</text></svg>`;
}

function ticketPreview() {
  const m = S.mk; const q = quotesFor(m.sym || '');
  const l = q.find(r => r.ex === m.longEx), sh = q.find(r => r.ex === m.shortEx);
  if (!l || !sh || m.longEx === m.shortEx) return '<div class="mut small">Выберите две разные биржи с котировками.</div>';
  const la = num(l.bestAsk), sb = num(sh.bestBid);
  const gross = (sb - la) / la * 100;
  const fl = ArbFee(m.longEx), fs = ArbFee(m.shortEx);
  const feesRt = 2 * (fl + fs);
  const exitW = (num(l.bestAsk) - num(l.bestBid)) / num(l.bestBid) * 100 + (num(sh.bestAsk) - num(sh.bestBid)) / num(sh.bestBid) * 100;
  const net0 = gross - feesRt - exitW;
  const size = num(m.size) || 0; const lev = num(m.lev) || 1;
  const sig = opps().find(o => o.symbol === m.sym && o.longExchange === m.longEx && o.shortExchange === m.shortEx);
  return `<div class="tk-rows">
    <div class="row between"><span class="mut">Покупка (long) ${esc(venue(m.longEx))}</span><span class="num long">${fmt.price(la)}</span></div>
    <div class="row between"><span class="mut">Продажа (short) ${esc(venue(m.shortEx))}</span><span class="num short">${fmt.price(sb)}</span></div>
    <div class="row between"><span class="mut">Gross спред</span><span class="num ${cls(gross)}">${fmt.pct(gross)}</span></div>
    <div class="row between"><span class="mut">4 комиссии + выход по стакану</span><span class="num cost">−${fmt.pct(feesRt + exitW, 3, false)}</span></div>
    <div class="row between" style="white-space:nowrap"><strong>При схождении цен</strong><span class="num ${cls(net0)}" style="font-size:15px">${fmt.pct(net0)}</span></div>
    <div class="row between"><span class="mut">В долларах на ${fmt.usd(size, 0, false)}</span><span class="num ${cls(net0)}">${fmt.usd(size * net0 / 100)}</span></div>
    ${sig ? `<div class="row between"><span class="mut">С учётом fair basis</span><span class="num ${cls(sig.netRoundTripPercent)}">${fmt.pct(sig.netRoundTripPercent)}</span></div>` : ''}
    <div class="row between"><span class="mut">Маржа на каждую ногу</span><span class="num">${fmt.usd(size / lev, 2, false)}</span></div>
  </div>
  ${net0 <= 0 ? `<div class="callout" style="margin-top:12px">${ICON.warn}<div>Даже при полном схождении цен эта сделка убыточна после комиссий и выхода.</div></div>` : ''}`;
}
function ArbFee(ex) { const t = S.settings?.trading; return ({ BITGET: .06, KUCOIN: .06, BYBIT: .055 })[String(ex).toUpperCase()] ?? .05; }

function compareTable(sym) {
  const q = quotesFor(sym); const m = S.mk;
  if (!q.length) return '<div class="empty">Нет котировок</div>';
  const maxBid = Math.max(...q.map(r => num(r.bestBid))), minAsk = Math.min(...q.map(r => num(r.bestAsk)));
  return `<div class="tbl-wrap"><table class="tbl matrix"><thead><tr><th>Биржа</th><th class="r">Bid</th><th class="r">Ask</th><th class="r">Ширина</th><th class="r">Глубина bid / ask</th><th class="r">Возраст</th><th class="r">Нога</th></tr></thead><tbody>
  ${q.map(r => { const w = (num(r.bestAsk) - num(r.bestBid)) / num(r.bestBid) * 100; const age = (Date.now() - new Date(r.timestamp).getTime()) / 1000; return `<tr>
    <td>${esc(venue(r.ex))}</td><td class="r num ${num(r.bestBid) === maxBid ? 'best-bid' : ''}">${fmt.price(r.bestBid)}</td><td class="r num ${num(r.bestAsk) === minAsk ? 'best-ask' : ''}">${fmt.price(r.bestAsk)}</td>
    <td class="r num">${fmt.pct(w, 3, false)}</td><td class="r num mut">${fmt.usd(num(r.bestBid) * num(r.bidQuantity), 0, false)} / ${fmt.usd(num(r.bestAsk) * num(r.askQuantity), 0, false)}</td>
    <td class="r num ${age > 1.5 ? 'cost' : 'mut'}">${Number.isFinite(age) ? fmt.dur(Math.max(0, age)) : '—'}</td>
    <td class="r"><div class="row" style="justify-content:flex-end;gap:6px"><button class="btn sm ${m.longEx === r.ex ? 'leg-on-l' : 'ghost'}" data-mk-long="${esc(r.ex)}" aria-pressed="${m.longEx === r.ex}" aria-label="Long на ${esc(venue(r.ex))}">Long</button><button class="btn sm ${m.shortEx === r.ex ? 'leg-on-s' : 'ghost'}" data-mk-short="${esc(r.ex)}" aria-pressed="${m.shortEx === r.ex}" aria-label="Short на ${esc(venue(r.ex))}">Short</button></div></td></tr>`; }).join('')}
  </tbody></table></div>`;
}

function bookPanel(sym) {
  const m = S.mk; const q = quotesFor(sym);
  return `<div class="seg" role="group" aria-label="Биржа стакана" style="margin-bottom:12px;flex-wrap:wrap">${q.map(r => `<button data-mk-book="${esc(r.ex)}" aria-pressed="${m.bookEx === r.ex}">${esc(venue(r.ex))}</button>`).join('')}</div>
    ${m.bookEx ? ladder(sym, m.bookEx, m.bookEx === m.shortEx ? 'short' : 'long') : ''}`;
}

function renderTerminal() {
  const { list, q } = ensureMk(); const m = S.mk;
  if (!m.sym) return '<section class="panel"><div class="empty"><strong>Котировок пока нет</strong>Стаканы подключаются; обычно это занимает до минуты после старта.</div></section>';
  return `<div id="mkRoot">
  <div class="chips" id="mkChips"></div>
  <div class="grid cols-hero" style="margin-top:14px">
    <section class="panel">
      <div class="panel-head"><h2 id="mkTitle"></h2><span class="sub" id="mkBrush"></span>
        <div class="tools"><div class="seg" role="group" aria-label="Вид графика"><button data-mk-view="dev" aria-pressed="${m.view === 'dev'}">Отклонение, bps</button><button data-mk-view="price" aria-pressed="${m.view === 'price'}">Цена</button></div></div></div>
      <div class="panel-body" id="mkChart"></div>
    </section>
    <section class="panel ticket" id="mkTicket">
      <div class="panel-head"><h2>Ручной хедж</h2><span class="sub" id="mkMode"></span></div>
      <div class="panel-body stack" style="gap:14px">
        <div class="cols-2 grid" style="gap:10px">
          <div class="field"><label for="mkLong" class="long">Long на</label><select class="input" id="mkLong"></select></div>
          <div class="field"><label for="mkShort" class="short">Short на</label><select class="input" id="mkShort"></select></div>
        </div>
        <button class="btn sm ghost" data-act="mk-best" style="align-self:flex-start">Подставить лучший маршрут</button>
        <div class="cols-2 grid" style="gap:10px">
          <div class="field"><label for="mkSize">Размер ноги</label><div class="input-wrap"><input class="input num" id="mkSize" inputmode="decimal" value="${esc(m.size)}"><span class="unit">USDT</span></div></div>
          <div class="field"><label for="mkLev">Плечо (live)</label><div class="input-wrap"><input class="input num" id="mkLev" inputmode="decimal" value="${esc(m.lev)}"><span class="unit">x</span></div></div>
        </div>
        <div id="mkPreview"></div>
        <div id="mkWarn"></div>
        <button class="btn primary" data-act="mk-open" id="mkOpen" style="height:42px">Открыть хедж</button>
      </div>
    </section>
  </div>
  <div class="grid cols-3-2" style="margin-top:18px">
    <section class="panel"><div class="panel-head"><h2>Биржи</h2><span class="sub">выберите ноги прямо в таблице</span></div><div class="panel-body flush" id="mkCompare"></div></section>
    <section class="panel"><div class="panel-head"><h2>Стакан</h2></div><div class="panel-body" id="mkBook"></div></section>
  </div></div>`;
}

function updateTerminal() {
  const root = $('#mkRoot'); if (!root) return;
  const { list, q } = ensureMk(); const m = S.mk; const md = modeOf();
  $('#mkChips').innerHTML = list.slice(0, 40).map(x => `<button class="chip ${x.sym === m.sym ? 'on' : ''}" data-mk-sym="${esc(x.sym)}"><span>${esc(baseOf(x.sym))}</span><span class="num ${cls(x.cross)}">${x.cross == null ? '—' : fmt.pct(x.cross, 2)}</span></button>`).join('');
  $('#mkTitle').innerHTML = `${esc(baseOf(m.sym))} <span class="mut" style="font-weight:500">по всем биржам</span>`;
  const b = brushScore(m.sym);
  $('#mkBrush').innerHTML = b ? `Ёршик ${b.score}/100 · ${esc(b.note)}${b.rangePct ? ' · диапазон ' + fmt.pct(b.rangePct, 2, false) : ''}` : '';
  $('#mkChart').innerHTML = priceChart(m.sym);
  $('#mkCompare').innerHTML = compareTable(m.sym);
  $('#mkBook').innerHTML = bookPanel(m.sym);
  const key = m.sym + '|' + q.map(r => r.ex).join(',');
  if (key !== m.venuesKey) {
    m.venuesKey = key;
    const opt = (sel) => q.map(r => `<option value="${esc(r.ex)}" ${r.ex === sel ? 'selected' : ''}>${esc(venue(r.ex))}</option>`).join('');
    $('#mkLong').innerHTML = opt(m.longEx); $('#mkShort').innerHTML = opt(m.shortEx);
  } else {
    if ($('#mkLong').value !== m.longEx && document.activeElement !== $('#mkLong')) $('#mkLong').value = m.longEx;
    if ($('#mkShort').value !== m.shortEx && document.activeElement !== $('#mkShort')) $('#mkShort').value = m.shortEx;
  }
  $('#mkPreview').innerHTML = ticketPreview();
  const live = md.key === 'live', ro = md.key === 'readonly', killed = md.key === 'killed';
  $('#mkMode').innerHTML = live ? '<span class="badge neg">реальные деньги</span>' : `<span class="badge long">paper</span>`;
  $('#mkWarn').innerHTML = live ? `<div class="callout bad">${ICON.warn}<div>Live включён: хедж откроется реальными ордерами на ${esc(venue(m.longEx))} и ${esc(venue(m.shortEx))}.</div></div>`
    : (ro || killed) ? `<div class="callout info">${ICON.info}<div>${killed ? 'Kill switch активен, live-ордера запрещены.' : 'Live в режиме чтения.'} Хедж откроется в paper.</div></div>` : '';
  const btn = $('#mkOpen'); btn.className = 'btn ' + (live ? 'solid-danger' : 'primary'); btn.textContent = live ? 'Открыть хедж реальными ордерами' : 'Открыть paper-хедж';
  btn.disabled = !m.longEx || !m.shortEx || m.longEx === m.shortEx || !(num(m.size) > 0);
}

// Scanner --------------------------------------------------------
function renderScanner() {
  const books = snap().bookTickers || {};
  let rows = Object.entries(books).map(([sym, byEx]) => {
    const q = Object.entries(byEx).map(([ex, t]) => ({ ex, ...lc(t) })).filter(r => num(r.bestBid) > 0 && num(r.bestAsk) > 0);
    if (!q.length) return null;
    const mids = q.map(r => (num(r.bestBid) + num(r.bestAsk)) / 2);
    const lo = Math.min(...mids), hi = Math.max(...mids);
    const bestBid = q.reduce((a, b) => num(b.bestBid) > num(a.bestBid) ? b : a);
    const bestAsk = q.reduce((a, b) => num(b.bestAsk) < num(a.bestAsk) ? b : a);
    const cross = (num(bestBid.bestBid) - num(bestAsk.bestAsk)) / num(bestAsk.bestAsk) * 100;
    const widths = q.map(r => (num(r.bestAsk) - num(r.bestBid)) / num(r.bestBid) * 100);
    const execN = opps().filter(o => o.symbol === sym && o.isExecutable).length;
    return { sym, venues: q.length, disp: (hi - lo) / lo * 100, cross, bestBid: bestBid.ex, bestAsk: bestAsk.ex, avgW: widths.reduce((a, b) => a + b, 0) / widths.length, execN };
  }).filter(Boolean);
  if (S.scanner.q) rows = rows.filter(r => r.sym.toLowerCase().includes(S.scanner.q.toLowerCase()));
  rows.sort((a, b) => b.cross - a.cross);
  return `
  <div class="toolbar">
    <div class="search">${ICON.search}<input class="input" id="sq" placeholder="Символ" value="${esc(S.scanner.q)}" aria-label="Фильтр символов"></div>
    <span class="grow"></span>
    <span class="mut small">${rows.length} символов в universe · источник ${esc(snap().discoverySource || 'конфиг')}</span>
  </div>
  <div class="callout info" style="margin-bottom:16px">${ICON.info}<div>Сканер показывает сырой рынок до гейтов. Положительный кросс означает, что bid одной биржи выше ask другой. Сделкой это станет, только если кросс превысит fair basis и все издержки.</div></div>
  <section class="panel"><div class="panel-body flush">${rows.length ? `<div class="tbl-wrap"><table class="tbl"><thead><tr><th>Символ</th><th class="r">Бирж</th><th class="r">Разброс mid</th><th class="r">Лучший кросс</th><th>Дешевле всего</th><th>Дороже всего</th><th class="r">Средняя ширина</th><th>Сигналы</th></tr></thead><tbody>
    ${rows.map(r => `<tr><td class="sym">${esc(baseOf(r.sym))}</td><td class="r num">${r.venues}</td><td class="r num">${fmt.pct(r.disp, 3, false)}</td><td class="r num ${cls(r.cross)}">${fmt.pct(r.cross)}</td><td class="long">${esc(venue(r.bestAsk))}</td><td class="short">${esc(venue(r.bestBid))}</td><td class="r num ${r.avgW > 0.22 ? 'cost' : 'mut'}">${fmt.pct(r.avgW, 3, false)}</td><td>${r.execN ? `<span class="badge pos">${r.execN} готов</span>` : '<span class="mut">—</span>'}</td></tr>`).join('')}
  </tbody></table></div>` : '<div class="empty"><strong>Котировок пока нет</strong>Стаканы подключаются; обычно это занимает до минуты после старта.</div>'}</div></section>`;
}

// Positions & trades ---------------------------------------------
function renderPositions() {
  const p = fp();
  const pos = (p.positions || []).map(lc);
  const live = (snap().livePositions?.open || []).map(lc);
  const trades = S.trades.src === 'all' && S.ledger ? S.ledger.filter(t => t.status && t.status !== 'Open') : (p.trades || []).map(lc).filter(t => !t.isOpen);
  return `
  <section class="panel">
    <div class="panel-head"><h2>Paper-хеджи</h2><span class="sub">${pos.length} открыто · плечо ${esc(p.leverage ?? '—')}x</span>
      <div class="tools">${pos.length ? '<button class="btn sm danger" data-act="paper-close-all">Закрыть все</button>' : ''}</div></div>
    <div class="panel-body flush">${pos.length ? `<div class="tbl-wrap"><table class="tbl"><thead><tr><th>Символ</th><th>Маршрут</th><th class="r">Кол-во</th><th class="r">Вход L / S</th><th class="r">Метка L / S</th><th class="r">Ширина: вход → сейчас</th><th class="r">Держим</th><th class="r">P&amp;L</th><th></th></tr></thead><tbody>
      ${pos.map(x => `<tr><td class="sym">${esc(baseOf(x.symbol))}</td><td>${routeHtml(x.longExchange, x.shortExchange)}</td><td class="r num">${fmt.qty(x.baseQty)}</td><td class="r num">${fmt.price(x.longEntry)} / ${fmt.price(x.shortEntry)}</td><td class="r num mut">${fmt.price(x.markLongBid)} / ${fmt.price(x.markShortAsk)}</td><td class="r num">${fmt.pct(x.entryWidthPercent, 3, false)} → ${fmt.pct(x.currentWidthPercent, 3, false)}</td><td class="r num">${fmt.dur(x.holdSeconds)}</td><td class="r">${signed(x.unrealizedPnlUsd)}</td><td class="r"><button class="btn sm" data-act="paper-close" data-id="${esc(x.tradeId)}" data-sym="${esc(x.symbol)}">Закрыть</button></td></tr>`).join('')}
    </tbody></table></div>` : '<div class="empty"><strong>Открытых paper-хеджей нет</strong>Бот откроет хедж, когда маршрут пройдёт все гейты и симуляция исполнения подтвердит заполнение обеих ног.</div>'}</div>
  </section>

  ${live.length ? `<section class="panel" style="margin-top:18px">
    <div class="panel-head"><h2>Live-хеджи</h2><span class="sub">реальные позиции из журнала бота</span></div>
    <div class="panel-body flush"><div class="tbl-wrap"><table class="tbl"><thead><tr><th>Символ</th><th>Маршрут</th><th class="r">Кол-во</th><th class="r">Notional</th><th class="r">Вход L / S</th><th>Статус</th><th class="r">P&amp;L</th><th></th></tr></thead><tbody>
      ${live.map(x => `<tr><td class="sym">${esc(baseOf(x.symbol))}</td><td>${routeHtml(x.longExchange, x.shortExchange)}</td><td class="r num">${fmt.qty(x.baseQty)}</td><td class="r num">${fmt.usd(x.notionalUsd, 0, false)}</td><td class="r num">${fmt.price(x.longEntry)} / ${fmt.price(x.shortEntry)}</td><td><span class="badge ${x.status === 'Open' ? 'long' : 'cost'}">${esc(x.status)}</span></td><td class="r">${signed(x.totalUnrealizedPnlUsd ?? x.unrealizedPricePnlUsd)}</td><td class="r"><button class="btn sm danger" data-act="live-close" data-id="${esc(x.tradeId)}" data-sym="${esc(x.symbol)}">Закрыть на бирже</button></td></tr>`).join('')}
    </tbody></table></div></div></section>` : ''}

  <section class="panel" style="margin-top:18px">
    <div class="panel-head"><h2>Журнал сделок</h2>
      <div class="tools"><div class="seg" role="group" aria-label="Источник">
        <button data-tsrc="recent" aria-pressed="${S.trades.src === 'recent'}">Последние 40</button>
        <button data-tsrc="all" aria-pressed="${S.trades.src === 'all'}">Вся история</button></div></div></div>
    <div class="panel-body flush">${S.trades.src === 'all' && !S.ledger ? '<div class="empty">Загружаем журнал…</div>' : tradesTable(trades.slice(0, 400))}</div>
  </section>`;
}

// Analytics -------------------------------------------------------
function holdBuckets(ledger) {
  const B = [[0, 1, '≤ 1 с'], [1, 5, '1–5 с'], [5, 30, '5–30 с'], [30, 120, '30 с – 2 мин'], [120, 600, '2–10 мин'], [600, 1e12, '> 10 мин']];
  const out = B.map(([a, b, l]) => ({ l, a, b, n: 0, pnl: 0 }));
  for (const t of ledger) {
    if (!t.status || t.status === 'Open') continue;
    const pnl = num(t.realizedPnlUsd); if (pnl == null) continue;
    let sec = num(t.holdMin) != null ? num(t.holdMin) * 60 : null;
    if (sec == null && t.openedAt && t.closedAt) sec = (new Date(t.closedAt) - new Date(t.openedAt)) / 1000;
    if (sec == null) continue;
    const b = out.find(x => sec >= x.a && sec < x.b); if (b) { b.n++; b.pnl += pnl; }
  }
  return out;
}

function equityChart(curve) {
  const pts = (curve || []).map(lc).map(p => ({ t: new Date(p.t).getTime(), v: num(p.equity) })).filter(p => Number.isFinite(p.t) && p.v != null);
  if (pts.length < 2) return '<div class="empty"><strong>Мало закрытых сделок</strong>Кривая появится после двух закрытых хеджей в выбранном периоде.</div>';
  const W = 900, H = 240, L = 54, R = 12, T = 12, Bm = 26;
  let lo = Math.min(...pts.map(p => p.v)), hi = Math.max(...pts.map(p => p.v)); if (hi - lo < 1e-6) { hi += 1; lo -= 1; }
  const pad = (hi - lo) * 0.08; lo -= pad; hi += pad;
  const t0 = pts[0].t, t1 = pts[pts.length - 1].t || t0 + 1;
  const x = (t) => L + (t - t0) / Math.max(1, t1 - t0) * (W - L - R);
  const y = (v) => T + (1 - (v - lo) / (hi - lo)) * (H - T - Bm);
  const d = pts.map((p, i) => `${i ? 'L' : 'M'}${x(p.t).toFixed(1)},${y(p.v).toFixed(1)}`).join('');
  const area = d + `L${x(t1).toFixed(1)},${H - Bm}L${L},${H - Bm}Z`;
  const ticks = [0, .25, .5, .75, 1].map(f => lo + (hi - lo) * f);
  const days = [0, .5, 1].map(f => t0 + (t1 - t0) * f);
  return `<svg class="chart" viewBox="0 0 ${W} ${H}" data-eq='${esc(JSON.stringify(pts.map(p => [p.t, +p.v.toFixed(2)])))}' role="img" aria-label="Кривая капитала">
    <defs><linearGradient id="eqfill" x1="0" x2="0" y1="0" y2="1"><stop offset="0" stop-color="#4fb3ff" stop-opacity=".28"/><stop offset="1" stop-color="#4fb3ff" stop-opacity="0"/></linearGradient></defs>
    ${ticks.map(v => `<line class="grid-line" x1="${L}" x2="${W - R}" y1="${y(v)}" y2="${y(v)}"/><text class="axis" x="${L - 8}" y="${y(v) + 4}" text-anchor="end">${v.toFixed(0)}</text>`).join('')}
    ${days.map((t, i) => `<text class="axis" x="${x(t)}" y="${H - 6}" text-anchor="${i === 0 ? 'start' : i === days.length - 1 ? 'end' : 'middle'}">${new Date(t).toLocaleDateString('ru-RU', { day: '2-digit', month: '2-digit' })}</text>`).join('')}
    <path class="area" d="${area}"/><path class="curve" d="${d}"/>
    <line id="eqx" x1="0" x2="0" y1="${T}" y2="${H - Bm}" stroke="var(--text-2)" stroke-dasharray="3 3" opacity="0"/>
  </svg>`;
}

function dailyBars(daily) {
  const rows = (daily || []).map(lc).filter(d => d.hasActivity !== false).slice(-30);
  if (!rows.length) return '<div class="empty">Нет дней с активностью</div>';
  const W = 600, H = 200, T = 10, Bm = 22;
  const mx = Math.max(0.01, ...rows.map(r => Math.abs(num(r.pnl) || 0)));
  const bw = (W - 20) / rows.length;
  const mid = T + (H - T - Bm) / 2;
  return `<svg class="chart" viewBox="0 0 ${W} ${H}" role="img" aria-label="P&L по дням">
    <line class="zero" x1="0" x2="${W}" y1="${mid}" y2="${mid}"/>
    ${rows.map((r, i) => { const v = num(r.pnl) || 0; const h = Math.abs(v) / mx * (H - T - Bm) / 2; const xx = 10 + i * bw + bw * 0.15; return `<rect class="${v >= 0 ? 'bar-pos' : 'bar-neg'}" x="${xx}" y="${v >= 0 ? mid - h : mid}" width="${bw * 0.7}" height="${Math.max(1, h)}" rx="2"><title>${esc(r.day)}: ${fmt.usd(v)} · ${fmt.int(r.trades)} сделок</title></rect>`; }).join('')}
    ${rows.length > 1 ? `<text class="axis" x="10" y="${H - 5}">${esc(rows[0].day)}</text><text class="axis" x="${W - 10}" y="${H - 5}" text-anchor="end">${esc(rows[rows.length - 1].day)}</text>` : ''}
  </svg>`;
}

function renderAnalytics() {
  const r = S.perf ? lc(S.perf) : null;
  const an = lc(snap().paperAnalytics || {});
  const skips = (an.skipReasons || []).map(lc).slice(0, 10);
  const sTotal = Math.max(1, ...skips.map(s => s.count));
  const buckets = S.ledger ? holdBuckets(S.ledger) : null;
  let phantom = null;
  if (buckets) {
    const fast = buckets.filter(b => b.b <= 30).reduce((a, b) => a + b.pnl, 0);
    const total = buckets.reduce((a, b) => a + b.pnl, 0);
    if (fast > 0 && (total <= 0 || fast / total > 0.6)) phantom = { fast, total };
  }
  const periods = [[1, 'День'], [7, '7 дней'], [30, '30 дней'], [0, 'Вся история']];
  return `
  <div class="toolbar"><div class="seg" role="group" aria-label="Период">${periods.map(([d, l]) => `<button data-days="${d}" aria-pressed="${S.perfDays === d}">${l}</button>`).join('')}</div><span class="grow"></span>
    <button class="btn sm ghost" data-act="perf-refresh">${ICON.refresh}Обновить</button></div>
  ${!r ? '<section class="panel"><div class="empty">Загружаем отчёт…</div></section>' : `
  <section class="panel"><div class="kpis">
    <div class="kpi"><div class="k">Чистый P&amp;L</div><div class="v ${cls(r.netPnl)}">${fmt.usd(r.netPnl)}</div><div class="d">${fmt.pct(r.netPctOfEquity, 2)} к капиталу</div></div>
    <div class="kpi"><div class="k">Сделок</div><div class="v">${fmt.int(r.totalTrades)}</div><div class="d">${fmt.int(r.wins)} в плюс · ${fmt.int(r.losses)} в минус</div></div>
    <div class="kpi"><div class="k">Win rate</div><div class="v">${num(r.winRate) != null ? num(r.winRate).toFixed(1) + '%' : '—'}</div><div class="d">PF ${num(r.profitFactor) != null ? num(r.profitFactor).toFixed(2) : '—'}</div></div>
    <div class="kpi"><div class="k">Ожидание на сделку</div><div class="v ${cls(r.expectancy)}">${fmt.usd(r.expectancy, 3)}</div><div class="d">${fmt.pct(r.expectancyPctOfSize, 3)} от размера</div></div>
    <div class="kpi"><div class="k">Макс. просадка</div><div class="v neg">${fmt.usd(-Math.abs(num(r.maxDrawdown) || 0))}</div><div class="d">${fmt.pct(-Math.abs(num(r.maxDrawdownPct) || 0), 2)}</div></div>
    <div class="kpi"><div class="k">Комиссии</div><div class="v cost">${fmt.usd(-Math.abs(num(r.totalFees) || 0))}</div><div class="d">${fmt.pct(r.feesPctOfGross, 1, false)} от gross</div></div>
  </div></section>
  <section class="panel" style="margin-top:18px"><div class="panel-head"><h2>Кривая капитала</h2><span class="sub">${esc(r.fromUtc || '')} — ${esc(r.toUtc || '')}</span></div><div class="panel-body">${equityChart(r.equityCurve)}</div></section>
  <div class="grid cols-2" style="margin-top:18px">
    <section class="panel"><div class="panel-head"><h2>P&amp;L по дням</h2></div><div class="panel-body">${dailyBars(r.daily)}</div></section>
    <section class="panel"><div class="panel-head"><h2>Причины пропуска за день</h2></div><div class="panel-body">${skips.length ? `<div class="bars">${skips.map(s => `<div class="bar-row"><span class="t" title="${esc(s.reason)}">${esc(s.reason)}</span><div class="track"><div class="fill" style="width:${s.count / sTotal * 100}%"></div></div><span class="c">${fmt.int(s.count)}</span></div>`).join('')}</div>` : '<div class="empty">Пропусков нет</div>'}</div></section>
  </div>`}
  <section class="panel" style="margin-top:18px">
    <div class="panel-head"><h2>P&amp;L по времени удержания</h2><span class="sub">проверка на фантомную прибыль</span></div>
    <div class="panel-body stack">
      ${phantom ? `<div class="callout bad">${ICON.warn}<div><strong>Сделки короче 30 секунд принесли ${fmt.usd(phantom.fast)} при общем результате ${fmt.usd(phantom.total)}.</strong> Такие «схождения» почти всегда — устаревшая котировка одной биржи, и в live их не будет. Доверяйте только результату с включённой симуляцией задержки.</div></div>` : ''}
      ${buckets ? `<div class="buckets">${buckets.map(b => `<div class="bucket"><div class="k">${esc(b.l)}</div><div class="v ${cls(b.pnl)}">${fmt.usd(b.pnl)}</div><div class="n">${fmt.int(b.n)} сделок</div></div>`).join('')}</div>` : '<div class="empty">Загружаем журнал…</div>'}
    </div>
  </section>`;
}

// Funding -------------------------------------------------------
function renderFunding() {
  const rows = (S.funding || []).map(lc);
  return `
  <div class="callout info" style="margin-bottom:16px">${ICON.info}<div>Funding начисляется только в моменты расчёта. Бот учитывает расчёты, которые попадают в горизонт удержания (${esc(S.settings?.trading?.fundingHorizonMinutes ?? 20)} мин), по текущей ставке каждой биржи. Положительный funding не открывает сделку сам по себе.</div></div>
  <section class="panel"><div class="panel-head"><h2>Лучшая разница ставок по символам</h2><div class="tools"><button class="btn sm ghost" data-act="funding-refresh">${ICON.refresh}Обновить</button></div></div>
  <div class="panel-body flush">${rows.length ? `<div class="tbl-wrap"><table class="tbl"><thead><tr><th>Символ</th><th>Long / Short</th><th class="r">Ставка long</th><th class="r">Ставка short</th><th class="r">Дельта за период</th><th class="r">Годовых</th><th class="r">Интервал</th><th class="r">До расчёта</th><th>Тренд</th></tr></thead><tbody>
    ${rows.map(r => { const next = new Date(r.nextFundingUtc).getTime(); const left = (next - Date.now()) / 1000; const ann = (num(r.deltaRate) || 0) * (24 / (num(r.intervalHours) || 8)) * 365 * 100; return `<tr><td class="sym">${esc(baseOf(r.symbol))}</td><td>${routeHtml(r.longExchange, r.shortExchange)}</td><td class="r num">${fmt.pct((num(r.longRate) || 0) * 100, 4)}</td><td class="r num">${fmt.pct((num(r.shortRate) || 0) * 100, 4)}</td><td class="r num ${cls(r.deltaRate)}">${fmt.pct((num(r.deltaRate) || 0) * 100, 4)}</td><td class="r num ${cls(ann)}">${fmt.pct(ann, 1)}</td><td class="r num mut">${esc(r.intervalHours)} ч</td><td class="r num">${Number.isFinite(left) && left > 0 ? fmt.dur(left) : '—'}</td><td>${r.trend === 'expanding' ? '<span class="badge pos">расширяется</span>' : r.trend === 'converging' ? '<span class="badge cost">сходится</span>' : '<span class="mut">—</span>'}</td></tr>`; }).join('')}
  </tbody></table></div>` : '<div class="empty"><strong>Ставок пока нет</strong>Сервис funding опрашивает биржи раз в 5 минут.</div>'}</div></section>`;
}


// Balances ---------------------------------------------------------
function usdtFree(e) {
  const b = (e.balances || []).map(lc).filter(x => /^(USDT|USD)$/i.test(x.asset));
  return b.length ? b.reduce((a, x) => a + (num(x.available) || 0), 0) : null;
}
function posList(e) { return Array.isArray(e.positions) ? e.positions.map(lc).filter(p => num(p.quantity)) : []; }
function botOwner(ex, sym) {
  const live = (snap().livePositions?.open || []).map(lc);
  if (live.some(h => h.symbol === sym && (h.longExchange === ex || h.shortExchange === ex))) return '<span class="badge long">хедж бота</span>';
  if ((S.scalp?.open || []).map(lc).some(p => p.live && p.symbol === sym && p.exchange === ex)) return '<span class="badge cost">скальп бота</span>';
  return '<span class="badge">не от бота</span>';
}

function renderBalances() {
  const p = fp();
  const mb = p.marginBreakdown || {};
  const venues = Object.entries(mb).map(([ex, v]) => ({ ex, ...lc(v) }));
  const tot = venues.reduce((a, v) => ({ s: a.s + (num(v.starting) || 0), f: a.f + (num(v.free) || 0), l: a.l + (num(v.locked) || 0), e: a.e + (num(v.equity) || 0) }), { s: 0, f: 0, l: 0, e: 0 });
  const sc = S.scalp ? lc(S.scalp) : null; const w = sc ? lc(sc.wallet || {}) : null;
  const bal = S.liveBalances ? lc(S.liveBalances) : null;
  const exs = (bal?.exchanges || []).map(lc);
  const allPos = exs.flatMap(e => posList(e).map(x => ({ ...x, ex: e.exchange })));
  const uPnl = allPos.reduce((a, x) => a + (num(x.unrealizedPnl) || 0), 0);
  const freeTot = exs.reduce((a, e) => a + (e.ok ? usdtFree(e) || 0 : 0), 0);

  return `
  <section class="panel">
    <div class="panel-head"><h2>Demo-кошелёк арбитража</h2><span class="sub">виртуальная маржа paper на каждой бирже</span></div>
    <div class="kpis" style="border-top:1px solid var(--line-soft);margin-top:12px">
      <div class="kpi"><div class="k">Капитал</div><div class="v">${fmt.usd(tot.e, 2, false)}</div><div class="d">старт ${fmt.usd(tot.s, 0, false)}</div></div>
      <div class="kpi"><div class="k">Свободно</div><div class="v">${fmt.usd(tot.f, 2, false)}</div></div>
      <div class="kpi"><div class="k">В открытых хеджах</div><div class="v">${fmt.usd(tot.l, 2, false)}</div><div class="d">${fmt.int(p.openCount)} хеджей</div></div>
      <div class="kpi"><div class="k">Изменение</div><div class="v ${cls(tot.e - tot.s)}">${fmt.usd(tot.e - tot.s)}</div><div class="d">уPnL ${fmt.usd(p.unrealizedPnlUsd)}</div></div>
    </div>
    <div class="panel-body flush">${venues.length ? `<div class="tbl-wrap"><table class="tbl"><thead><tr><th>Биржа</th><th class="r">Старт</th><th class="r">Свободно</th><th class="r">В хеджах</th><th class="r">Капитал</th><th class="r">Изменение</th><th style="width:28%">Загрузка маржи</th></tr></thead><tbody>
      ${venues.map(v => { const used = (num(v.equity) || 0) > 0 ? (num(v.locked) || 0) / num(v.equity) * 100 : 0; return `<tr><td>${esc(venue(v.ex))}</td><td class="r num mut">${fmt.usd(v.starting, 2, false)}</td><td class="r num">${fmt.usd(v.free, 2, false)}</td><td class="r num">${fmt.usd(v.locked, 2, false)}</td><td class="r num">${fmt.usd(v.equity, 2, false)}</td><td class="r">${signed(v.deltaFromStart)}</td><td><div class="row" style="gap:8px"><div class="track" style="flex:1;height:8px;background:var(--bg-deep);border-radius:99px;overflow:hidden"><div style="height:100%;width:${Math.min(100, used).toFixed(1)}%;background:${used > 70 ? 'var(--cost)' : 'var(--long)'}"></div></div><span class="num mut small" style="width:42px;text-align:right">${used.toFixed(0)}%</span></div></td></tr>`; }).join('')}
    </tbody></table></div>` : '<div class="empty"><strong>Paper-кошелёк ещё не инициализирован</strong>Он появится после первого скана.</div>'}</div>
  </section>

  <section class="panel" style="margin-top:18px">
    <div class="panel-head"><h2>Кошелёк скальпера</h2><span class="sub">отдельный депозит, не пересекается с арбитражем</span><div class="tools"><button class="btn sm ghost" data-go="scalp">Открыть скальпер</button></div></div>
    ${w ? `<div class="kpis" style="border-top:1px solid var(--line-soft);margin-top:12px">
      <div class="kpi"><div class="k">Депозит</div><div class="v">${fmt.usd(w.depositUsd, 2, false)}</div><div class="d">плечо ${esc(sc.settings?.leverage ?? '—')}x</div></div>
      <div class="kpi"><div class="k">Paper-капитал</div><div class="v">${fmt.usd(w.paper?.equityUsd ?? w.paperEquityUsd, 2, false)}</div><div class="d">реализовано ${fmt.usd(w.paper?.realizedUsd ?? w.paperRealizedUsd)}</div></div>
      <div class="kpi"><div class="k">Маржа в позициях</div><div class="v">${fmt.usd(w.marginUsedUsd, 2, false)}</div><div class="d">${fmt.int(sc.openCount)} открыто</div></div>
      <div class="kpi"><div class="k">Live-капитал</div><div class="v ${cls(w.live?.realizedUsd ?? w.liveRealizedUsd)}">${fmt.usd(w.live?.equityUsd ?? w.depositUsd, 2, false)}</div><div class="d">реализовано ${fmt.usd(w.live?.realizedUsd ?? w.liveRealizedUsd)} · ${sc.live ? 'реальные ордера' : 'live выключен'}</div></div>
    </div>` : '<div class="panel-body"><div class="mut">Загружаем…</div></div>'}
  </section>

  <section class="panel" style="margin-top:18px">
    <div class="panel-head"><h2>Реальные счета на биржах</h2><span class="sub">${bal ? 'обновлено ' + fmt.ago(bal.utc) : 'по API-ключам из настроек'}</span>
      <div class="tools"><button class="btn sm ghost" data-act="live-balances" ${S.liveBalancesLoading ? 'disabled' : ''}>${ICON.refresh}${S.liveBalancesLoading ? 'Запрос…' : 'Обновить'}</button></div></div>
    ${!bal ? `<div class="panel-body"><div class="empty">${S.liveBalancesLoading ? 'Запрашиваем биржи…' : 'Нажмите «Обновить»'}</div></div>` : `
    <div class="kpis" style="border-top:1px solid var(--line-soft);margin-top:12px">
      <div class="kpi"><div class="k">USDT всего</div><div class="v">${fmt.usd(bal.totalUsdtApprox, 2, false)}</div><div class="d">по биржам с ключами</div></div>
      <div class="kpi"><div class="k">Свободно</div><div class="v">${fmt.usd(freeTot, 2, false)}</div></div>
      <div class="kpi"><div class="k">Открытых позиций</div><div class="v">${fmt.int(allPos.length)}</div><div class="d">на всех биржах</div></div>
      <div class="kpi"><div class="k">Нереализовано</div><div class="v ${cls(uPnl)}">${fmt.usd(uPnl)}</div></div>
    </div>
    <div class="panel-body"><div class="acct-grid">
      ${exs.map(e => { const ps = posList(e); const posErr = e.positions && !Array.isArray(e.positions) ? lc(e.positions).error : null; return `<div class="acct ${e.ok ? '' : e.hasKey === false ? 'off' : 'bad'}">
        <div class="acct-h"><span class="led ${e.ok ? 'ok' : e.hasKey === false ? '' : 'bad'}"></span><strong>${esc(venue(e.exchange))}</strong>
          ${e.ok ? `<span class="badge ${e.tradePermission ? 'neg' : ''}">${e.tradePermission ? 'торговля' : 'чтение'}</span>${e.accountMode ? `<span class="badge">${esc(e.accountMode)}</span>` : ''}` : ''}</div>
        ${e.ok ? `<div class="acct-n"><div><span class="mut small">USDT</span><div class="num big">${fmt.usd(e.usdtTotal, 2, false)}</div></div><div><span class="mut small">свободно</span><div class="num">${fmt.usd(usdtFree(e), 2, false)}</div></div></div>
          ${ps.length ? `<div class="tbl-wrap"><table class="tbl mini"><thead><tr><th>Позиция</th><th>Сторона</th><th class="r">Кол-во</th><th class="r">Вход</th><th class="r">uPnL</th></tr></thead><tbody>${ps.map(x => `<tr><td><div class="sym">${esc(baseOf(x.symbol))}</div><div style="margin-top:3px">${botOwner(e.exchange, x.symbol)}</div></td><td>${/sell|short/i.test(x.side || '') ? '<span class="short">short</span>' : '<span class="long">long</span>'}${num(x.leverage) ? ` <span class="mut small">${esc(num(x.leverage))}x</span>` : ''}</td><td class="r num">${fmt.qty(Math.abs(num(x.quantity)))}</td><td class="r num">${fmt.price(x.entryPrice)}</td><td class="r">${signed(x.unrealizedPnl)}</td></tr>`).join('')}</tbody></table></div>`
            : `<div class="mut small">${posErr ? 'Позиции: ' + esc(posErr) : 'Открытых позиций нет'}</div>`}`
          : `<div class="mut small">${e.hasKey === false ? 'Ключ не подключён. Добавьте его в «Настройки → Ключи бирж».' : esc(e.error || 'Ошибка запроса')}${e.hint ? '<br>' + esc(e.hint) : ''}</div>`}
      </div>`; }).join('')}
    </div>
    ${allPos.some(x => !/хедж|скальп/.test(botOwner(x.ex, x.symbol))) ? `<div class="callout" style="margin-top:14px">${ICON.warn}<div>На биржах есть позиции, которых нет в журнале бота. Если вы их не открывали вручную, сверьте их до включения live-ордеров.</div></div>` : ''}
    </div>`}
  </section>`;
}

// Scalper ----------------------------------------------------------
function strategySwitch(cur) {
  const opt = (k, t) => `<button type="button" class="chip ${cur === k ? 'on' : ''}" aria-pressed="${cur === k}" data-act="scalp-strategy" data-strategy="${k}">${t}</button>`;
  return `<div class="chips" role="group" aria-label="Стратегия скальпера">${opt('lag', 'Отставание бирж')}${opt('density', 'Плотности в стакане')}</div>`;
}

function renderDensity(sc) {
  const d = lc(S.density || {}); const sx = lc(d.stats || {}); const t = S.settings?.trading || {};
  const isLive = d.mode === 'live'; const stt = { ...lc(sx.current || {}), placed: sx.placed, filled: sx.filled, cancelled: sx.cancelled, cancelReasons: sx.cancelReasons }; const oth = lc(sx.other || {});
  const walls = (d.walls || []).map(lc); const closed = (d.closed || []).map(lc);
  const pos = d.position ? lc(d.position) : null; const rest = d.resting ? lc(d.resting) : null;
  const win = stt.trades ? Math.round(stt.wins / stt.trades * 100) : null;
  const fillRate = stt.placed ? Math.round(stt.filled / stt.placed * 100) : null;
  const why = { 'take-profit': ['тейк', 'pos'], 'take-profit-market': ['тейк по рынку', 'pos'], stop: ['стоп за плотностью', 'neg'], 'exchange-stop': ['стоп на бирже', 'neg'], 'wall-gone': ['плотность ушла', 'neg'], time: ['по времени', ''], 'no-book': ['нет стакана', 'neg'], 'no-stop': ['нет стопа — закрыли', 'neg'] };
  const liveHint = !d.liveRequested ? '' : !d.binanceKey ? 'Реальные ордера запрошены, но нет ключа Binance с правом trade.' : !d.liveOrdersOn ? 'Реальные ордера запрошены, но live-ордера выключены на странице Live.' : '';
  return `
  <section class="panel">
    <div class="panel-body row wrap" style="gap:18px;align-items:center">
      <div style="flex:1;min-width:min(260px,100%)"><div class="row" style="gap:8px 10px;flex-wrap:wrap"><h2 style="margin:0;font-size:20px;letter-spacing:-.02em">Скальпер</h2><span class="badge ${!sc.enabled ? '' : isLive ? 'neg' : 'long'}">${!sc.enabled ? 'выключен' : isLive ? 'реальные ордера' : 'paper'}</span><span class="badge">биржи: ${esc((d.venues || []).map(venue).join(', '))}</span>${liveHint ? `<span class="badge cost">${esc(liveHint)}</span>` : ''}</div>
        <div style="margin-top:10px">${strategySwitch('density')}</div>
        <p class="prose" style="margin:10px 0 0">Ищет крупную заявку маркет-мейкера в стакане, которая стоит не меньше ${esc(t.densityMinWallAgeSeconds ?? 20)} с. Когда цена подходит, ставит лимитку на тик перед ней. Плотность — причина отскока и защита стопа: если её съели или сняли, бот выходит. Тейк — небольшое движение от плотности.</p></div>
      <button class="btn ${sc.enabled ? '' : 'primary'}" data-act="scalp-toggle" style="height:40px">${sc.enabled ? ICON.pause + 'Выключить' : ICON.play + 'Включить скальпер'}</button>
    </div>
    <div class="kpis" style="border-top:1px solid var(--line)">
      <div class="kpi"><div class="k">${isLive ? 'Реальный результат' : 'Paper-результат'}</div><div class="v ${cls(stt.realizedUsd)}">${fmt.usd(stt.realizedUsd)}</div><div class="d">комиссии ${fmt.usd(stt.feesUsd, 2, false)}</div></div>
      <div class="kpi"><div class="k">Сегодня</div><div class="v ${cls(stt.todayUsd)}">${fmt.usd(stt.todayUsd)}</div><div class="d">${fmt.int(stt.tradesToday)} сделок</div></div>
      <div class="kpi"><div class="k">Win rate</div><div class="v">${win != null ? win + '%' : '—'}</div><div class="d">${fmt.int(stt.wins)} из ${fmt.int(stt.trades)}</div></div>
      <div class="kpi"><div class="k">Исполнение заявок</div><div class="v">${fillRate != null ? fillRate + '%' : '—'}</div><div class="d">${fmt.int(stt.filled)} из ${fmt.int(stt.placed)} · снято ${fmt.int(stt.cancelled)}</div></div>
    </div>
  </section>

  ${isLive ? `<div class="callout" style="margin-top:14px">${ICON.warn}<div>Реальные ордера на Binance: вход и тейк — post-only лимитки, стоп стоит на самой бирже (STOP_MARKET по mark price) и закроет позицию, даже если бот или интернет упадут. Стоп закрывает всю позицию по символу: не держите на этих символах ручных позиций.${oth.trades ? ` Paper отдельно: ${fmt.usd(oth.realizedUsd)} за ${fmt.int(oth.trades)} сделок.` : ''}${d.lastNote ? '<br>Последнее: ' + esc(d.lastNote) : ''}</div></div>` : `<div class="callout info" style="margin-top:14px">${ICON.info}<div>Paper здесь считает строго: заявка исполняется, только если цена прошла сквозь наш уровень (касание не считается), заявка начинает работать через ${esc(t.paperFillLatencyMs ?? 700)} мс, а рыночный выход берётся по стакану ещё через столько же. ${d.lastNote ? '<br>Последнее: ' + esc(d.lastNote) : ''}</div></div>`}

  <section class="panel" style="margin-top:14px"><div class="panel-head"><h2>Сейчас</h2></div><div class="panel-body">
    ${pos ? `<div class="row wrap" style="gap:14px"><span class="sym">${esc(baseOf(pos.symbol))}</span><span class="${pos.side === 'Long' ? 'long' : 'short'}">${pos.side === 'Long' ? 'long' : 'short'}</span><span class="num">вход ${fmt.price(pos.entry)}</span><span class="num pos">тейк ${fmt.price(pos.takeProfit)}</span><span class="num neg">стоп ${fmt.price(pos.stop)}</span><span class="num mut">плотность ${fmt.price(pos.wallPrice)}</span><span class="num">${signed(pos.unrealizedUsd)}</span>${pos.live ? `<span class="badge neg">live</span>${pos.exchangeStop ? '<span class="badge pos">стоп на бирже</span>' : '<span class="badge neg">стопа на бирже нет</span>'}` : ''}${pos.exiting ? `<span class="badge neg">выходим: ${esc((why[pos.exiting] || [pos.exiting])[0])}</span>` : ''}</div>`
      : rest ? `<div>${rest.live ? '<span class="badge neg">live</span> ' : ''}Заявка ${rest.side === 'Long' ? 'на покупку' : 'на продажу'} <b>${esc(baseOf(rest.symbol))}</b> по ${fmt.price(rest.limit)} стоит перед плотностью ${fmt.price(rest.wallPrice)} · ${fmt.ago(rest.placedAt)}</div>`
      : `<div class="mut">${sc.enabled ? 'Ждём, когда цена подойдёт к зрелой плотности ближе ' + fmt.pct(t.densityApproachPercent ?? 0.3, 2, false) + '.' : 'Скальпер выключен.'}</div>`}
  </div></section>

  <section class="panel" style="margin-top:14px"><div class="panel-head"><h2>Плотности рядом с ценой</h2><span class="sub">от $${fmt.int(t.densityMinWallUsd ?? 20000)} и ×${esc(t.densityMinMultiple ?? 5)} к среднему уровню</span></div>
    <div class="panel-body flush">${walls.length ? `<div class="tbl-wrap"><table class="tbl"><thead><tr><th>Символ</th><th>Сторона</th><th class="r">Цена</th><th class="r">Объём</th><th class="r">× среднего</th><th class="r">До цены</th><th class="r">Стоит</th></tr></thead><tbody>
      ${walls.map(w => `<tr><td class="sym">${esc(baseOf(w.symbol))}</td><td class="${w.side === 'bid' ? 'long' : 'short'}">${w.side === 'bid' ? 'покупка' : 'продажа'}</td><td class="r num">${fmt.price(w.price)}</td><td class="r num">$${fmt.int(w.notionalUsd)}</td><td class="r num">×${fmt.int(w.multiple)}</td><td class="r num">${fmt.pct(w.distancePercent, 2, false)}</td><td class="r num ${w.mature ? '' : 'mut'}">${fmt.dur(w.ageSeconds)}${w.mature ? '' : ' · рано'}</td></tr>`).join('')}
    </tbody></table></div>` : '<div class="empty">Плотностей по фильтру сейчас нет</div>'}</div></section>

  ${(d.blacklist || []).length ? `<section class="panel" style="margin-top:14px"><div class="panel-head"><h2>Чёрный список</h2><span class="sub">монета, проигравшая ${esc(t.densitySymbolMaxLosses ?? 2)} ${ruPlural(t.densitySymbolMaxLosses ?? 2, 'раз', 'раза', 'раз')}, больше не торгуется</span></div><div class="panel-body" style="display:flex;flex-wrap:wrap;gap:8px">${(d.blacklist || []).map(lc).map(x => `<span class="chip on" style="gap:8px"><b>${esc(baseOf(x.symbol))}</b><span class="small neg">${fmt.int(x.losses)} ${ruPlural(x.losses, 'убыток', 'убытка', 'убытков')} · ${fmt.usd(x.pnlUsd)}</span>${x.until ? `<span class="small mut">до ${new Date(x.until).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' })}</span>` : ''}<button type="button" class="btn sm ghost" data-act="density-unblock" data-symbol="${esc(x.symbol)}">Вернуть</button></span>`).join('')}</div></section>` : ''}
  ${(stt.cancelReasons || []).length ? `<div class="mut small" style="margin-top:10px">Почему заявки снимались: ${(stt.cancelReasons || []).map(lc).map(c => esc(c.reason) + ' — ' + fmt.int(c.count)).join(' · ')}</div>` : ''}

  <section class="panel" style="margin-top:14px"><div class="panel-head" style="flex-wrap:wrap;gap:6px 12px"><h2>История</h2><div class="tools">${closed.some(x => !x.live) ? '<button class="btn sm ghost" data-act="density-reset">Очистить paper-историю</button>' : ''}<button class="btn sm ghost" data-go="settings" onclick="setTimeout(()=>document.getElementById('sec-scalp')?.scrollIntoView(),450)">Настройки скальпера</button></div></div>
    <div class="panel-body flush">${closed.length ? `<div class="tbl-wrap"><table class="tbl"><thead><tr><th>Закрыта</th><th>Символ</th><th>Сторона</th><th class="r">Вход → выход</th><th class="r">Плотность</th><th class="r">Держали</th><th class="r">P&amp;L</th><th>Причина</th></tr></thead><tbody>
      ${closed.map(x => { const [tt, c] = why[x.reason] || [x.reason, '']; return `<tr><td class="mut">${fmt.dt(x.closedAt)}</td><td class="sym">${esc(baseOf(x.symbol))}</td><td class="${x.isLong ? 'long' : 'short'}">${x.isLong ? 'long' : 'short'}${x.live ? ' <span class="badge neg">live</span>' : ''}</td><td class="r num">${fmt.price(x.entry)} → ${fmt.price(x.exit)}</td><td class="r num mut">${fmt.price(x.wallPrice)}</td><td class="r num">${fmt.dur((new Date(x.closedAt) - new Date(x.openedAt)) / 1000)}</td><td class="r">${signed(x.pnlUsd)}</td><td><span class="badge ${c}">${esc(tt)}</span></td></tr>`; }).join('')}
    </tbody></table></div>` : '<div class="empty">Сделок пока нет</div>'}</div></section>`;
}

function renderScalp() {
  const sc = S.scalp ? lc(S.scalp) : null;
  if (!sc) return '<section class="panel"><div class="empty">Загружаем скальпер…</div></section>';
  if ((S.settings?.trading?.scalpStrategy || 'lag') === 'density') return renderDensity(sc);
  const w = lc(sc.wallet || {}); const st = lc(sc.settings || {});
  const open = (sc.open || []).map(lc); const closed = (sc.closed || []).map(lc); const pend = (sc.pending || []).map(lc);
  const isLive = w.mode === 'live';
  const cur = lc((isLive ? w.live : w.paper) || {}); const other = lc((isLive ? w.paper : w.live) || {});
  const winRate = cur.trades ? (cur.wins / cur.trades * 100) : null;
  const scVen = (sc.liveVenues && sc.liveVenues.length ? sc.liveVenues : []);
  const realBal = (S.liveBalances?.exchanges || []).map(lc).filter(e => e.ok && scVen.includes(e.exchange));
  const realUsdt = realBal.reduce((a, e) => a + (num(e.usdtTotal) || 0), 0);
  const realFree = realBal.reduce((a, e) => a + (num(usdtFree(e)) || 0), 0);
  const mode = !sc.enabled ? ['выключен', ''] : sc.live ? ['реальные ордера', 'neg'] : ['paper', 'long'];
  return `
  <section class="panel">
    <div class="panel-body row wrap" style="gap:18px;align-items:center">
      <div style="flex:1;min-width:min(260px,100%)"><div class="row" style="gap:8px 10px;flex-wrap:wrap"><h2 style="margin:0;font-size:20px;letter-spacing:-.02em">Скальпер</h2><span class="badge ${mode[1]}">${mode[0]}</span>${sc.liveRequested && !sc.live ? '<span class="badge cost">live запрошен, но live-ордера выключены</span>' : ''}<span class="badge">биржи: ${esc(sc.venues === 'все' || !sc.venues ? 'все' : sc.venues.split(',').map(venue).join(', '))}</span>${sc.venuesUnknown?.length ? `<span class="badge neg">неизвестные биржи: ${esc(sc.venuesUnknown.join(', '))}</span>` : ''}${sc.live ? `<span class="badge ${sc.liveVenues?.length ? 'pos' : 'neg'}">${sc.liveVenues?.length ? 'реально на: ' + esc(sc.liveVenues.join(', ')) : 'нет бирж с trade-ключами'}</span>` : ''}</div>
        <div style="margin-top:10px">${strategySwitch('lag')}</div>
        <p class="prose" style="margin:8px 0 0">Ловит момент, когда рынок на большинстве бирж уже сдвинулся, а одна биржа отстала. Берёт одну ногу на отстающей бирже в сторону рынка и выходит, когда она догонит. Торгует на отдельном депозите и не трогает символы, которые держит арбитраж.</p></div>
      <button class="btn ${sc.enabled ? '' : 'primary'}" data-act="scalp-toggle" style="height:40px">${sc.enabled ? ICON.pause + 'Выключить' : ICON.play + 'Включить скальпер'}</button>
    </div>
    <div class="kpis" style="border-top:1px solid var(--line-soft)">
      <div class="kpi"><div class="k">Депозит × плечо</div><div class="v">${fmt.usd(w.depositUsd, 0, false)} × ${esc(st.leverage)}</div><div class="d">размер позиции ${fmt.usd((num(w.depositUsd) || 0) * (num(st.leverage) || 1) / Math.max(1, num(st.maxOpen) || 1), 0, false)}</div></div>
      <div class="kpi"><div class="k">${isLive ? 'Live-капитал скальпера' : 'Paper-капитал'}</div><div class="v">${fmt.usd(cur.equityUsd, 2, false)}</div><div class="d">реализовано ${fmt.usd(cur.realizedUsd)}${num(cur.unrealizedUsd) ? ' · открыто ' + fmt.usd(cur.unrealizedUsd) : ''}</div></div>
      <div class="kpi"><div class="k">Сегодня${isLive ? ', live' : ', paper'}</div><div class="v ${cls(cur.realizedTodayUsd)}">${fmt.usd(cur.realizedTodayUsd)}</div><div class="d">${fmt.int(cur.tradesToday)} сделок · лимит −${fmt.usd(st.dailyLossUsd, 2, false)}</div></div>
      <div class="kpi"><div class="k">Win rate${isLive ? ', live' : ', paper'}</div><div class="v">${winRate != null ? winRate.toFixed(0) + '%' : '—'}</div><div class="d">${fmt.int(cur.wins)} из ${fmt.int(cur.trades)}</div></div>
      ${isLive
        ? `<div class="kpi"><div class="k">USDT на ${esc(scVen.map(venue).join(', ') || 'биржах')}</div><div class="v">${realBal.length ? fmt.usd(realUsdt, 2, false) : '—'}</div><div class="d">${realBal.length ? 'свободно ' + fmt.usd(realFree, 2, false) + ' · ' + fmt.ago(S.liveBalances.utc) : '<button class="btn sm ghost" data-act="live-balances">Запросить баланс</button>'}</div></div>`
        : `<div class="kpi"><div class="k">Live реализовано</div><div class="v ${cls(other.realizedUsd)}">${fmt.usd(other.realizedUsd)}</div><div class="d">${fmt.int(other.trades)} сделок</div></div>`}
    </div>
  </section>
  ${(() => { const wv = sc.watch ? lc(sc.watch) : null; if (!sc.enabled || !wv) return sc.lastNote ? `<div class="callout info" style="margin-top:14px">${ICON.info}<div>Последнее наблюдение: ${esc(sc.lastNote)}</div></div>` : '';
    const nr = wv.nearest ? lc(wv.nearest) : null;
    const pct = nr ? Math.min(100, (num(nr.ofThreshold) || 0) * 100) : 0;
    const state = !num(wv.watched) ? 'Нет ни одной пары для входа: проверьте биржи скальпера и ключи с правом trade.'
      : num(wv.warming) >= num(wv.watched) ? `Скальпер учит обычное смещение бирж (${fmt.int(wv.warming)} из ${fmt.int(wv.watched)} пар). Это занимает около минуты после старта.`
      : nr ? `Ближе всего ко входу: <b>${esc(baseOf(nr.symbol))}</b> на ${esc(venue(nr.exchange))}, отставание ${fmt.pct(Math.abs(num(nr.lagPercent)), 3, false)} из нужных ${fmt.pct(wv.thresholdPercent, 2, false)}. Не хватает: ${esc(nr.why || '—')}.`
      : 'Рынок стоит: за последние секунды цены почти не двигались.';
    return `<section class="panel" style="margin-top:14px"><div class="panel-head"><h2>Что видит скальпер</h2><span class="sub">обновлено ${fmt.ago(wv.utc)}</span></div><div class="panel-body" style="display:grid;gap:10px">
      <div>${state}</div>
      ${nr ? `<div class="row" style="gap:10px"><div class="track" style="flex:1;height:8px;background:var(--bg-deep);border-radius:99px;overflow:hidden"><div style="height:100%;width:${pct.toFixed(0)}%;background:${pct >= 100 ? 'var(--long)' : 'var(--cost)'}"></div></div><span class="num small mut">${pct.toFixed(0)}% порога</span></div>` : ''}
      <div class="mut small">Пар биржа × символ под наблюдением: ${fmt.int(wv.watched)} · ещё учатся: ${fmt.int(wv.warming)} · символов: ${fmt.int(wv.symbols)}${sc.lastNote ? ' · последний сигнал: ' + esc(sc.lastNote) : ''}</div>
    </div></section>`; })()}
  <div class="callout" style="margin-top:14px">${ICON.warn}<div>Скальп — направленная сделка: в отличие от хеджа, она зарабатывает или теряет на движении цены. Гоняйте его в paper хотя бы несколько дней и включайте реальные ордера, только если результат держится в плюсе после комиссий.</div></div>

  <section class="panel" style="margin-top:18px"><div class="panel-head"><h2>Открытые позиции</h2><span class="sub">вход ${fmt.pct(st.entryPercent, 2, false)} отставания · стоп ${fmt.pct(st.stopPercent, 2, false)} · до ${esc(st.maxHoldSeconds)} с</span></div>
    <div class="panel-body flush">${open.length || pend.length ? `<div class="tbl-wrap"><table class="tbl"><thead><tr><th>Символ</th><th>Биржа</th><th>Сторона</th><th class="r">Кол-во</th><th class="r">Вход</th><th class="r">Метка</th><th class="r">Держим</th><th class="r">P&amp;L</th><th>Режим</th></tr></thead><tbody>
      ${pend.map(x => `<tr><td class="sym">${esc(baseOf(x.symbol))}</td><td>${esc(venue(x.exchange))}</td><td class="${x.side === 'Long' ? 'long' : 'short'}">${x.side === 'Long' ? 'long' : 'short'}</td><td colspan="5" class="mut">ордер в пути…</td><td><span class="badge">paper</span></td></tr>`).join('')}
      ${open.map(x => `<tr title="${esc(x.note || '')}"><td class="sym">${esc(baseOf(x.symbol))}</td><td>${esc(venue(x.exchange))}</td><td class="${x.side === 'Long' ? 'long' : 'short'}">${x.side === 'Long' ? 'long' : 'short'}</td><td class="r num">${fmt.qty(x.qty)}</td><td class="r num">${fmt.price(x.entry)}</td><td class="r num mut">${fmt.price(x.lastMarkPrice)}</td><td class="r num">${fmt.dur((Date.now() - new Date(x.openedAt)) / 1000)}</td><td class="r">${signed(x.unrealizedUsd)}</td><td>${x.needsReconciliation ? '<span class="badge neg">нужна сверка</span>' : x.live ? '<span class="badge neg">live</span>' : '<span class="badge">paper</span>'}</td></tr>`).join('')}
    </tbody></table></div>` : `<div class="empty"><strong>${sc.enabled ? 'Ждём отставания одной из бирж' : 'Скальпер выключен'}</strong>${sc.enabled ? 'Вход появится, когда биржа отстанет от рынка сильнее порога и это перекроет комиссии.' : 'Включите его кнопкой выше. Он не мешает арбитражу.'}</div>`}</div></section>

  ${isLive && other.trades ? `<div class="mut small" style="margin-top:10px">Paper-результат отдельно: ${fmt.usd(other.realizedUsd)} за ${fmt.int(other.trades)} сделок. В live-цифры и дневной лимит он не входит.</div>` : ''}
  <section class="panel" style="margin-top:18px"><div class="panel-head"><h2>История</h2><div class="tools">${closed.some(x => !x.live) ? '<button class="btn sm ghost" data-act="scalp-reset">Очистить paper-историю</button>' : ''}<button class="btn sm ghost" data-go="settings" onclick="setTimeout(()=>document.getElementById('sec-scalp')?.scrollIntoView(),450)">Настройки скальпера</button></div></div>
    <div class="panel-body flush">${closed.length ? `<div class="tbl-wrap"><table class="tbl"><thead><tr><th>Закрыта</th><th>Символ</th><th>Биржа</th><th>Сторона</th><th class="r">Вход → выход</th><th class="r">Держали</th><th class="r">P&amp;L</th><th>Причина</th></tr></thead><tbody>
      ${closed.map(x => { const why = String(x.note || '').split('|').pop().trim(); const map = { 'caught-up': ['догнала', 'pos'], target: ['цель', 'pos'], stop: ['стоп', 'neg'], 'market-reversed': ['рынок развернулся', 'neg'], time: ['по времени', ''], 'time-profit': ['по времени', 'pos'], 'no-book': ['нет стакана', 'neg'], 'imbalance died': ['старый сигнал', ''] }; const [t, c] = map[why] || [why, '']; return `<tr title="${esc(x.note || '')}"><td class="mut">${fmt.dt(x.closedAt)}</td><td class="sym">${esc(baseOf(x.symbol))}</td><td>${esc(venue(x.exchange))}</td><td class="${x.side === 'Long' ? 'long' : 'short'}">${x.side === 'Long' ? 'long' : 'short'}${x.live ? ' <span class="badge neg">live</span>' : ''}</td><td class="r num">${fmt.price(x.entry)} → ${fmt.price(x.exit)}</td><td class="r num">${x.closedAt ? fmt.dur((new Date(x.closedAt) - new Date(x.openedAt)) / 1000) : '—'}</td><td class="r">${signed(x.realizedUsd)}</td><td><span class="badge ${c}">${esc(t)}</span></td></tr>`; }).join('')}
    </tbody></table></div>` : '<div class="empty">Сделок пока нет</div>'}</div></section>`;
}

// Live -------------------------------------------------------------
function renderLive() {
  const st = liveSt();
  const m = modeOf();
  const phaseIdx = m.key === 'live' ? 2 : m.key === 'readonly' ? 1 : 0;
  const bal = S.liveBalances ? lc(S.liveBalances) : null;
  const exRows = (bal?.exchanges || []).map(lc);
  const lp = S.livePositions ? lc(S.livePositions) : null;
  const audit = st.auditTail || [];
  return `
  ${st.killed ? `<div class="callout bad" style="margin-bottom:16px">${ICON.warn}<div><strong>Kill switch активен.</strong> Новые ордера запрещены. Причина: ${esc(st.killReason || 'не указана')}. Сверьте позиции на биржах, затем включите live заново.</div></div>` : ''}
  <section class="panel"><div class="phases">
    <div class="phase ${phaseIdx > 0 ? 'done' : 'now'}"><div class="t">1. Paper</div><div class="d">Реальные стаканы, виртуальная маржа, ордеров нет.</div></div>
    <div class="phase ${phaseIdx > 1 ? 'done' : phaseIdx === 1 ? 'now' : ''}"><div class="t">2. Только чтение</div><div class="d">Ключи подключены, бот видит балансы и позиции, но не торгует.</div></div>
    <div class="phase ${phaseIdx === 2 ? 'now danger' : ''}"><div class="t">3. Реальные ордера</div><div class="d">Хеджи открываются на биржах в пределах лимитов ниже.</div></div>
  </div>
  <div class="panel-body row wrap" style="gap:10px;border-top:1px solid var(--line-soft)">
    ${phaseIdx === 0 && !st.killed ? '<button class="btn" data-act="live-enable-ro">Включить только чтение</button><button class="btn danger" data-act="live-enable">Включить реальные ордера</button>' : ''}
    ${phaseIdx === 1 ? '<button class="btn danger" data-act="live-enable">Перейти к реальным ордерам</button><button class="btn ghost" data-act="live-disable">Вернуться в paper</button>' : ''}
    ${phaseIdx === 2 ? '<button class="btn" data-act="live-disable">Остановить live, вернуться в paper</button>' : ''}
    ${st.killed ? '<button class="btn" data-act="kill-reset">Снять kill switch</button>' : ''}
    <span class="grow" style="flex:1"></span>
    <span class="mut small">Лимиты: до ${fmt.int(st.maxOpenPositions)} хеджей · до ${fmt.usd(st.maxNotionalUsd, 0, false)} на хедж · стоп дня ${fmt.usd(st.dailyLossLimitUsd, 0)} · сегодня ${fmt.usd(st.dayRealizedUsd)}</span>
  </div></section>

  <div class="killbox" style="margin-top:18px">
    <div class="txt"><strong>Аварийная остановка</strong><span>Мгновенно запрещает новые ордера. Открытые позиции остаются на биржах — закройте их вручную или со страницы позиций.</span></div>
    <button class="btn solid-danger" data-act="kill">${ICON.stop}Kill switch</button>
  </div>

  <div class="grid cols-3-2" style="margin-top:18px">
    <section class="panel"><div class="panel-head"><h2>Балансы на биржах</h2><button class="btn sm ghost" data-go="balances">Подробно</button><span class="sub">${bal ? 'обновлено ' + fmt.ago(bal.utc) : ''}</span><div class="tools"><button class="btn sm ghost" data-act="live-balances">${ICON.refresh}Обновить</button></div></div>
      <div class="panel-body flush">${!bal ? `<div class="empty">${S.liveBalancesLoading ? 'Запрашиваем биржи…' : 'Нажмите «Обновить», чтобы запросить балансы по ключам.'}</div>` : `<div class="tbl-wrap"><table class="tbl"><thead><tr><th>Биржа</th><th>Ключ</th><th class="r">USDT</th><th class="r">Позиций</th><th>Состояние</th></tr></thead><tbody>
        ${exRows.map(e => `<tr><td>${esc(venue(e.exchange))}</td><td>${e.hasKey === false ? '<span class="mut">нет</span>' : `<span class="badge ${e.permission === 'trade' ? 'neg' : ''}">${esc(e.permission || 'есть')}</span>`}</td><td class="r num">${e.ok ? fmt.usd(e.usdtTotal, 2, false) : '—'}</td><td class="r num">${Array.isArray(e.positions) ? e.positions.length : '—'}</td><td>${e.ok ? '<span class="badge pos">ok</span>' : `<span class="mut small">${esc(e.error || '—')}</span>`}</td></tr>`).join('')}
        <tr><td colspan="2" class="mut">Всего, примерно</td><td class="r num">${fmt.usd(bal.totalUsdtApprox, 2, false)}</td><td colspan="2"></td></tr>
      </tbody></table></div>`}</div></section>
    <section class="panel"><div class="panel-head"><h2>Журнал guard</h2></div><div class="panel-body">${audit.length ? `<div class="audit">${audit.map(a => `<div>${esc(typeof a === 'string' ? a : JSON.stringify(a))}</div>`).join('')}</div>` : '<div class="mut">Событий нет</div>'}</div></section>
  </div>

  <section class="panel" style="margin-top:18px"><div class="panel-head"><h2>Сверка позиций</h2><span class="sub">журнал бота против фактических ног на биржах</span><div class="tools"><button class="btn sm ghost" data-act="live-positions">${ICON.refresh}Сверить</button></div></div>
    <div class="panel-body">${!lp ? '<div class="mut">Нажмите «Сверить» после рестарта и перед включением ордеров: позиции в журнале должны совпадать с ногами на биржах.</div>' : `
      <div class="row wrap" style="gap:24px;margin-bottom:12px"><div><span class="mut small">В журнале</span><div class="num" style="font-size:20px">${fmt.int(lp.openLedgerCount)}</div></div><div><span class="mut small">Ног на биржах</span><div class="num" style="font-size:20px">${fmt.int(lp.exchangeLegCount)}</div></div><div><span class="mut small">Реализовано live</span><div style="font-size:20px">${signed(lp.realizedPnlUsd)}</div></div></div>
      ${(num(lp.exchangeLegCount) || 0) !== (num(lp.openLedgerCount) || 0) * 2 ? `<div class="callout">${ICON.warn}<div>Число ног на биржах не равно удвоенному числу хеджей в журнале. Проверьте позиции вручную, прежде чем разрешать новые ордера.</div></div>` : `<div class="callout info">${ICON.ok}<div>Журнал и биржи совпадают.</div></div>`}`}</div></section>`;
}

// Settings ---------------------------------------------------------
const SETTINGS = [
  { id: 'mode', title: 'Режим', lead: 'Как бот работает с сигналами.', fields: [
    ['paperTrading', 'Paper-торговля', 'bool', 'Виртуальная маржа на реальных стаканах'],
    ['paperAutoExecute', 'Автоисполнение', 'bool', 'Открывать хеджи автоматически при прохождении гейтов'],
    ['includeFunding', 'Учитывать funding', 'bool', 'Расчёты funding в горизонте удержания могут запретить вход'],
  ]},
  { id: 'size', title: 'Размер и риск', lead: 'Сколько денег участвует в одном хедже и где бот остановится.', fields: [
    ['quoteSize', 'Размер хеджа', 'num', 'Notional одной ноги', 'USDT'],
    ['maxNotionalUsd', 'Потолок notional', 'num', 'Жёсткий предел на хедж в paper', 'USDT'],
    ['futuresPaperLeverage', 'Плечо paper', 'num', 'От 1 до 10', 'x'],
    ['futuresMaxOpenPositions', 'Макс. открытых хеджей', 'int', ''],
    ['maxLegsPerVenue', 'Макс. ног на бирже', 'int', 'Ограничивает концентрацию маржи'],
    ['maxMarginUsagePercent', 'Доля свободной маржи', 'num', '0.35 = не больше 35% на ногу', ''],
    ['futuresStopLossUsd', 'Стоп на хедж', 'num', 'Отрицательное число', 'USDT'],
    ['futuresDailyLossLimitUsd', 'Дневной лимит убытка', 'num', 'Отрицательное число', 'USDT'],
    ['paperStartingQuote', 'Стартовая маржа на биржу', 'num', 'Для paper-кошелька', 'USDT'],
  ]},
  { id: 'entry', title: 'Фильтры входа', lead: 'Пороги, которые сигнал проходит до ордера. Код больше не занижает эти значения — работает то, что вы задали.', fields: [
    ['minProfitPercent', 'Мин. чистый edge', 'num', 'После 4 комиссий, выхода и запаса', '%'],
    ['minGrossSpreadPercent', 'Мин. gross спред', 'num', 'Не ниже комиссий открытия + 0.02', '%'],
    ['minTakeProfitUsd', 'Мин. ожидаемый результат', 'num', 'В долларах на размер хеджа', 'USDT'],
    ['openEdgeBufferPercent', 'Запас на исполнение', 'num', 'От 0.02 до 0.04', '%'],
    ['minSpreadPersistMs', 'Удержание сигнала', 'int', 'От 200 мс до 5 с', 'мс'],
    ['requireRoundTripEdge', 'Требовать round-trip edge', 'bool', ''],
    ['requireDepthFullFill', 'Требовать полный fill', 'bool', 'Стакан должен закрыть весь размер'],
    ['paperRequireFullFill', 'Полный fill в paper', 'bool', ''],
    ['requireSpreadingEdge', 'Отсекать схлопывающийся спред', 'bool', ''],
    ['paperCooldownMs', 'Пауза между сделками', 'int', '', 'мс'],
  ]},
  { id: 'exec', title: 'Реализм исполнения', lead: 'Защита от фантомного арбитража: свежесть котировок, прогрев базиса, задержка и IOC-лимиты. Одни и те же значения используют paper и live.', fields: [
    ['tradeMaxQuoteAgeMs', 'Макс. возраст котировки ноги', 'int', 'REST-котировки никогда не торгуются', 'мс'],
    ['maxLegSkewMs', 'Макс. разнос ног во времени', 'int', '', 'мс'],
    ['basisMinSamples', 'Точек для базиса', 'int', 'Примерно одна точка в 0.7 с'],
    ['basisMinSpanSeconds', 'Окно прогрева базиса', 'int', '', 'с'],
    ['paperFillLatencyMs', 'Задержка исполнения paper', 'int', 'Поставьте измеренную задержку ваших ордеров', 'мс'],
    ['iocSlipBps', 'Запас IOC-лимита', 'num', 'Сверх цены сигнала', 'bps'],
    ['fundingHorizonMinutes', 'Горизонт funding', 'int', '', 'мин'],
    ['liveConcurrentLegs', 'Обе ноги одновременно (live)', 'bool', 'Рекомендуется: меньше времени на одной ноге'],
    ['maxBookAgeMs', 'Макс. возраст книги для показа', 'int', '', 'мс'],
    ['scanIntervalMs', 'Период скана', 'int', 'От 100 мс', 'мс'],
  ]},
  { id: 'exit', title: 'Выход', lead: 'Когда бот закрывает хедж.', fields: [
    ['closeBelowNetPercent', 'Закрыть при сходимости ниже', 'num', '', '%'],
    ['maxHoldMinutes', 'Макс. удержание', 'int', '0 — без лимита', 'мин'],
    ['futuresMaxHoldSeconds', 'Макс. удержание (точно)', 'int', '0 — без лимита', 'с'],
    ['paperCloseFeeFactor', 'Множитель комиссии выхода paper', 'num', '1 — честный taker. Меньше 1 приукрашивает paper'],
  ]},
  { id: 'universe', title: 'Набор символов', lead: 'Какие перпы бот отслеживает.', fields: [
    ['dynamicSymbols', 'Динамический набор', 'bool', 'Подбирать ликвидные символы автоматически'],
    ['dynamicTopN', 'Сколько символов', 'int', ''],
    ['dynamicMinQuoteVolumeUsd', 'Мин. объём за сутки', 'num', '', 'USDT'],
    ['dynamicMaxQuoteVolumeUsd', 'Макс. объём за сутки', 'num', 'Отсекает самые эффективные рынки', 'USDT'],
    ['dynamicRefreshMinutes', 'Обновлять набор каждые', 'int', '', 'мин'],
  ]},
  { id: 'scalp', title: 'Скальпер', lead: 'Одна нога на отстающей бирже, отдельный депозит. Не занимает лимиты, маржу и дневной стоп арбитража и не торгует символы, которые держит арбитраж.', fields: [
    ['spatialScalpMode', 'Скальпер включён', 'bool', 'Без этого он только наблюдает за рынком'],
    ['scalpLive', 'Скальпить реальными ордерами', 'bool', 'Действует, только когда live-ордера включены. Иначе скальп идёт в paper'],
    ['scalpVenues', 'Биржи скальпера', 'venues', 'Отметьте, где скальпер может входить. Ничего не отмечено — все биржи. Отставание всегда считается по всем биржам. Реальные ордера идут только туда, где ключ с правом trade'],
    ['scalpDepositUsd', 'Депозит скальпера', 'num', 'Держите эти USDT на биржах сверх капитала арбитража', 'USDT'],
    ['scalpLeverage', 'Плечо', 'num', 'От 1 до 5. Большее значение будет снижено до 5', 'x'],
    ['scalpMaxOpen', 'Позиций одновременно', 'int', 'Депозит делится между ними'],
    ['scalpDailyLossUsd', 'Дневной лимит убытка', 'num', 'После него скальпер не открывает сделки до конца дня UTC', 'USDT'],
    ['scalpEntryPercent', 'Мин. отставание биржи', 'num', 'От её обычного смещения к остальным', '%'],
    ['scalpMinNetPercent', 'Мин. ожидаемый результат', 'num', 'После ширины стакана и двух комиссий', '%'],
    ['scalpStopPercent', 'Стоп по цене', 'num', '', '%'],
    ['scalpMaxHoldSeconds', 'Макс. удержание', 'int', '', 'с'],
    ['scalpCooldownSeconds', 'Пауза между входами', 'int', '', 'с'],
  ]},
  { id: 'density', title: 'Скальпер: плотности', lead: 'Работает, когда на странице скальпера выбрана стратегия «Плотности в стакане». Депозит, плечо, биржи, дневной лимит, паузу и «Скальпить реальными ордерами» берёт из раздела «Скальпер». Реальные ордера — только на Binance.', fields: [
    ['densityMinWallUsd', 'Мин. объём плотности', 'num', 'Заявка на одном уровне цены', 'USDT'],
    ['densityMinMultiple', 'Во сколько раз больше обычного уровня', 'num', 'Против медианы уровней в зоне поиска', 'x'],
    ['densityScanPercent', 'Зона поиска от цены', 'num', '', '%'],
    ['densityMinWallAgeSeconds', 'Плотность должна простоять', 'int', 'Защита от фейковых заявок, которые снимают перед ценой', 'с'],
    ['densityApproachPercent', 'Ставить заявку, когда до плотности', 'num', '', '%'],
    ['densityTakeProfitPercent', 'Тейк', 'num', 'Лимиткой, от цены входа', '%'],
    ['densityStopBeyondPercent', 'Стоп за плотностью', 'num', 'Плюс выход сразу, если плотность съели или сняли', '%'],
    ['densityMaxHoldSeconds', 'Макс. удержание', 'int', '', 'с'],
    ['densityTrendBlockPercent', 'Не входить против тренда сильнее', 'num', 'Не покупать от плотности, если цена упала больше этого за окно ниже (и наоборот). 0 — выключено', '%'],
    ['densityTrendMinutes', 'Окно тренда', 'int', '', 'мин'],
    ['densityLossCooldownSeconds', 'Пауза после убыточной сделки', 'int', '', 'с'],
    ['densitySymbolMaxLosses', 'В чёрный список после убытков', 'int', 'Монета, проигравшая столько раз, больше не торгуется. 0 — выключено', 'шт'],
    ['densitySymbolBlockMinutes', 'Срок чёрного списка', 'int', '0 — пока не уберёте вручную на странице скальпера', 'мин'],
    ['densityMakerFeePercent', 'Комиссия мейкера', 'num', 'Binance: 0.02, с BNB меньше', '%'],
    ['densityTakerFeePercent', 'Комиссия тейкера', 'num', 'Для стопа и выхода по времени', '%'],
  ]},
  { id: 'live', title: 'Лимиты live', lead: 'Действуют только для реальных ордеров.', fields: [
    ['liveEquityPerExchangeUsd', 'Капитал на биржу', 'num', '', 'USDT'],
    ['liveMarginUsageFraction', 'Доля маржи на ногу', 'num', 'От 0.15 до 0.85'],
    ['liveMaxNotionalUsd', 'Макс. notional хеджа', 'num', '', 'USDT'],
    ['liveMaxOpenPositions', 'Макс. открытых хеджей', 'int', ''],
    ['liveStopLossUsd', 'Стоп на хедж', 'num', '', 'USDT'],
    ['liveDailyLossLimitUsd', 'Дневной лимит убытка', 'num', '', 'USDT'],
  ]},
];
const settingsDraft = { values: {}, dirty: new Set() };

function renderSettings() {
  const t = S.settings?.trading;
  if (!t) return '<section class="panel"><div class="empty">Загружаем настройки…</div></section>';
  const conns = S.settings.connections || {};
  const val = (k) => settingsDraft.dirty.has(k) ? settingsDraft.values[k] : t[k];
  const field = ([k, label, type, hint, unit]) => {
    const id = 'f_' + k;
    if (type === 'bool') return `<div class="field"><span class="lbl">${esc(label)}</span><label class="switch"><input type="checkbox" id="${id}" data-k="${k}" ${val(k) ? 'checked' : ''}><span class="track"></span><span class="small mut">${val(k) ? 'включено' : 'выключено'}</span></label>${hint ? `<span class="hint">${esc(hint)}</span>` : ''}</div>`;
    if (type === 'venues') {
      const sel = new Set(String(val(k) ?? '').split(',').map(x => x.trim()).filter(Boolean));
      const keyState = (ex) => { const c = conns[ex]; if (!c?.hasApiKey) return ['нет ключа', 'mut']; return String(c.permission).toLowerCase() === 'trade' && c.enabled ? ['trade', 'pos'] : ['только чтение', 'mut']; };
      const chips = VENUES_ALL.map(ex => { const on = sel.has(ex); const [ks, kc] = keyState(ex); return `<button type="button" class="chip ${on ? 'on' : ''}" data-venue-k="${k}" data-venue="${ex}" aria-pressed="${on}">${esc(venue(ex))}<span class="small ${kc}">${ks}</span></button>`; }).join('');
      const unknown = [...sel].filter(x => !VENUES_ALL.includes(x));
      return `<div class="field field-wide"><span class="lbl">${esc(label)}</span><div class="chips" style="flex-wrap:wrap" role="group" aria-label="${esc(label)}">${chips}</div><span class="hint">${sel.size === 0 ? '<b>Сейчас: все биржи.</b> ' : `<b>Сейчас: ${esc([...sel].filter(x => VENUES_ALL.includes(x)).map(venue).join(', ') || 'ни одной известной')}.</b> `}${unknown.length ? `<span class="neg">Неизвестные имена: ${esc(unknown.join(', '))} — снимите их, иначе по ним не будет сделок.</span> ` : ''}${esc(hint)}</span></div>`;
    }
    if (type === 'text') return `<div class="field"><label for="${id}">${esc(label)}</label><div class="input-wrap"><input class="input ${settingsDraft.dirty.has(k) ? 'dirty' : ''}" id="${id}" data-k="${k}" data-t="text" autocomplete="off" spellcheck="false" value="${esc(val(k) ?? '')}"></div>${hint ? `<span class="hint">${esc(hint)}</span>` : ''}</div>`;
    return `<div class="field"><label for="${id}">${esc(label)}</label><div class="input-wrap"><input class="input num ${settingsDraft.dirty.has(k) ? 'dirty' : ''}" id="${id}" data-k="${k}" data-t="${type}" inputmode="decimal" value="${esc(val(k) ?? '')}">${unit ? `<span class="unit">${esc(unit)}</span>` : ''}</div>${hint ? `<span class="hint">${esc(hint)}</span>` : ''}</div>`;
  };
  return `<div class="settings">
    <nav class="toc" aria-label="Разделы настроек">${SETTINGS.map(s => `<a href="#sec-${s.id}">${esc(s.title)}</a>`).join('')}<a href="#sec-keys">Ключи бирж</a><a href="#sec-danger">Сброс</a></nav>
    <div class="stack">
      ${SETTINGS.map(s => `<section class="panel form-sec" id="sec-${s.id}"><div class="panel-head"><h2>${esc(s.title)}</h2></div><div class="panel-body"><p class="lead">${esc(s.lead)}</p>${s.fields.map(field).join('')}</div></section>`).join('')}
      <section class="panel form-sec" id="sec-keys"><div class="panel-head"><h2>Ключи бирж</h2><span class="sub">секреты не возвращаются в браузер</span></div>
        <div class="panel-body" style="display:block"><p class="prose" style="margin:0 0 14px">Создавайте ключи без права вывода и с белым списком IP. Для paper ключи не нужны; для режима «только чтение» достаточно прав на чтение.</p>
        <div class="keys">${Object.entries(conns).map(([ex, c]) => { c = lc(c); return `<form class="keycard" data-ex="${esc(ex)}" autocomplete="off">
          <div class="h">${esc(venue(ex))}${c.hasApiKey ? `<span class="badge ${c.permission === 'trade' ? 'neg' : 'pos'}">${c.permission === 'trade' ? 'торговля' : 'чтение'} · ${esc(c.apiKeyMasked || '')}</span>` : '<span class="badge">не подключена</span>'}</div>
          <input class="input" name="apiKey" placeholder="${c.hasApiKey ? 'API key сохранён — введите новый для замены' : 'API key'}" aria-label="API key ${esc(venue(ex))}">
          <input class="input" name="apiSecret" type="password" placeholder="API secret" aria-label="API secret ${esc(venue(ex))}">
          ${c.needsPassphrase ? `<input class="input" name="passphrase" type="password" placeholder="Passphrase" aria-label="Passphrase ${esc(venue(ex))}">` : ''}
          <div class="row"><select class="input" name="permission" style="width:auto" aria-label="Права ключа"><option value="read-only" ${c.permission !== 'trade' ? 'selected' : ''}>Только чтение</option><option value="trade" ${c.permission === 'trade' ? 'selected' : ''}>Торговля</option></select>
          <span style="flex:1"></span>${c.hasApiKey ? `<button type="button" class="btn sm ghost" data-act="key-clear">Удалить</button>` : ''}<button type="submit" class="btn sm primary">Сохранить ключ</button></div></form>`; }).join('')}</div></div></section>
      <section class="panel form-sec" id="sec-danger"><div class="panel-head"><h2>Сброс paper</h2></div><div class="panel-body" style="display:flex;gap:10px;flex-wrap:wrap;align-items:center">
        <p class="lead">Сброс обнуляет виртуальные балансы и открытые paper-позиции. Журнал сделок на диске сохраняется.</p>
        <button class="btn" data-act="prune">Закрыть позиции вне набора символов</button><button class="btn danger" data-act="paper-reset">Сбросить paper-кошелёк</button></div></section>
      <div class="savebar ${settingsDraft.dirty.size ? 'on' : ''}" id="savebar"><span class="grow">Изменено параметров: <strong id="dirtyN">${settingsDraft.dirty.size}</strong>. Применяются сразу, без рестарта.</span><button class="btn ghost" data-act="settings-discard">Отменить</button><button class="btn primary" data-act="settings-save">Сохранить</button></div>
    </div></div>`;
}

// Help ---------------------------------------------------------------
function renderHelp() {
  return `<section class="panel"><div class="panel-body prose">
    <h3 style="margin-top:0">Что делает бот</h3>
    <p>Покупает perp на бирже, где он дешевле (long), и одновременно продаёт тот же perp там, где он дороже (short). Направление рынка не важно: прибыль появляется, когда цены двух бирж снова сближаются. Монеты между биржами не переводятся — на каждой заранее лежит USDT.</p>
    <h3>Почему сделок мало</h3>
    <p>Спред между биржами почти всегда объясняется постоянной разницей (fair basis) и издержками: четыре комиссии taker, ширина стаканов на выходе и запас на проскальзывание. Бот торгует только то, что остаётся сверх этого. Полоса разбора на обзоре показывает это наглядно: янтарные части — издержки, мятная — чистый edge.</p>
    <h3>Почему paper теперь честнее</h3>
    <p>Ордер в paper «прибывает» через заданную задержку и исполняется по стакану, который есть в этот момент, с теми же IOC-лимитами, что и в live. Если исполнилась только одна нога, её откат стоит две комиссии и ширину стакана — ровно как на бирже. Котировки из REST и устаревшие ноги не торгуются.</p>
    <h3>Порядок перехода к live</h3>
    <p>Минимум неделя paper с плюсом после промахов ног. Затем режим «только чтение»: подключите ключи без права вывода и сверьте балансы и позиции. Затем реальные ордера на $100 на биржу и один хедж за раз. Kill switch всегда в верхней панели, пока включён live.</p>
    <h3>Горячие клавиши</h3>
    <p><span class="num">1–9</span> — разделы, <span class="num">/</span> — поиск по сигналам, <span class="num">Esc</span> — закрыть панель маршрута.</p>
  </div></section>`;
}

// ───────────────────────── chrome ─────────────────────────
function renderStrip() {
  const m = modeOf();
  const health = (snap().exchangeHealth || []).map(lc);
  const live = health.filter(h => h.state === 'live').length;
  const age = S.lastSnapAt ? (Date.now() - S.lastSnapAt) / 1000 : null;
  const feedCls = S.transport === 'down' || age == null || age > 10 ? 'bad' : age > 3 ? 'warn' : 'ok';
  const paused = !!snap().isPaused;
  $('#strip').innerHTML = `
    <h1>${esc(PAGES[S.page].title)}</h1>
    <span class="mode" data-mode="${m.key}" title="${esc(m.sub)}"><span class="dot"></span>${esc(m.label)}</span>
    <span class="feed" title="Источник данных: ${esc(S.transport)}"><span class="led ${feedCls}"></span>${S.transport === 'signalr' ? 'Поток' : S.transport === 'poll' ? 'Опрос' : 'Нет связи'} · бирж ${live}/${health.length || '—'}</span>
    <span class="feed hide-sm">скан ${fmt.ago(snap().lastScanUtc)}</span>
    <span class="spacer"></span>
    <button class="btn ${paused ? 'primary' : ''}" data-act="toggle-pause" aria-pressed="${paused}">${paused ? ICON.play + 'Продолжить' : ICON.pause + 'Пауза'}</button>
    ${m.key === 'live' || m.key === 'readonly' ? `<button class="btn solid-danger" data-act="kill">${ICON.stop}<span class="hide-sm">Kill switch</span></button>` : ''}`;
  const p = fp();
  $('#railFoot').innerHTML = `<div class="row"><span>Реализовано</span>${signed(p.realizedPnlUsd)}</div><div class="row"><span>Хеджей</span><span class="num">${fmt.int(p.openCount)}</span></div><div class="row"><span>Символов</span><span class="num">${fmt.int((snap().symbols || []).length)}</span></div>`;
  const execN = opps().filter(o => o.isExecutable).length;
  const b1 = $('#nb-market'); if (b1) { b1.textContent = execN; b1.classList.toggle('hide', !execN); }
  const b2 = $('#nb-positions'); if (b2) { b2.textContent = p.openCount || 0; b2.classList.toggle('hide', !p.openCount); }
  document.title = `${m.key === 'live' ? '● LIVE ' : ''}${fmt.usd(p.dailyRealizedPnlUsd)} · ArbitrageBot`;
}

function renderNav() {
  $('#nav').innerHTML = Object.entries(PAGES).map(([k, p], i) => `<button data-go="${k}" ${S.page === k ? 'aria-current="page"' : ''} title="${esc(p.title)} (${i + 1})">${ICON[p.icon]}<span>${esc(p.title)}</span>${k === 'signals' ? '<span class="badge pos hide" id="nb-market"></span>' : k === 'positions' ? '<span class="badge long hide" id="nb-positions"></span>' : ''}</button>`).join('');
}

// ───────────────────────── render loop ─────────────────────────
const RENDER = { balances: renderBalances, scalp: renderScalp, overview: renderOverview, market: renderTerminal, signals: renderMarket, scanner: renderScanner, positions: renderPositions, analytics: renderAnalytics, funding: renderFunding, live: renderLive, settings: renderSettings, help: renderHelp };
let renderQueued = false, lastRender = 0;
function schedule(force = false) {
  if (force) { lastRender = 0; }
  if (renderQueued) return;
  renderQueued = true;
  const wait = Math.max(0, 350 - (Date.now() - lastRender));
  setTimeout(() => requestAnimationFrame(() => { renderQueued = false; lastRender = Date.now(); render(); }), wait);
}
function render() {
  renderStrip();
  const page = $('#page');
  // Settings is a form: only redraw it on demand, never on every tick (would wipe typing).
  if (S.page === 'settings' && page.dataset.page === 'settings' && !page.dataset.redraw) { return; }
  if (S.page === 'market' && page.dataset.page === 'market' && !page.dataset.redraw && $('#mkRoot')) { updateTerminal(); if (S.drawer) renderDrawer(); return; }
  // Keep focus & caret in search inputs across redraws
  const active = document.activeElement; const fid = active && page.contains(active) ? active.id : null;
  const caret = fid && active.selectionStart != null ? active.selectionStart : null;
  const scrollers = $$('.tbl-wrap', page).map(el => el.scrollLeft);
  page.innerHTML = RENDER[S.page]();
  page.dataset.page = S.page; delete page.dataset.redraw;
  $$('.tbl-wrap', page).forEach((el, i) => { if (scrollers[i]) el.scrollLeft = scrollers[i]; });
  if (fid) { const el = document.getElementById(fid); if (el) { el.focus(); if (caret != null && el.setSelectionRange) try { el.setSelectionRange(caret, caret); } catch { } } }
  if (S.page === 'overview' && S.snap && S.firstHero) setTimeout(() => { S.firstHero = false; }, 900);
  if (S.drawer) renderDrawer();
  if (S.page === 'market') updateTerminal();
  wireCharts();
}

function go(page) {
  if (!PAGES[page]) page = 'overview';
  if (S.page === 'settings' && settingsDraft.dirty.size && page !== 'settings' && !window.confirm('Есть несохранённые настройки. Уйти без сохранения?')) return;
  S.page = page;
  if (location.hash !== '#/' + page) history.replaceState(null, '', '#/' + page);
  renderNav();
  $('#page').dataset.redraw = '1';
  window.scrollTo(0, 0);
  onEnter(page);
  schedule(true);
}

function onEnter(page) {
  if (page === 'analytics') { loadPerf(); loadLedger(); }
  if (page === 'positions' && S.trades.src === 'all') loadLedger();
  if (page === 'funding' && !S.funding) loadFunding();
  if (page === 'settings') loadSettings(true);
  if (page === 'balances') { loadScalp(); loadLiveBalances(); }
  if (page === 'scalp') loadScalp();
}

// charts: hover tooltip on equity
function wireCharts() {
  const svg = $('svg[data-eq]'); if (!svg) return;
  const pts = JSON.parse(svg.dataset.eq || '[]'); if (pts.length < 2) return;
  const tip = $('#tip'); const line = $('#eqx', svg);
  svg.onmousemove = (e) => {
    const r = svg.getBoundingClientRect(); const vb = svg.viewBox.baseVal;
    const fx = (e.clientX - r.left) / r.width * vb.width;
    const t0 = pts[0][0], t1 = pts[pts.length - 1][0]; const L = 54, R = 12;
    const t = t0 + (fx - L) / (vb.width - L - R) * (t1 - t0);
    let best = pts[0]; for (const p of pts) if (Math.abs(p[0] - t) < Math.abs(best[0] - t)) best = p;
    const bx = L + (best[0] - t0) / Math.max(1, t1 - t0) * (vb.width - L - R);
    line.setAttribute('x1', bx); line.setAttribute('x2', bx); line.setAttribute('opacity', '1');
    tip.innerHTML = `${new Date(best[0]).toLocaleString('ru-RU')}<br>капитал ${best[1].toFixed(2)}`;
    tip.style.left = (e.clientX + 14) + 'px'; tip.style.top = (e.clientY - 10) + 'px'; tip.classList.add('on');
  };
  svg.onmouseleave = () => { tip.classList.remove('on'); line.setAttribute('opacity', '0'); };
}

// ───────────────────────── loaders ─────────────────────────
async function loadSettings(redraw = false) {
  try { S.settings = await api.get('/api/settings'); if (redraw && S.page === 'settings') { $('#page').dataset.redraw = '1'; schedule(true); } } catch (e) { if (redraw) toast('Не удалось загрузить настройки: ' + e.message, 'err'); }
}
async function loadPerf() { try { S.perf = await api.get('/api/analytics/performance?days=' + S.perfDays); schedule(true); } catch (e) { toast('Отчёт недоступен: ' + e.message, 'err'); } }
async function loadLedger() { try { const rows = await api.get('/api/analytics/trades?take=3000&skip=0'); S.ledger = (rows || []).map(lc).map(r => { const o = {}; for (const k in r) o[k.charAt(0).toLowerCase() + k.slice(1)] = r[k]; return o; }); schedule(true); } catch (e) { toast('Журнал недоступен: ' + e.message, 'err'); } }
async function loadScalp() { try { [S.scalp, S.density] = await Promise.all([api.get('/api/scalp'), api.get('/api/scalp/density').catch(() => null)]); if (['scalp', 'balances'].includes(S.page)) schedule(); if (S.page === 'scalp' && (S.scalp?.live ?? S.scalp?.Live) && (!S.liveBalancesAt || Date.now() - S.liveBalancesAt > 60_000)) loadLiveBalances(); } catch { } }
async function loadLiveBalances(force = false) {
  if (S.liveBalancesLoading) return;
  if (!force && S.liveBalances && Date.now() - S.liveBalancesAt < 20_000) return;
  S.liveBalancesLoading = true; schedule(true);
  try { S.liveBalances = await api.get('/api/live/balances'); S.liveBalancesAt = Date.now(); }
  catch (e) { toast('Балансы бирж недоступны: ' + e.message, 'err'); }
  finally { S.liveBalancesLoading = false; schedule(true); }
}
async function loadFunding() { try { S.funding = await api.get('/api/funding'); schedule(true); } catch (e) { toast('Funding недоступен: ' + e.message, 'err'); } }

// ───────────────────────── transport ─────────────────────────
function sampleMids() {
  const bt = snap().bookTickers || {}; const now = Date.now();
  for (const [sym, byEx] of Object.entries(bt)) {
    let h = S.midHist.get(sym); if (!h) { h = {}; S.midHist.set(sym, h); }
    for (const [ex, t0] of Object.entries(byEx)) {
      const t = lc(t0); const b = num(t.bestBid), a = num(t.bestAsk); if (!(b > 0 && a > 0)) continue;
      const arr = h[ex] || (h[ex] = []); const last = arr[arr.length - 1];
      if (!last || now - last.t >= 700) { arr.push({ t: now, m: (a + b) / 2 }); if (arr.length > 360) arr.shift(); }
    }
  }
}
function onSnapshot(data) {
  if (!data) return;
  S.snap = data; S.lastSnapAt = Date.now();
  trackRoutes(); sampleMids();
  schedule();
}
function onTick(t) {
  if (!S.snap || !t) return;
  if (t.bookTickers) S.snap.bookTickers = t.bookTickers;
  if (t.orderBookDepth) S.snap.orderBookDepth = t.orderBookDepth;
  if (t.connectionStatus) S.snap.connectionStatus = t.connectionStatus;
  S.lastSnapAt = Date.now();
  sampleMids();
  if (S.page === 'scanner' || S.page === 'market' || S.drawer) schedule();
}
let pollTimer = null;
function startPolling() {
  S.transport = 'poll';
  if (pollTimer) return;
  const tick = async () => { try { onSnapshot(await api.get('/api/snapshot')); if (S.transport === 'down') S.transport = 'poll'; } catch { S.transport = 'down'; schedule(); } };
  tick(); pollTimer = setInterval(tick, 1000);
}
function startSignalR() {
  if (typeof signalR === 'undefined') return startPolling();
  try {
    const c = new signalR.HubConnectionBuilder().withUrl('/hubs/arbitrage').withAutomaticReconnect().build();
    c.on('Snapshot', onSnapshot); c.on('MarketTick', onTick);
    c.onreconnecting(() => { S.transport = 'poll'; startPolling(); schedule(); });
    c.onreconnected(() => { S.transport = 'signalr'; if (pollTimer) { clearInterval(pollTimer); pollTimer = null; } schedule(); });
    c.onclose(() => startPolling());
    c.start().then(() => { S.transport = 'signalr'; if (pollTimer) { clearInterval(pollTimer); pollTimer = null; } schedule(); }).catch(() => startPolling());
  } catch { startPolling(); }
  api.get('/api/snapshot').then(onSnapshot).catch(() => { });
}

// ───────────────────────── actions ─────────────────────────
const ACTIONS = {
  async 'toggle-pause'() { const r = await act(() => api.post('/api/control/toggle'), (r) => r.isPaused ? 'Сканирование на паузе' : 'Сканирование продолжено'); if (r && S.snap) { S.snap.isPaused = r.isPaused; schedule(true); } },
  async kill() {
    const r = await confirmModal({ title: 'Остановить live-торговлю?', body: 'Новые ордера будут запрещены сразу. Открытые позиции останутся на биржах.', confirm: 'Включить kill switch', danger: true, input: { label: 'Причина (необязательно)', placeholder: 'например, расхождение позиций' } });
    if (!r) return; await act(() => api.post('/api/live/kill', { reason: r.value || 'manual kill (UI)' }), 'Kill switch включён'); refreshSnap();
  },
  async 'live-enable'() {
    const r = await confirmModal({ title: 'Включить реальные ордера', body: 'Бот начнёт открывать хеджи на биржах в пределах лимитов live. Чтобы подтвердить, введите фразу <span class="num">ENABLE LIVE TRADING</span>.', confirm: 'Включить ордера', danger: true, input: { label: 'Фраза подтверждения', placeholder: 'ENABLE LIVE TRADING' } });
    if (!r) return; await act(() => api.post('/api/live/enable', { confirmPhrase: r.value, readOnly: false }), (x) => x?.message || 'Live включён'); refreshSnap();
  },
  async 'live-enable-ro'() {
    const r = await confirmModal({ title: 'Режим «только чтение»', body: 'Бот подключится к биржам по ключам, но ордеров не будет. Введите фразу <span class="num">ENABLE LIVE TRADING</span>.', confirm: 'Включить чтение', input: { label: 'Фраза подтверждения', placeholder: 'ENABLE LIVE TRADING' } });
    if (!r) return; await act(() => api.post('/api/live/enable', { confirmPhrase: r.value, readOnly: true }), (x) => x?.message || 'Режим чтения включён'); refreshSnap();
  },
  async 'kill-reset'() {
    if (!await confirmModal({ title: 'Снять kill switch?', body: 'Бот останется в paper. Сверьте позиции на биржах, затем включите live заново кнопкой «Включить реальные ордера».', confirm: 'Снять kill switch' })) return;
    await act(() => api.post('/api/live/disable'), (r) => r?.status?.killed ? (r.message || 'Kill switch не снят') : 'Kill switch снят, бот в paper. Можно включать live.');
    refreshSnap();
  },
  async 'live-disable'() { if (!await confirmModal({ title: 'Вернуться в paper?', body: 'Live будет выключен, бот продолжит работу на виртуальной марже.', confirm: 'Вернуться в paper' })) return; await act(() => api.post('/api/live/disable'), 'Live выключен, работает paper'); refreshSnap(); },
  'live-balances'() { loadLiveBalances(true); },
  async 'scalp-strategy'(el) {
    const t = S.settings?.trading; const k = el?.dataset?.strategy; if (!t || !k || t.scalpStrategy === k) return;
    const r = await act(() => api.post('/api/settings/trading', { ...t, scalpStrategy: k }), k === 'density' ? 'Скальпер: плотности в стакане (paper)' : 'Скальпер: отставание бирж');
    if (r) { await loadSettings(false); loadScalp(); }
  },
  async 'density-unblock'(el) {
    const sym = el?.dataset?.symbol; if (!sym) return;
    if (!await confirmModal({ title: `Вернуть ${baseOf(sym)} в торговлю?`, body: 'Счётчик убытков по монете начнётся заново.', confirm: 'Вернуть' })) return;
    await act(() => api.post('/api/scalp/density/blacklist/remove', { symbol: sym }), `${baseOf(sym)} снова торгуется`); loadScalp();
  },
  async 'density-reset'() {
    if (!await confirmModal({ title: 'Очистить paper-историю плотностей?', body: 'Удалит paper-сделки и счётчики заявок. Реальные сделки останутся.', confirm: 'Очистить' })) return;
    await act(() => api.post('/api/scalp/density/reset'), (r) => `Удалено сделок: ${r?.removed ?? 0}`); loadScalp();
  },
  async 'scalp-toggle'() {
    const t = S.settings?.trading; if (!t) return;
    const on = !(S.scalp?.enabled ?? t.spatialScalpMode);
    const r = await act(() => api.post('/api/settings/trading', { ...t, spatialScalpMode: on }), on ? 'Скальпер включён' : 'Скальпер выключен');
    if (r) { await loadSettings(false); loadScalp(); }
  },
  async 'scalp-reset'() {
    if (!await confirmModal({ title: 'Очистить paper-историю скальпера?', body: 'Удалятся закрытые paper-сделки скальпера, в том числе сделки прежнего сигнала. Live-история и открытые позиции не затрагиваются.', confirm: 'Очистить', danger: true })) return;
    await act(() => api.post('/api/scalp/reset-paper'), (r) => `Удалено сделок: ${r?.removed ?? 0}`); loadScalp();
  },
  async 'live-positions'() { const r = await act(() => api.get('/api/live/positions')); if (r) { S.livePositions = r; schedule(true); } },
  async 'paper-close'(el) { if (!await confirmModal({ title: `Закрыть хедж ${baseOf(el.dataset.sym)}?`, body: 'Обе ноги закроются по текущим стаканам с комиссией taker.', confirm: 'Закрыть хедж' })) return; await act(() => api.post('/api/paper/close/' + el.dataset.id), (r) => `Хедж закрыт: ${fmt.usd(r?.realizedPnlUsd ?? r?.trade?.realizedPnlUsd)}`); refreshSnap(); },
  async 'paper-close-all'() { if (!await confirmModal({ title: 'Закрыть все paper-хеджи?', body: 'Все открытые позиции закроются по текущим ценам.', confirm: 'Закрыть все', danger: true })) return; await act(() => api.post('/api/paper/close-all'), 'Все хеджи закрыты'); refreshSnap(); },
  async 'live-close'(el) { if (!await confirmModal({ title: `Закрыть live-хедж ${baseOf(el.dataset.sym)}?`, body: 'Бот отправит реальные ордера на закрытие обеих ног.', confirm: 'Закрыть на бирже', danger: true })) return; await act(() => api.post('/api/live/close/' + el.dataset.id), (r) => r?.ok === false ? (r.error || 'Не закрыто') : 'Ордера на закрытие отправлены'); refreshSnap(); },
  async 'paper-reset'() { if (!await confirmModal({ title: 'Сбросить paper-кошелёк?', body: 'Балансы вернутся к стартовым, открытые paper-позиции исчезнут. Журнал на диске останется.', confirm: 'Сбросить', danger: true })) return; await act(() => api.post('/api/paper/reset'), 'Paper-кошелёк сброшен'); refreshSnap(); },
  async prune() { await act(() => api.post('/api/paper/prune-orphans'), (r) => `Закрыто позиций: ${r?.pruned ?? r?.closed ?? 0}`); refreshSnap(); },
  'to-terminal'(el) { closeDrawer(); S.mk.sym = el.dataset.sym; S.mk.longEx = el.dataset.l; S.mk.shortEx = el.dataset.s; S.mk.bookEx = el.dataset.l; S.mk.venuesKey = ''; go('market'); },
  'mk-best'() { const br = bestRoute(S.mk.sym); if (br) { S.mk.longEx = br.longEx; S.mk.shortEx = br.shortEx; S.mk.venuesKey = ''; schedule(true); } },
  async 'mk-open'() {
    const m = S.mk; const live = modeOf().key === 'live';
    const size = num(m.size), lev = num(m.lev) || 2;
    if (!(size > 0)) return toast('Укажите размер ноги', 'err');
    const ok = await confirmModal({ title: live ? 'Открыть хедж реальными ордерами?' : 'Открыть paper-хедж?', body: `${esc(baseOf(m.sym))}: long на ${esc(venue(m.longEx))}, short на ${esc(venue(m.shortEx))}, по ${fmt.usd(size, 0, false)} на ногу${live ? `, плечо ${lev}x. Ордера уйдут на биржи сразу.` : '. Лимиты риска paper продолжают действовать.'}`, confirm: live ? 'Отправить ордера' : 'Открыть хедж', danger: live });
    if (!ok) return;
    const body = { symbol: m.sym, longExchange: m.longEx, shortExchange: m.shortEx, notionalUsd: size, leverage: lev };
    const r = await act(() => api.post(live ? '/api/live/hedge' : '/api/paper/hedge', body));
    if (!r) return;
    if (r.ok) { toast(live ? 'Ордера отправлены, хедж открыт' : 'Paper-хедж открыт'); refreshSnap(); }
    else toast('Не открыт: ' + (r.error || r.message || r.status || 'причина не указана'), 'err');
  },
  'perf-refresh'() { loadPerf(); loadLedger(); },
  'funding-refresh'() { loadFunding(); },
  'settings-discard'() { settingsDraft.dirty.clear(); settingsDraft.values = {}; $('#page').dataset.redraw = '1'; schedule(true); },
  async 'settings-save'() {
    const t = S.settings?.trading; if (!t) return;
    const body = { ...t };
    for (const k of settingsDraft.dirty) body[k] = settingsDraft.values[k];
    const r = await act(() => api.post('/api/settings/trading', body), 'Настройки сохранены и применены');
    if (r) { settingsDraft.dirty.clear(); settingsDraft.values = {}; await loadSettings(true); }
  },
  async 'key-clear'(el) { const ex = el.closest('form').dataset.ex; if (!await confirmModal({ title: `Удалить ключ ${venue(ex)}?`, body: 'Ключ и секрет будут стёрты из локальных настроек.', confirm: 'Удалить ключ', danger: true })) return; await act(() => api.del('/api/settings/exchanges/' + encodeURIComponent(ex)), 'Ключ удалён'); loadSettings(true); },
};
async function refreshSnap() { try { onSnapshot(await api.get('/api/snapshot')); } catch { } }

function wire() {
  document.addEventListener('click', (e) => {
    const goEl = e.target.closest('[data-go]'); if (goEl) { go(goEl.dataset.go); return; }
    const vc = e.target.closest('[data-venue-k]');
    if (vc && S.settings?.trading) {
      const k = vc.dataset.venueK, t = S.settings.trading;
      const cur = settingsDraft.dirty.has(k) ? settingsDraft.values[k] : (t[k] ?? '');
      const set = new Set(String(cur).split(',').map(x => x.trim()).filter(Boolean));
      set.has(vc.dataset.venue) ? set.delete(vc.dataset.venue) : set.add(vc.dataset.venue);
      const v = [...VENUES_ALL.filter(x => set.has(x)), ...[...set].filter(x => !VENUES_ALL.includes(x))].join(',');
      if ((t[k] ?? '') === v) settingsDraft.dirty.delete(k); else { settingsDraft.values[k] = v; settingsDraft.dirty.add(k); }
      $('#page').dataset.redraw = '1'; schedule(true); syncSavebar(); return;
    }
    const a = e.target.closest('[data-act]'); if (a && ACTIONS[a.dataset.act]) { e.preventDefault(); ACTIONS[a.dataset.act](a); return; }
    const sort = e.target.closest('[data-sort]'); if (sort) { const k = sort.dataset.sort; if (S.market.sort === k) S.market.dir *= -1; else { S.market.sort = k; S.market.dir = -1; } schedule(true); return; }
    const days = e.target.closest('[data-days]'); if (days) { S.perfDays = Number(days.dataset.days); S.perf = null; schedule(true); loadPerf(); return; }
    const ts = e.target.closest('[data-tsrc]'); if (ts) { S.trades.src = ts.dataset.tsrc; if (ts.dataset.tsrc === 'all' && !S.ledger) loadLedger(); schedule(true); return; }
    const row = e.target.closest('tr[data-route]'); if (row) { openDrawer(row.dataset.route); return; }
    const mks = e.target.closest('[data-mk-sym]'); if (mks) { S.mk.sym = mks.dataset.mkSym; S.mk.longEx = S.mk.shortEx = S.mk.bookEx = null; S.mk.venuesKey = ''; ensureMk(); schedule(true); return; }
    const mkl = e.target.closest('[data-mk-long]'); if (mkl) { const ex = mkl.dataset.mkLong; if (S.mk.shortEx === ex) S.mk.shortEx = S.mk.longEx; S.mk.longEx = ex; S.mk.venuesKey = ''; schedule(true); return; }
    const mksh = e.target.closest('[data-mk-short]'); if (mksh) { const ex = mksh.dataset.mkShort; if (S.mk.longEx === ex) S.mk.longEx = S.mk.shortEx; S.mk.shortEx = ex; S.mk.venuesKey = ''; schedule(true); return; }
    const mkb = e.target.closest('[data-mk-book]'); if (mkb) { S.mk.bookEx = mkb.dataset.mkBook; schedule(true); return; }
    const mkv = e.target.closest('[data-mk-view]'); if (mkv) { S.mk.view = mkv.dataset.mkView; $$('[data-mk-view]').forEach(b => b.setAttribute('aria-pressed', String(b === mkv))); schedule(true); return; }
    const toc = e.target.closest('.toc a'); if (toc) { e.preventDefault(); document.querySelector(toc.getAttribute('href'))?.scrollIntoView({ behavior: 'smooth', block: 'start' }); }
  });
  document.addEventListener('keydown', (e) => {
    const tag = (e.target.tagName || '').toLowerCase(); const typing = tag === 'input' || tag === 'textarea' || tag === 'select';
    if (e.key === 'Escape' && S.drawer) { closeDrawer(); return; }
    if (e.key === 'Enter' && e.target.matches?.('tr[data-route]')) { openDrawer(e.target.dataset.route); return; }
    if (typing || e.metaKey || e.ctrlKey || e.altKey) return;
    if (/^[1-9]$/.test(e.key)) { const k = Object.keys(PAGES)[Number(e.key) - 1]; if (k) go(k); }
    if (e.key === '/') { e.preventDefault(); if (S.page !== 'signals') go('signals'); setTimeout(() => $('#mq')?.focus(), 400); }
  });
  document.addEventListener('input', (e) => {
    const t = e.target;
    if (t.id === 'mq') { S.market.q = t.value; schedule(); }
    if (t.id === 'sq') { S.scanner.q = t.value; schedule(); }
    if (t.id === 'mkSize') { S.mk.size = parseFloat(t.value.replace(',', '.')); schedule(); }
    if (t.id === 'mkLev') { S.mk.lev = parseFloat(t.value.replace(',', '.')); schedule(); }
    if (t.dataset.k && t.type !== 'checkbox') {
      const k = t.dataset.k;
      if (t.dataset.t === 'text') {
        const v = t.value.trim();
        if ((S.settings.trading[k] ?? '') === v) { settingsDraft.dirty.delete(k); t.classList.remove('dirty'); }
        else { settingsDraft.values[k] = v; settingsDraft.dirty.add(k); t.classList.add('dirty'); }
        syncSavebar(); return;
      }
      const raw = t.value.replace(',', '.');
      let v = t.dataset.t === 'int' ? parseInt(raw, 10) : parseFloat(raw);
      if (k === 'scalpLeverage' && Number.isFinite(v)) v = Math.min(5, Math.max(1, v));
      if (raw === '' || !Number.isFinite(v)) { t.classList.add('dirty'); return; }
      if (String(S.settings.trading[k]) === String(v)) { settingsDraft.dirty.delete(k); t.classList.remove('dirty'); }
      else { settingsDraft.values[k] = v; settingsDraft.dirty.add(k); t.classList.add('dirty'); }
      syncSavebar();
    }
  });
  document.addEventListener('change', (e) => {
    const t = e.target;
    if (t.id === 'mexec') { S.market.onlyExec = t.checked; schedule(true); }
    if (t.id === 'mkLong') { if (t.value === S.mk.shortEx) S.mk.shortEx = S.mk.longEx; S.mk.longEx = t.value; S.mk.venuesKey = ''; schedule(true); }
    if (t.id === 'mkShort') { if (t.value === S.mk.longEx) S.mk.longEx = S.mk.shortEx; S.mk.shortEx = t.value; S.mk.venuesKey = ''; schedule(true); }
    if (t.dataset.k && t.type === 'checkbox') {
      const k = t.dataset.k;
      if (!!S.settings.trading[k] === t.checked) settingsDraft.dirty.delete(k); else { settingsDraft.values[k] = t.checked; settingsDraft.dirty.add(k); }
      const lbl = t.parentElement.querySelector('.small'); if (lbl) lbl.textContent = t.checked ? 'включено' : 'выключено';
      syncSavebar();
    }
  });
  document.addEventListener('submit', async (e) => {
    const f = e.target.closest('form.keycard'); if (!f) return; e.preventDefault();
    const ex = f.dataset.ex; const fd = new FormData(f);
    const body = { enabled: true, apiKey: fd.get('apiKey')?.trim(), apiSecret: fd.get('apiSecret')?.trim(), passphrase: fd.get('passphrase')?.trim() || null, permission: fd.get('permission') };
    if (!body.apiKey || !body.apiSecret) { toast('Нужны и API key, и secret', 'err'); return; }
    const r = await act(() => api.post('/api/settings/exchanges/' + encodeURIComponent(ex), body), `Ключ ${venue(ex)} сохранён`);
    if (r) { f.reset(); loadSettings(true); }
  });
  $('#scrim').addEventListener('click', closeDrawer);
  $('#drawerClose').addEventListener('click', closeDrawer);
  window.addEventListener('hashchange', () => { const p = location.hash.replace(/^#\/?/, ''); if (p && p !== S.page) go(p); });
  window.addEventListener('beforeunload', (e) => { if (settingsDraft.dirty.size) { e.preventDefault(); e.returnValue = ''; } });
}
function syncSavebar() { const b = $('#savebar'); if (!b) return; b.classList.toggle('on', settingsDraft.dirty.size > 0); const n = $('#dirtyN'); if (n) n.textContent = settingsDraft.dirty.size; }

let lastFocus = null;
function openDrawer(route) { lastFocus = document.activeElement; S.drawer = { route }; $('#drawer').classList.add('open'); $('#scrim').classList.add('open'); $('#drawer').setAttribute('aria-hidden', 'false'); renderDrawer(); $('#drawerClose').focus(); }
function closeDrawer() { S.drawer = null; $('#drawer').classList.remove('open'); $('#scrim').classList.remove('open'); $('#drawer').setAttribute('aria-hidden', 'true'); if (lastFocus) lastFocus.focus(); }

// periodic background loads
setInterval(() => { if (S.page === 'funding') loadFunding(); if ((S.page === 'live' || S.page === 'balances') && S.liveBalances) loadLiveBalances(true); }, 30_000);
setInterval(() => { if (['scalp', 'balances', 'overview'].includes(S.page)) loadScalp(); }, 2000);
setInterval(() => { if (S.page !== 'settings') schedule(); }, 1000); // keep "x s ago" labels honest

// ───────────────────────── boot ─────────────────────────
function boot() {
  const p = location.hash.replace(/^#\/?/, '');
  S.page = PAGES[p] ? p : 'overview';
  renderNav(); wire();
  loadSettings(false).then(() => schedule(true));
  startSignalR();
  onEnter(S.page);
  schedule(true);
}
document.readyState === 'loading' ? document.addEventListener('DOMContentLoaded', boot) : boot();
})();
