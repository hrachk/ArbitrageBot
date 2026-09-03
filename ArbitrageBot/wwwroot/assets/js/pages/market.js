// ── Market Terminal — full production UI ─────────────────────────────────────
// Real order book from WS data, real hedge execution via /api/paper/hedge
// and /api/live/hedge, TradingView lightweight chart, live arb scanner.

AB.pages.market = {
  selected: '',
  selectedEx: '',
  side: 'long',
  lev: 3,
  interval: '15',
  overlayHist: {},
  overlayMaxPts: 300,
  overlayColors: ['#38bdf8', '#2dd4bf', '#a78bfa', '#f0b429', '#f472b6', '#34d399'],
  _bound: false,
  _chart: null,
  _chartSeries: {},    // ex → LineSeries
  _tvContainer: null,
  _lastFundingLoad: 0,

  // ── Entry point from SignalR ──────────────────────────────────────────────
  render(data) {
    if (!data) return;
    const mode = (data.mode || 'PAPER').toUpperCase();
    const isLive = mode.includes('LIVE');
    const canOrder = mode === 'LIVE';

    // Symbols from discovery
    const symbols = (data.symbols || []).length
      ? data.symbols
      : Object.keys(data.bookTickers || {});
    if (!this.selected && symbols[0]) this.selected = symbols[0];
    if (this.selected && symbols.length && !symbols.includes(this.selected))
      this.selected = symbols[0];

    // Top bar
    const pill = document.getElementById('m_modePill');
    if (pill) {
      pill.textContent = canOrder ? '🔴 LIVE' : isLive ? '🟡 RO' : '📄 PAPER';
      pill.className = 'mode-pill' + (canOrder ? ' live' : isLive ? ' ro' : ' paper');
    }
    const sc = document.getElementById('m_scanStat');
    if (sc) sc.textContent = 'scan #' + (data.scanCount != null ? Number(data.scanCount).toLocaleString() : '—');
    const exLive = document.getElementById('m_exLive');
    if (exLive) exLive.textContent = (data.exchanges || []).length + ' ex';

    this.renderSymTabs(symbols, data);
    this.renderExTabs(data);
    this.renderOrderBook(data);
    this.renderArbScanner(data);
    this.renderPositions(data);
    this.renderCompare(data);
    this.paintOverlayChart(data);
    this.updateTradePanel(data);

    // Funding: load once per minute max
    if (Date.now() - this._lastFundingLoad > 60000) {
      this._lastFundingLoad = Date.now();
      this.renderFundingStrip();
    }

    this.bindOnce(data);
  },

  // ── Symbol tabs ───────────────────────────────────────────────────────────
  renderSymTabs(symbols, data) {
    const el = document.getElementById('m_symTabs');
    if (!el) return;
    const show = symbols.slice(0, 14);
    el.innerHTML = show.map(s => {
      const act = s === this.selected;
      // Check if this sym has an executable opportunity
      const opps = Array.isArray(data.opportunities) ? data.opportunities : [];
      const hot  = opps.some(o => o.symbol === s && o.isExecutable);
      return `<button type="button" class="sym-btn${act ? ' active' : ''}" data-sym="${s}">
        ${s.replace(/USDT$/i, '')}${hot ? '<span style="color:var(--green);font-size:8px;margin-left:2px">●</span>' : ''}
      </button>`;
    }).join('');
    el.querySelectorAll('.sym-btn').forEach(btn => {
      btn.onclick = () => {
        this.selected = btn.getAttribute('data-sym');
        this.overlayHist = {};
        this._chartSeries = {};
        this.render(AB.state.snapshot || {});
      };
    });
  },

  // ── Exchange tabs (order book selector) ───────────────────────────────────
  renderExTabs(data) {
    const el = document.getElementById('m_exTabs');
    if (!el) return;
    const books = this.booksFor(data, this.selected);
    let venues = Object.keys(books);
    if (!venues.length) venues = data.exchanges || ['Binance', 'Bybit', 'OKX', 'Bitget'];
    if (!this.selectedEx || !venues.includes(this.selectedEx)) this.selectedEx = venues[0];
    const short = { Binance: 'BN', Bybit: 'BY', OKX: 'OK', Bitget: 'BG', GateIo: 'GT' };
    el.innerHTML = venues.map(ex => `
      <button type="button" class="ex-tab${ex === this.selectedEx ? ' active' : ''}" data-ex="${ex}">
        ${short[ex] || ex.slice(0, 2)}
      </button>`).join('');
    el.querySelectorAll('.ex-tab').forEach(btn => {
      btn.onclick = () => {
        this.selectedEx = btn.getAttribute('data-ex');
        el.querySelectorAll('.ex-tab').forEach(b => b.classList.toggle('active', b === btn));
        this.renderOrderBook(AB.state.snapshot || {});
      };
    });
    // Populate exchange select in trade panel
    const sel = document.getElementById('m_tradeEx');
    if (sel) {
      const prev = sel.value;
      sel.innerHTML = venues.map(ex =>
        `<option value="${ex}"${ex === (prev || this.selectedEx) ? ' selected' : ''}>${ex}</option>`
      ).join('');
    }
    // Short exchange select
    const sel2 = document.getElementById('m_tradeExShort');
    if (sel2) {
      const prev2 = sel2.value;
      sel2.innerHTML = venues.map(ex =>
        `<option value="${ex}"${ex === prev2 ? ' selected' : ''}>${ex}</option>`
      ).join('');
    }
  },

  // ── Book data helpers ─────────────────────────────────────────────────────
  booksFor(data, sym) {
    let books = (data.bookTickers || {})[sym] || {};
    if (!Object.keys(books).length && data.bookTickers) {
      const key = Object.keys(data.bookTickers).find(k =>
        k.toUpperCase() === (sym || '').toUpperCase());
      if (key) books = data.bookTickers[key] || {};
    }
    if (!Object.keys(books).length && data.orderBookDepth) {
      const depth = data.orderBookDepth[sym] || {};
      const synth = {};
      Object.keys(depth).forEach(ex => {
        const d = depth[ex] || {};
        const bids = d.bids || d.Bids || [];
        const asks = d.asks || d.Asks || [];
        const bid = Number((bids[0] && (bids[0][0] ?? bids[0].price)) || 0);
        const ask = Number((asks[0] && (asks[0][0] ?? asks[0].price)) || 0);
        if (bid > 0 && ask > 0) synth[ex] = { bestBid: bid, bestAsk: ask };
      });
      books = synth;
    }
    return books;
  },

  depthFor(data, sym, ex) {
    const root = data.orderBookDepth || {};
    let depth = root[sym] || {};
    if (!Object.keys(depth).length) {
      const key = Object.keys(root).find(k => k.toUpperCase() === (sym || '').toUpperCase());
      if (key) depth = root[key] || {};
    }
    return depth[ex] ||
      depth[Object.keys(depth).find(k => k.toLowerCase() === (ex || '').toLowerCase()) || ''] || null;
  },

  // ── Order book ────────────────────────────────────────────────────────────
  renderOrderBook(data) {
    const sym = this.selected;
    const ex  = this.selectedEx;
    const obSymEl = document.getElementById('obSym');
    if (obSymEl) obSymEl.textContent = sym || '—';

    const depth = this.depthFor(data, sym, ex);
    let asks = [], bids = [];
    if (depth) {
      asks = (depth.asks || depth.Asks || []).slice(0, 12).map(r => ({
        pr: Number(r[0] ?? r.price), q: Number(r[1] ?? r.quantity ?? r.qty)
      })).filter(x => x.pr > 0 && x.q > 0);
      bids = (depth.bids || depth.Bids || []).slice(0, 12).map(r => ({
        pr: Number(r[0] ?? r.price), q: Number(r[1] ?? r.quantity ?? r.qty)
      })).filter(x => x.pr > 0 && x.q > 0);
    }

    const books = this.booksFor(data, sym);
    const top = books[ex] || Object.values(books)[0];
    let mid = 0, spd = 0, spdPct = 0;
    if (top) {
      const bid = Number(top.bestBid ?? top.BestBid ?? 0);
      const ask = Number(top.bestAsk ?? top.BestAsk ?? 0);
      if (bid > 0 && ask > 0) { mid = (bid + ask) / 2; spd = ask - bid; spdPct = spd / ask * 100; }
    }
    if (!mid && asks.length && bids.length) {
      mid = (asks[0].pr + bids[0].pr) / 2;
      spd = asks[0].pr - bids[0].pr;
      spdPct = spd / asks[0].pr * 100;
    }

    const maxQ = Math.max(1, ...asks.map(a => a.q), ...bids.map(b => b.q));

    const asksEl = document.getElementById('ob-asks');
    if (asksEl) {
      const rows = asks.slice(0, 10).reverse();
      asksEl.innerHTML = rows.length ? rows.map(r => {
        const pct = Math.min(95, (r.q / maxQ) * 100);
        return `<div class="ob-row">
          <div class="ob-bar ask-bar" style="width:${pct.toFixed(0)}%"></div>
          <span class="price" style="color:var(--red)">${this.fmtP(r.pr)}</span>
          <span class="qty">${this.fmtQ(r.q)}</span>
          <span class="total">${this.fmtQ(r.pr * r.q)}</span>
        </div>`;
      }).join('') : '<div class="empty" style="padding:8px;color:var(--t3)">No asks</div>';
    }

    const bidsEl = document.getElementById('ob-bids');
    if (bidsEl) {
      bidsEl.innerHTML = bids.slice(0, 10).length ? bids.slice(0, 10).map(r => {
        const pct = Math.min(95, (r.q / maxQ) * 100);
        return `<div class="ob-row">
          <div class="ob-bar bid-bar" style="width:${pct.toFixed(0)}%"></div>
          <span class="price" style="color:var(--green)">${this.fmtP(r.pr)}</span>
          <span class="qty">${this.fmtQ(r.q)}</span>
          <span class="total">${this.fmtQ(r.pr * r.q)}</span>
        </div>`;
      }).join('') : '<div class="empty" style="padding:8px;color:var(--t3)">No bids</div>';
    }

    const midEl = document.getElementById('ob-mid-price');
    const spdEl = document.getElementById('ob-spread');
    if (midEl) {
      midEl.textContent = mid ? this.fmtP(mid) : '—';
      midEl.style.color = 'var(--green)';
    }
    if (spdEl) spdEl.textContent = spd ? `spd ${this.fmtP(spd)} (${spdPct.toFixed(3)}%)` : 'spd —';

    const cp = document.getElementById('chartPrice');
    if (cp && mid) cp.textContent = this.fmtP(mid);
    const pi = document.getElementById('priceIn');
    if (pi && mid) pi.value = this.fmtP(mid);
    const ml = document.getElementById('m_markLbl');
    if (ml) ml.textContent = 'Mark: ' + (mid ? this.fmtP(mid) : '—');

    // Bid/ask depth summary bar
    const bidVol = bids.reduce((s, r) => s + r.q * r.pr, 0);
    const askVol = asks.reduce((s, r) => s + r.q * r.pr, 0);
    const total  = bidVol + askVol;
    const bidPct = total > 0 ? (bidVol / total * 100).toFixed(1) : 50;
    const depthBar = document.getElementById('m_depthBar');
    const depthLbl = document.getElementById('m_depthLbl');
    if (depthBar) depthBar.style.width = bidPct + '%';
    if (depthLbl) depthLbl.textContent =
      `Bid ${this.fmtQ(bidVol)} USDT  /  Ask ${this.fmtQ(askVol)} USDT`;
  },

  // ── Multi-exchange price compare strip ────────────────────────────────────
  renderCompare(data) {
    const el = document.getElementById('exCompare');
    if (!el) return;
    const books = this.booksFor(data, this.selected);
    const cols  = this.overlayColors;
    const venues = Object.keys(books);
    if (!venues.length) { el.innerHTML = ''; return; }

    let minMid = Infinity, maxMid = -Infinity;
    const mids = {};
    venues.forEach(ex => {
      const b = books[ex];
      const m = (Number(b.bestBid ?? 0) + Number(b.bestAsk ?? 0)) / 2;
      if (m > 0) { mids[ex] = m; minMid = Math.min(minMid, m); maxMid = Math.max(maxMid, m); }
    });

    el.innerHTML = venues.map((ex, i) => {
      const m = mids[ex] || 0;
      const c = cols[i % cols.length];
      const isBest = m === minMid && venues.length > 1;
      return `<span class="ex-badge" style="background:${c}22;color:${c};${isBest ? 'border:1px solid '+c : ''}">
        ${ex.slice(0, 2).toUpperCase()} ${m ? this.fmtP(m) : '—'}
      </span>`;
    }).join('');

    // Live arb edge for selected symbol
    const edgeEl  = document.getElementById('liveEdge');
    const routeEl = document.getElementById('m_liveRoute');
    const oneClick = document.getElementById('m_oneClick');

    if (Object.keys(mids).length >= 2) {
      const loEx = venues.find(ex => mids[ex] === minMid);
      const hiEx = venues.find(ex => mids[ex] === maxMid);
      const edge = minMid > 0 ? ((maxMid - minMid) / minMid * 100) : 0;
      const minPct = Number(data.minProfitPercent) || 0.1;

      if (edgeEl) {
        edgeEl.textContent = (edge >= 0 ? '+' : '') + edge.toFixed(3) + '%';
        edgeEl.style.color = edge >= minPct ? 'var(--green)' : edge >= minPct * 0.5 ? 'var(--amber)' : 'var(--t3)';
      }
      if (routeEl) routeEl.textContent = (loEx || '?') + ' LONG → ' + (hiEx || '?') + ' SHORT';

      // Enable/disable 1-click button
      if (oneClick) {
        const canClick = edge >= minPct * 0.5;
        oneClick.disabled = !canClick;
        oneClick.setAttribute('data-long-ex', loEx || '');
        oneClick.setAttribute('data-short-ex', hiEx || '');
        oneClick.title = canClick
          ? `Open hedge: ${loEx} LONG / ${hiEx} SHORT — ${edge.toFixed(3)}% edge`
          : `Edge ${edge.toFixed(3)}% below threshold ${minPct}%`;
      }
    } else {
      if (edgeEl) edgeEl.textContent = '—';
      if (routeEl) routeEl.textContent = 'Need ≥2 exchanges';
    }
  },

  // ── Arb scanner list ──────────────────────────────────────────────────────
  renderArbScanner(data) {
    const el  = document.getElementById('arbList');
    const cnt = document.getElementById('arbCount');
    if (!el) return;
    const opps = Array.isArray(data.opportunities) ? data.opportunities.slice() : [];
    opps.sort((a, b) =>
      (Number(b.netSpreadPercent ?? b.netProfitPercent) || 0) -
      (Number(a.netSpreadPercent ?? a.netProfitPercent) || 0));
    const minPct = Number(data.minProfitPercent) || 0.1;
    const exec = opps.filter(o => o.isExecutable).length;
    if (cnt) cnt.textContent = exec + ' executable';

    if (!opps.length) {
      el.innerHTML = '<div class="empty" style="padding:10px;color:var(--t3)">No routes ≥ threshold</div>';
      return;
    }
    el.innerHTML = opps.slice(0, 30).map(o => {
      const net  = Number(o.netSpreadPercent ?? o.netProfitPercent) || 0;
      const rt   = Number(o.netRoundTripPercent) || 0;
      const fund = Number(o.expectedFundingPercent ?? o.netAfterFundingPercent) || 0;
      const hot  = o.isExecutable === true || net >= minPct;
      const longEx  = o.longExchange  || o.buyExchange  || '?';
      const shortEx = o.shortExchange || o.sellExchange || '?';
      const sym  = (o.symbol || '').replace(/USDT$/i, '');
      const col  = net >= 0.3 ? 'var(--green)' : net >= 0.1 ? 'var(--amber)' : 'var(--t3)';
      return `<div class="arb-row${hot ? ' hot' : ''}" data-sym="${o.symbol || ''}">
        <div>
          <div class="arb-sym">${sym}</div>
          <div class="arb-route">${longEx} → ${shortEx}</div>
        </div>
        <div style="text-align:right">
          <div class="arb-edge" style="color:${col}">${net >= 0 ? '+' : ''}${net.toFixed(3)}%</div>
          <div style="font-size:8px;color:var(--t3);font-family:var(--mono)">
            RT ${rt >= 0 ? '+' : ''}${rt.toFixed(3)}%${fund ? ' · F+' + fund.toFixed(3) + '%' : ''}
          </div>
        </div>
        <div style="text-align:center">
          ${hot
            ? `<button type="button" class="arb-exec" data-pick="${o.symbol || ''}"
                data-long="${longEx}" data-short="${shortEx}">Exec</button>`
            : '<span style="color:var(--t3);font-size:9px">below</span>'}
        </div>
        <div></div>
      </div>`;
    }).join('');

    // Click row → switch symbol
    el.querySelectorAll('.arb-row').forEach(row => {
      row.onclick = (e) => {
        if (e.target.closest('.arb-exec')) return;
        const s = row.getAttribute('data-sym');
        if (!s) return;
        this.selected = s;
        this.overlayHist = {};
        this._chartSeries = {};
        this.render(AB.state.snapshot || {});
      };
    });

    // Exec button → fill trade panel and focus
    el.querySelectorAll('.arb-exec').forEach(btn => {
      btn.onclick = (e) => {
        e.stopPropagation();
        const sym   = btn.getAttribute('data-pick');
        const longE = btn.getAttribute('data-long');
        const shortE= btn.getAttribute('data-short');
        this.selected = sym;
        this.overlayHist = {};
        this._chartSeries = {};
        // Pre-fill trade panel
        const longSel  = document.getElementById('m_tradeEx');
        const shortSel = document.getElementById('m_tradeExShort');
        if (longSel)  { for (let o of longSel.options)  o.selected = o.value === longE; }
        if (shortSel) { for (let o of shortSel.options) o.selected = o.value === shortE; }
        // Flash exec button
        const eb = document.getElementById('execBtn');
        if (eb) { eb.classList.add('flash'); setTimeout(() => eb.classList.remove('flash'), 600); }
        this.render(AB.state.snapshot || {});
        document.getElementById('sizeIn')?.focus();
      };
    });
  },

  // ── Open positions ────────────────────────────────────────────────────────
  renderPositions(data) {
    const el = document.getElementById('posTable');
    if (!el) return;
    const fp     = data.futuresPaper || data.paper || {};
    const paper  = Array.isArray(fp.positions) ? fp.positions : [];
    const live   = data.livePositions || {};
    const ledger = Array.isArray(live.ledger) ? live.ledger
                 : Array.isArray(live.open)   ? live.open : [];

    const rows = [];
    paper.forEach(p => rows.push({
      sym:    p.symbol || p.Symbol,
      long:   p.longExchange,
      short:  p.shortExchange,
      entry:  Number(p.longEntry ?? 0),
      upnl:   Number(p.unrealizedPnlUsd ?? p.unrealizedPnl ?? 0),
      type:   p.positionType || 'Paper',
      hold:   p.lastHoldDecision,
      id:     p.tradeId || p.id,
      source: 'paper'
    }));
    ledger.forEach(p => rows.push({
      sym:    p.symbol || p.Symbol,
      long:   p.longExchange || p.LongExchange,
      short:  p.shortExchange || p.ShortExchange,
      entry:  Number(p.longEntry ?? p.LongEntry ?? 0),
      upnl:   Number(p.unrealizedPricePnlUsd ?? p.unrealizedPnl ?? 0),
      type:   'LIVE',
      hold:   p.shouldHold ? 'HOLD' : p.shouldHold === false ? 'CLOSE' : null,
      id:     p.tradeId || p.id,
      source: 'live'
    }));

    const sub = document.getElementById('m_posSub');
    if (sub) sub.textContent = rows.length + ' open';
    const realEl = document.getElementById('m_posRealized');
    if (realEl) {
      const r = Number(live.realizedPnlUsd ?? fp.realizedPnlUsd ?? fp.realizedPnl) || 0;
      realEl.textContent = (r >= 0 ? '+$' : '-$') + Math.abs(r).toFixed(2);
      realEl.style.color = r >= 0 ? 'var(--green)' : 'var(--red)';
    }

    if (!rows.length) {
      el.innerHTML = '<div class="empty" style="padding:10px;color:var(--t3)">No open hedges</div>';
      return;
    }
    el.innerHTML = rows.map(p => {
      const holdBg = p.hold === 'HOLD'
        ? 'rgba(45,212,191,.15);color:var(--accent)'
        : p.hold === 'CLOSE'
          ? 'rgba(248,113,113,.15);color:var(--red)'
          : 'transparent';
      const typeBadge = p.source === 'live'
        ? '<span style="font-size:8px;padding:1px 4px;border-radius:3px;background:rgba(248,113,113,.15);color:var(--red)">LIVE</span>'
        : '<span style="font-size:8px;padding:1px 4px;border-radius:3px;background:rgba(56,189,248,.1);color:var(--t2)">PAPER</span>';
      return `<div class="pos-row" style="grid-template-columns:60px 36px 52px 48px 28px 40px">
        <span class="pos-sym">${String(p.sym || '').replace(/USDT$/i, '')}</span>
        ${typeBadge}
        <span class="mono" style="color:var(--t2);font-size:10px">${p.entry ? this.fmtP(p.entry) : '—'}</span>
        <span class="mono" style="color:${p.upnl >= 0 ? 'var(--green)' : 'var(--red)'};font-size:10px">
          ${p.upnl >= 0 ? '+' : ''}${p.upnl.toFixed(2)}
        </span>
        ${p.hold ? `<span style="font-size:8px;padding:1px 4px;border-radius:3px;background:${holdBg}">${p.hold.slice(0,1)}</span>` : '<span></span>'}
        <button type="button" class="close-btn" data-close="${p.id || ''}" data-src="${p.source}">✕</button>
      </div>`;
    }).join('');

    el.querySelectorAll('[data-close]').forEach(btn => {
      btn.onclick = async (e) => {
        e.stopPropagation();
        const id  = btn.getAttribute('data-close');
        const src = btn.getAttribute('data-src');
        if (!id) return;
        btn.textContent = '…'; btn.disabled = true;
        try {
          const url = src === 'live' ? '/api/live/close/' + id : '/api/paper/close/' + id;
          const r = await AB.api.post(url);
          if (AB.refreshSnapshot) AB.refreshSnapshot();
          this.showToast(r.ok !== false ? '✓ Position closed' : '✗ ' + (r.error || r.message || 'failed'));
        } catch (err) {
          this.showToast('✗ ' + (err.message || err));
          btn.textContent = '✕'; btn.disabled = false;
        }
      };
    });
  },

  // ── Funding rates strip ───────────────────────────────────────────────────
  async renderFundingStrip() {
    const el = document.getElementById('fundTable');
    if (!el) return;
    try {
      const rows = await AB.api.get('/api/funding');
      const list = Array.isArray(rows) ? rows : [];
      if (!list.length) {
        el.innerHTML = '<div class="empty" style="padding:8px;color:var(--t3)">Funding rates loading…</div>';
        return;
      }
      const maxAbs = Math.max(...list.map(f => Math.abs(Number(f.deltaRate) || 0)), 1e-9);
      el.innerHTML = list.slice(0, 20).map(f => {
        const d   = Number(f.deltaRate) || 0;
        const apr = Number(f.annualizedApr) || d * 3 * 365;
        const pct = Math.min(100, Math.abs(d) / maxAbs * 100);
        const trend = f.trend === 'expanding'
          ? '<span style="color:var(--green)">↑</span>'
          : '<span style="color:var(--red)">↓</span>';
        const aprCls = apr * 100 > 10 ? 'color:var(--green)' : apr * 100 > 3 ? 'color:var(--accent)' : 'color:var(--t3)';
        return `<div class="fund-row" style="cursor:pointer" onclick="AB.pages.market.pickSym('${f.symbol || ''}')">
          <span class="fund-sym">${String(f.symbol || '').replace(/USDT$/i, '')}</span>
          <span class="fund-delta" style="color:${d >= 0 ? 'var(--green)' : 'var(--red)'}">
            ${d >= 0 ? '+' : ''}${(d * 100).toFixed(4)}%
          </span>
          <span class="fund-apr" style="${aprCls}">${(apr * 100).toFixed(1)}%</span>
          <div style="display:flex;align-items:center;gap:4px;flex:1">
            <div class="fund-bar-bg" style="flex:1"><div class="fund-bar-fill" style="width:${pct.toFixed(0)}%"></div></div>
            ${trend}
          </div>
        </div>`;
      }).join('');
    } catch {
      el.innerHTML = '<div class="empty" style="padding:8px;color:var(--t3)">Funding N/A</div>';
    }
  },

  pickSym(sym) {
    if (!sym) return;
    this.selected = sym;
    this.overlayHist = {};
    this._chartSeries = {};
    this.render(AB.state.snapshot || {});
  },

  // ── Trade panel update ────────────────────────────────────────────────────
  updateTradePanel(data) {
    const sizeEl = document.getElementById('sizeIn');
    const size   = parseFloat(sizeEl?.value) || 100;
    const books  = this.booksFor(data, this.selected);
    const longEx = document.getElementById('m_tradeEx')?.value || this.selectedEx;
    const top    = books[longEx] || Object.values(books)[0];
    let mid = 0;
    if (top) mid = (Number(top.bestBid ?? 0) + Number(top.bestAsk ?? 0)) / 2;

    const symLabel = String(this.selected || '').replace(/USDT$/i, '');
    const est = document.getElementById('sizeEst');
    if (est && mid > 0) est.textContent = '≈ ' + (size / mid).toFixed(4) + ' ' + symLabel;
    const marg = document.getElementById('marginOut');
    if (marg) marg.textContent = '$' + (size / (this.lev || 3)).toFixed(2);

    // Exec button state
    const mode = (data.mode || 'PAPER').toUpperCase();
    const canLive  = mode === 'LIVE';
    const isLiveRo = mode.includes('LIVE');
    const execBtn  = document.getElementById('execBtn');
    const hedgeBtn = document.getElementById('hedgeBtn');
    if (execBtn) {
      const label = canLive
        ? `⚡ Live Hedge — ${symLabel}`
        : `📄 Paper Hedge — ${symLabel}`;
      execBtn.textContent = label;
      execBtn.disabled = false;
      execBtn.className = 'exec-btn ' + (canLive ? 'live-order' : 'long');
    }
    if (hedgeBtn) {
      hedgeBtn.style.display = isLiveRo && !canLive ? '' : 'none';
    }

    // Mode display in panel
    const modeEl = document.getElementById('m_tradeMode');
    if (modeEl) {
      modeEl.textContent = canLive ? '🔴 LIVE ORDERS' : isLiveRo ? '🟡 Read-only' : '📄 PAPER';
      modeEl.style.color = canLive ? 'var(--red)' : isLiveRo ? 'var(--amber)' : 'var(--t2)';
    }

    // Hedge leg preview
    const shortBooks = books;
    const shortEx = document.getElementById('m_tradeExShort')?.value;
    if (shortEx && books[shortEx]) {
      const sb = Number(books[shortEx].bestBid ?? 0);
      const lb = mid;
      const hintEl = document.getElementById('m_hedgeHint');
      const hintPx = document.getElementById('m_hedgePx');
      if (hintEl) hintEl.textContent = (shortEx || '?') + ' SHORT @ ≈ ' + (sb ? this.fmtP(sb) : '—');
      if (hintPx && lb > 0 && sb > 0) {
        const gross = (sb - lb) / lb * 100;
        hintPx.textContent = (gross >= 0 ? '+' : '') + gross.toFixed(3) + '%';
        hintPx.style.color = gross >= 0.1 ? 'var(--green)' : 'var(--t3)';
      }
    }
  },

  // ── Execute hedge (paper or live) ─────────────────────────────────────────
  async executeHedge(data) {
    const mode   = (data?.mode || (AB.state.snapshot || {}).mode || 'PAPER').toUpperCase();
    const canLive = mode === 'LIVE';
    const sym    = this.selected;
    const longEx = document.getElementById('m_tradeEx')?.value || this.selectedEx;
    const shortEx= document.getElementById('m_tradeExShort')?.value;
    const sizeRaw= parseFloat(document.getElementById('sizeIn')?.value) || 0;

    if (!sym)    { this.showToast('✗ No symbol selected'); return; }
    if (!shortEx){ this.showToast('✗ Select short exchange'); return; }
    if (longEx === shortEx) { this.showToast('✗ Long and short exchanges must differ'); return; }
    if (sizeRaw < 5) { this.showToast('✗ Size too small (min $5)'); return; }

    const btn = document.getElementById('execBtn');
    if (btn) { btn.disabled = true; btn.textContent = '⏳ Sending…'; }

    try {
      const body = {
        symbol:       sym,
        longExchange: longEx,
        shortExchange:shortEx,
        notionalUsd:  sizeRaw,
        leverage:     this.lev
      };
      const url = canLive ? '/api/live/hedge' : '/api/paper/hedge';
      const r   = await AB.api.post(url, body);
      if (r.ok !== false) {
        this.showToast(`✓ Hedge opened: ${longEx} LONG / ${shortEx} SHORT — $${sizeRaw}`);
        if (AB.refreshSnapshot) AB.refreshSnapshot();
      } else {
        this.showToast('✗ ' + (r.error || r.message || 'open failed'));
      }
    } catch (err) {
      this.showToast('✗ ' + (err.message || 'request failed'));
    } finally {
      if (btn) { btn.disabled = false; }
      this.updateTradePanel(AB.state.snapshot || {});
    }
  },

  // ── Multi-venue overlay chart (canvas) ────────────────────────────────────
  paintOverlayChart(data) {
    const canvas = document.getElementById('mainChart');
    if (!canvas) return;
    const sym    = this.selected;
    const books  = this.booksFor(data, sym);
    const venues = Object.keys(books);
    const now    = Date.now();

    venues.forEach(ex => {
      const b = books[ex] || {};
      const bid = Number(b.bestBid ?? 0), ask = Number(b.bestAsk ?? 0);
      if (bid <= 0 || ask <= 0) return;
      const mid = (bid + ask) / 2;
      if (!this.overlayHist[ex]) this.overlayHist[ex] = [];
      const arr  = this.overlayHist[ex];
      const last = arr[arr.length - 1];
      if (!last || now - last.t >= 200) {
        arr.push({ t: now, mid, bid, ask });
        while (arr.length > this.overlayMaxPts) arr.shift();
      } else { last.mid = mid; last.bid = bid; last.ask = ask; }
    });

    const parent = canvas.parentElement;
    const w = Math.max(100, (parent?.clientWidth || 600));
    const h = Math.max(80,  (parent?.clientHeight || 260));
    const dpr = window.devicePixelRatio || 1;
    if (canvas.width !== Math.floor(w * dpr) || canvas.height !== Math.floor(h * dpr)) {
      canvas.width  = Math.floor(w * dpr);
      canvas.height = Math.floor(h * dpr);
      canvas.style.width  = w + 'px';
      canvas.style.height = h + 'px';
    }
    const ctx = canvas.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);
    ctx.fillStyle = '#060a10';
    ctx.fillRect(0, 0, w, h);

    const series = venues
      .map(ex => ({ ex, pts: this.overlayHist[ex] || [] }))
      .filter(s => s.pts.length > 1);

    if (!series.length) {
      ctx.fillStyle = '#566878';
      ctx.font = '11px Inter,sans-serif';
      ctx.fillText('Waiting for live WS books… select a symbol', 16, h / 2);
      return;
    }

    let lo = Infinity, hi = -Infinity;
    series.forEach(s => s.pts.forEach(p => { lo = Math.min(lo, p.mid); hi = Math.max(hi, p.mid); }));
    const pad = (hi - lo) * 0.12 || lo * 0.0002;
    lo -= pad; hi += pad;
    const t0 = Math.min(...series.map(s => s.pts[0].t));
    const t1 = Math.max(...series.map(s => s.pts[s.pts.length - 1].t));
    const xOf = t => ((t - t0) / ((t1 - t0) || 1)) * (w - 20) + 10;
    const yOf = m => h - 14 - ((m - lo) / ((hi - lo) || 1)) * (h - 28);

    // Grid lines
    ctx.strokeStyle = 'rgba(255,255,255,0.04)';
    ctx.lineWidth = 1;
    for (let i = 0; i <= 4; i++) {
      const y = (h / 4) * i + 4;
      ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(w, y); ctx.stroke();
      const price = hi - (hi - lo) * (i / 4);
      ctx.fillStyle = '#566878';
      ctx.font = '9px JetBrains Mono,monospace';
      ctx.fillText(this.fmtP(price), 4, y - 2);
    }

    // Price lines per exchange
    series.forEach((s, i) => {
      const col = this.overlayColors[i % this.overlayColors.length];
      ctx.strokeStyle = col;
      ctx.lineWidth = 1.8;
      ctx.shadowColor = col;
      ctx.shadowBlur  = 2;
      ctx.beginPath();
      s.pts.forEach((p, j) => {
        const x = xOf(p.t), y = yOf(p.mid);
        j === 0 ? ctx.moveTo(x, y) : ctx.lineTo(x, y);
      });
      ctx.stroke();
      ctx.shadowBlur = 0;
      // End dot
      const last = s.pts[s.pts.length - 1];
      ctx.fillStyle = col;
      ctx.beginPath();
      ctx.arc(xOf(last.t), yOf(last.mid), 3, 0, Math.PI * 2);
      ctx.fill();
    });

    // Legend top-left
    series.forEach((s, i) => {
      const col  = this.overlayColors[i % this.overlayColors.length];
      const last = s.pts[s.pts.length - 1];
      ctx.fillStyle = col;
      ctx.font = '9px JetBrains Mono,monospace';
      ctx.fillText(s.ex.slice(0, 2).toUpperCase() + ' ' + this.fmtP(last.mid), 8, 14 + i * 11);
    });

    // Cross-venue delta top-right
    if (series.length >= 2) {
      const lasts = series.map(s => s.pts[s.pts.length - 1].mid);
      const d = ((Math.max(...lasts) - Math.min(...lasts)) / Math.min(...lasts)) * 100;
      ctx.fillStyle = d >= 0.1 ? '#26d48a' : '#8fa4bc';
      ctx.font = 'bold 11px JetBrains Mono,monospace';
      ctx.fillText('Δ ' + d.toFixed(4) + '%', w - 96, 14);
    }

    // Sym + timeframe label
    ctx.fillStyle = '#8fa4bc';
    ctx.font = '10px Inter,sans-serif';
    ctx.fillText((this.selected || '') + ' · live mids · WS', w / 2 - 60, h - 3);
  },

  // ── Toast notification ────────────────────────────────────────────────────
  showToast(msg) {
    const t = document.getElementById('toast');
    if (!t) return;
    const isOk = msg.startsWith('✓');
    t.textContent = msg;
    t.style.borderColor = isOk ? 'var(--green)' : 'var(--red)';
    t.style.color       = isOk ? 'var(--green)' : 'var(--red)';
    t.classList.add('show');
    clearTimeout(this._toastTimer);
    this._toastTimer = setTimeout(() => t.classList.remove('show'), 2800);
  },

  // ── Bind events once ──────────────────────────────────────────────────────
  bindOnce(data) {
    if (this._bound) return;
    this._bound = true;
    const self = this;

    // Side tabs
    document.getElementById('longTab')?.addEventListener('click', () => {
      self.side = 'long';
      document.getElementById('longTab')?.classList.add('active');
      document.getElementById('shortTab')?.classList.remove('active');
    });
    document.getElementById('shortTab')?.addEventListener('click', () => {
      self.side = 'short';
      document.getElementById('longTab')?.classList.remove('active');
      document.getElementById('shortTab')?.classList.add('active');
    });

    // Leverage
    document.getElementById('m_levBtns')?.addEventListener('click', e => {
      const b = e.target.closest('.lev-btn');
      if (!b) return;
      self.lev = Number(b.getAttribute('data-lev')) || 3;
      document.querySelectorAll('#m_levBtns .lev-btn').forEach(x =>
        x.classList.toggle('active', x === b));
      self.updateTradePanel(AB.state.snapshot || {});
    });

    // Size input → update panel
    document.getElementById('sizeIn')?.addEventListener('input', () =>
      self.updateTradePanel(AB.state.snapshot || {}));

    // Short exchange select → update hedge preview
    document.getElementById('m_tradeExShort')?.addEventListener('change', () =>
      self.updateTradePanel(AB.state.snapshot || {}));
    document.getElementById('m_tradeEx')?.addEventListener('change', () =>
      self.updateTradePanel(AB.state.snapshot || {}));

    // Timeframe buttons (for future TradingView integration)
    document.getElementById('m_tfBtns')?.addEventListener('click', e => {
      const b = e.target.closest('.tf-btn');
      if (!b) return;
      self.interval = b.getAttribute('data-tf') || '15';
      document.querySelectorAll('#m_tfBtns .tf-btn').forEach(x =>
        x.classList.toggle('active', x === b));
      self.overlayHist = {};
    });

    // Execute hedge button
    document.getElementById('execBtn')?.addEventListener('click', () =>
      self.executeHedge(AB.state.snapshot || {}));

    // 1-click button from arb opportunity panel
    document.getElementById('m_oneClick')?.addEventListener('click', () => {
      const btn     = document.getElementById('m_oneClick');
      const longEx  = btn?.getAttribute('data-long-ex');
      const shortEx = btn?.getAttribute('data-short-ex');
      if (longEx && shortEx) {
        const l = document.getElementById('m_tradeEx');
        const s = document.getElementById('m_tradeExShort');
        if (l) { for (const o of l.options) o.selected = o.value === longEx; }
        if (s) { for (const o of s.options) o.selected = o.value === shortEx; }
        self.executeHedge(AB.state.snapshot || {});
      }
    });

    // Size quick-pick buttons
    document.getElementById('m_sizeBtns')?.addEventListener('click', e => {
      const b = e.target.closest('[data-size]');
      if (!b) return;
      const v = b.getAttribute('data-size');
      const inp = document.getElementById('sizeIn');
      if (inp) { inp.value = v; self.updateTradePanel(AB.state.snapshot || {}); }
    });
  },

  // ── Helpers ───────────────────────────────────────────────────────────────
  fmtP(p) {
    if (!p || !Number.isFinite(p)) return '—';
    if (p >= 10000) return p.toFixed(1);
    if (p >= 1000)  return p.toFixed(2);
    if (p >= 1)     return p.toFixed(4);
    if (p >= 0.01)  return p.toFixed(5);
    return p.toFixed(6);
  },
  fmtQ(q) {
    if (!Number.isFinite(q)) return '—';
    if (q >= 1e9) return (q / 1e9).toFixed(2) + 'B';
    if (q >= 1e6) return (q / 1e6).toFixed(2) + 'M';
    if (q >= 1e3) return (q / 1e3).toFixed(1) + 'K';
    return q.toFixed(q >= 10 ? 0 : 2);
  }
};
