AB.pages.market = {
  selected: '',
  selectedEx: '',
  side: 'long',
  lev: 5,
  interval: '15',
  overlayHist: {},
  overlayMaxPts: 200,
  overlayColors: ['#38bdf8', '#2dd4bf', '#a78bfa', '#f0b429', '#f472b6', '#34d399'],
  chart: null,
  chartBound: false,

  render(data) {
    if (!data) return;
    const symbols = (data.symbols || []).length
      ? data.symbols
      : Object.keys(data.bookTickers || {});
    if (!this.selected && symbols[0]) this.selected = symbols[0];
    if (this.selected && symbols.length && !symbols.includes(this.selected))
      this.selected = symbols[0];

    const mode = (data.mode || 'PAPER').toUpperCase();
    const pill = document.getElementById('m_modePill');
    if (pill) {
      pill.textContent = mode.indexOf('LIVE') >= 0 ? mode : 'PAPER';
      pill.className = 'mode-pill' + (mode.indexOf('LIVE') >= 0 ? '' : ' ro');
    }
    const scan = document.getElementById('m_scanStat');
    if (scan) scan.textContent = 'scan #' + (data.scanCount != null ? Number(data.scanCount).toLocaleString() : '—');
    const exs = data.exchanges || [];
    const exLive = document.getElementById('m_exLive');
    if (exLive) exLive.textContent = (exs.length || 0) + ' ex';

    this.renderSymTabs(symbols);
    this.renderExTabs(data);
    this.renderOrderBook(data);
    this.renderArb(data);
    this.renderPositions(data);
    this.renderFundingStrip();
    this.renderCompare(data);
    this.paintOverlayChart(data);
    this.updateTradePanel(data);
    this.bindOnce();
  },

  bindOnce() {
    if (this._bound) return;
    this._bound = true;
    const self = this;
    document.getElementById('longTab')?.addEventListener('click', () => {
      self.side = 'long';
      document.getElementById('longTab')?.classList.add('active');
      document.getElementById('shortTab')?.classList.remove('active');
    });
    document.getElementById('shortTab')?.addEventListener('click', () => {
      self.side = 'short';
      document.getElementById('shortTab')?.classList.add('active');
      document.getElementById('longTab')?.classList.remove('active');
    });
    document.getElementById('m_levBtns')?.addEventListener('click', (e) => {
      const b = e.target.closest('.lev-btn');
      if (!b) return;
      self.lev = Number(b.getAttribute('data-lev')) || 5;
      document.querySelectorAll('#m_levBtns .lev-btn').forEach(x => x.classList.toggle('active', x === b));
      self.updateTradePanel(AB.state.snapshot || {});
    });
    document.getElementById('sizeIn')?.addEventListener('input', () => self.updateTradePanel(AB.state.snapshot || {}));
    document.getElementById('m_tfBtns')?.addEventListener('click', (e) => {
      const b = e.target.closest('.tf-btn');
      if (!b) return;
      self.interval = b.getAttribute('data-tf') || '15';
      document.querySelectorAll('#m_tfBtns .tf-btn').forEach(x => x.classList.toggle('active', x === b));
      self.overlayHist = {};
    });
  },

  renderSymTabs(symbols) {
    const el = document.getElementById('m_symTabs');
    if (!el) return;
    const show = symbols.slice(0, 12);
    el.innerHTML = show.map(s => {
      const lab = s.replace(/USDT$/i, '') + '/USDT';
      const act = s === this.selected ? ' active' : '';
      return '<button type="button" class="sym-btn' + act + '" data-sym="' + s + '">' + lab + '</button>';
    }).join('');
    el.querySelectorAll('.sym-btn').forEach(btn => {
      btn.onclick = () => {
        this.selected = btn.getAttribute('data-sym');
        this.overlayHist = {};
        this.render(AB.state.snapshot || {});
      };
    });
  },

  renderExTabs(data) {
    const el = document.getElementById('m_exTabs');
    if (!el) return;
    const books = this.booksFor(data, this.selected);
    let venues = Object.keys(books);
    if (!venues.length) venues = data.exchanges || ['Binance', 'Bybit', 'OKX', 'Bitget'];
    if (!this.selectedEx || !venues.includes(this.selectedEx)) this.selectedEx = venues[0];
    const short = { Binance: 'BN', Bybit: 'BY', OKX: 'OK', Bitget: 'BG', GateIo: 'GT' };
    el.innerHTML = venues.map(ex => {
      const act = ex === this.selectedEx ? ' active' : '';
      return '<button type="button" class="ex-tab' + act + '" data-ex="' + ex + '">' + (short[ex] || ex.slice(0, 2)) + '</button>';
    }).join('');
    el.querySelectorAll('.ex-tab').forEach(btn => {
      btn.onclick = () => {
        this.selectedEx = btn.getAttribute('data-ex');
        this.renderOrderBook(AB.state.snapshot || {});
      };
    });
    const sel = document.getElementById('m_tradeEx');
    if (sel) {
      sel.innerHTML = venues.map(ex => '<option value="' + ex + '"' + (ex === this.selectedEx ? ' selected' : '') + '>' + ex + '</option>').join('');
    }
  },

  booksFor(data, sym) {
    let books = (data.bookTickers || {})[sym] || {};
    if (!Object.keys(books).length && data.bookTickers) {
      const key = Object.keys(data.bookTickers).find(k => k.toUpperCase() === (sym || '').toUpperCase());
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
    const depthRoot = data.orderBookDepth || {};
    let depth = depthRoot[sym] || {};
    if (!Object.keys(depth).length) {
      const key = Object.keys(depthRoot).find(k => k.toUpperCase() === (sym || '').toUpperCase());
      if (key) depth = depthRoot[key] || {};
    }
    return depth[ex] || depth[Object.keys(depth).find(k => k.toLowerCase() === (ex || '').toLowerCase()) || ''] || null;
  },

  renderOrderBook(data) {
    const sym = this.selected;
    const ex = this.selectedEx;
    const obSym = document.getElementById('obSym');
    if (obSym) obSym.textContent = sym || '—';
    const depth = this.depthFor(data, sym, ex);
    let asks = [], bids = [];
    if (depth) {
      asks = (depth.asks || depth.Asks || []).slice(0, 10).map(r => ({
        pr: Number(r[0] ?? r.price), q: Number(r[1] ?? r.quantity ?? r.qty)
      })).filter(x => x.pr > 0);
      bids = (depth.bids || depth.Bids || []).slice(0, 10).map(r => ({
        pr: Number(r[0] ?? r.price), q: Number(r[1] ?? r.quantity ?? r.qty)
      })).filter(x => x.pr > 0);
    }
    const books = this.booksFor(data, sym);
    const top = books[ex] || Object.values(books)[0];
    let mid = 0, spd = 0;
    if (top) {
      const bid = Number(top.bestBid ?? top.BestBid);
      const ask = Number(top.bestAsk ?? top.BestAsk);
      if (bid > 0 && ask > 0) { mid = (bid + ask) / 2; spd = ask - bid; }
    }
    if (!mid && asks.length && bids.length) {
      mid = (asks[0].pr + bids[0].pr) / 2;
      spd = asks[0].pr - bids[0].pr;
    }
    const maxQ = Math.max(1, ...asks.map(a => a.q), ...bids.map(b => b.q));
    const asksEl = document.getElementById('ob-asks');
    const bidsEl = document.getElementById('ob-bids');
    if (asksEl) {
      const rows = asks.slice().reverse();
      asksEl.innerHTML = rows.length ? rows.map(r => {
        const pct = Math.min(100, (r.q / maxQ) * 100);
        return '<div class="ob-row"><div class="ob-bar ask-bar" style="width:' + pct.toFixed(0) + '%"></div>' +
          '<span class="price" style="color:var(--red)">' + this.fmtP(r.pr) + '</span>' +
          '<span class="qty">' + this.fmtQ(r.q) + '</span>' +
          '<span class="total">' + this.fmtQ(r.pr * r.q) + '</span></div>';
      }).join('') : '<div class="empty" style="padding:8px">No asks</div>';
    }
    if (bidsEl) {
      bidsEl.innerHTML = bids.length ? bids.map(r => {
        const pct = Math.min(100, (r.q / maxQ) * 100);
        return '<div class="ob-row"><div class="ob-bar bid-bar" style="width:' + pct.toFixed(0) + '%"></div>' +
          '<span class="price" style="color:var(--green)">' + this.fmtP(r.pr) + '</span>' +
          '<span class="qty">' + this.fmtQ(r.q) + '</span>' +
          '<span class="total">' + this.fmtQ(r.pr * r.q) + '</span></div>';
      }).join('') : '<div class="empty" style="padding:8px">No bids</div>';
    }
    const midEl = document.getElementById('ob-mid-price');
    const spdEl = document.getElementById('ob-spread');
    if (midEl) midEl.textContent = mid ? this.fmtP(mid) : '—';
    if (spdEl) spdEl.textContent = spd ? ('spd ' + this.fmtP(spd)) : 'spd —';
    const cp = document.getElementById('chartPrice');
    if (cp && mid) cp.textContent = this.fmtP(mid);
    const cs = document.getElementById('chartSym');
    if (cs) cs.textContent = (sym || '—') + ' · Futures perp';
    const pi = document.getElementById('priceIn');
    if (pi && mid) pi.value = this.fmtP(mid);
    const ml = document.getElementById('m_markLbl');
    if (ml) ml.textContent = 'Mark: ' + (mid ? this.fmtP(mid) : '—');
  },

  renderCompare(data) {
    const el = document.getElementById('exCompare');
    if (!el) return;
    const books = this.booksFor(data, this.selected);
    const cols = this.overlayColors;
    const venues = Object.keys(books);
    if (!venues.length) { el.innerHTML = ''; return; }
    el.innerHTML = venues.map((ex, i) => {
      const b = books[ex];
      const mid = (Number(b.bestBid) + Number(b.bestAsk)) / 2;
      const c = cols[i % cols.length];
      return '<span class="ex-badge" style="background:' + c + '22;color:' + c + '">' +
        ex.slice(0, 2).toUpperCase() + ' ' + this.fmtP(mid) + '</span>';
    }).join('');
    // live edge for selected
    const mids = venues.map(ex => {
      const b = books[ex];
      return (Number(b.bestBid) + Number(b.bestAsk)) / 2;
    }).filter(x => x > 0);
    const edgeEl = document.getElementById('liveEdge');
    const routeEl = document.getElementById('m_liveRoute');
    if (mids.length >= 2) {
      const lo = Math.min(...mids), hi = Math.max(...mids);
      const edge = ((hi - lo) / lo) * 100;
      if (edgeEl) {
        edgeEl.textContent = (edge >= 0 ? '+' : '') + edge.toFixed(3) + '%';
        edgeEl.style.color = edge >= 0.3 ? 'var(--green)' : (edge >= 0.1 ? 'var(--amber)' : 'var(--red)');
      }
      const loEx = venues.find(ex => {
        const b = books[ex]; return Math.abs((Number(b.bestBid) + Number(b.bestAsk)) / 2 - lo) < 1e-12;
      });
      const hiEx = venues.find(ex => {
        const b = books[ex]; return Math.abs((Number(b.bestBid) + Number(b.bestAsk)) / 2 - hi) < 1e-12;
      });
      if (routeEl) routeEl.textContent = (loEx || '?') + ' LONG → ' + (hiEx || '?') + ' SHORT';
    }
  },

  renderArb(data) {
    const el = document.getElementById('arbList');
    const cnt = document.getElementById('arbCount');
    if (!el) return;
    const opps = Array.isArray(data.opportunities) ? data.opportunities.slice() : [];
    opps.sort((a, b) => (Number(b.netSpreadPercent ?? b.netProfitPercent) || 0) - (Number(a.netSpreadPercent ?? a.netProfitPercent) || 0));
    const min = Number(data.minProfitPercent) || 0;
    if (cnt) cnt.textContent = opps.filter(o => o.isExecutable).length + ' executable';
    if (!opps.length) {
      el.innerHTML = '<div class="empty" style="padding:10px">No routes ≥ threshold</div>';
      return;
    }
    el.innerHTML = opps.slice(0, 30).map(o => {
      const net = Number(o.netSpreadPercent ?? o.netProfitPercent) || 0;
      const hot = o.isExecutable === true || net >= min;
      const route = (o.longExchange || o.buyExchange || '?') + ' → ' + (o.shortExchange || o.sellExchange || '?');
      const fund = Number(o.expectedFundingPercent) || 0;
      return '<div class="arb-row' + (hot ? ' hot' : '') + '" data-sym="' + (o.symbol || '') + '">' +
        '<div><div class="arb-sym">' + (o.symbol || '').replace(/USDT$/i, '') + '</div>' +
        '<div class="arb-route">' + route + '</div></div>' +
        '<div style="text-align:right"><div class="arb-edge" style="color:' + (net >= 0.2 ? 'var(--green)' : 'var(--t2)') + '">' +
        (net >= 0 ? '+' : '') + net.toFixed(3) + '%</div>' +
        (fund ? '<div class="arb-apr">fund ' + fund.toFixed(3) + '%</div>' : '') + '</div>' +
        '<div style="text-align:center">' + (hot
          ? '<button type="button" class="arb-exec" data-pick="' + (o.symbol || '') + '">View</button>'
          : '<span style="color:var(--t3);font-size:9px">below</span>') + '</div><div></div></div>';
    }).join('');
    el.querySelectorAll('[data-pick]').forEach(btn => {
      btn.onclick = (e) => {
        e.stopPropagation();
        this.selected = btn.getAttribute('data-pick');
        this.overlayHist = {};
        this.render(AB.state.snapshot || {});
      };
    });
    el.querySelectorAll('.arb-row').forEach(row => {
      row.onclick = () => {
        const s = row.getAttribute('data-sym');
        if (!s) return;
        this.selected = s;
        this.overlayHist = {};
        this.render(AB.state.snapshot || {});
      };
    });
  },

  renderPositions(data) {
    const el = document.getElementById('posTable');
    if (!el) return;
    const fp = data.futuresPaper || data.paper || {};
    const paper = Array.isArray(fp.positions) ? fp.positions : [];
    const live = data.livePositions || {};
    const ledger = Array.isArray(live.ledger) ? live.ledger
      : (Array.isArray(live.open) ? live.open : []);
    const rows = [];
    paper.forEach(p => {
      rows.push({
        sym: p.symbol || p.Symbol,
        side: (p.longExchange ? 'Long@' + p.longExchange : 'Hedge'),
        side2: p.shortExchange ? 'Short@' + p.shortExchange : '',
        entry: Number(p.longEntry ?? p.entryPrice ?? p.avgEntry) || 0,
        upnl: Number(p.unrealizedPnlUsd ?? p.unrealizedPnl ?? p.uPnl) || 0,
        id: p.id || p.tradeId
      });
    });
    ledger.forEach(p => {
      rows.push({
        sym: p.symbol || p.Symbol,
        side: p.side || p.Side || 'LIVE',
        side2: p.exchange || p.Exchange || '',
        entry: Number(p.entryPrice ?? p.averagePrice ?? p.avgEntry) || 0,
        upnl: Number(p.unrealizedPnl ?? p.uPnl) || 0,
        id: p.id || p.tradeId
      });
    });
    const sub = document.getElementById('m_posSub');
    if (sub) sub.textContent = rows.length + ' open';
    const real = document.getElementById('m_posRealized');
    if (real) {
      const r = Number(fp.realizedPnlUsd ?? fp.realizedPnl) || 0;
      real.textContent = (r >= 0 ? '+' : '') + '$' + r.toFixed(2);
      real.style.color = r >= 0 ? 'var(--green)' : 'var(--red)';
    }
    if (!rows.length) {
      el.innerHTML = '<div class="empty" style="padding:10px">No open hedges</div>';
      return;
    }
    el.innerHTML = rows.map(p => {
      const sideCls = String(p.side).toLowerCase().indexOf('short') >= 0 ? 'short-badge' : 'long-badge';
      const up = p.upnl;
      return '<div class="pos-row">' +
        '<span class="pos-sym">' + String(p.sym || '').replace(/USDT$/i, '') +
        '<span style="font-size:8px;color:var(--t3);margin-left:3px">' + (p.side2 || '') + '</span></span>' +
        '<span><span class="pos-side ' + sideCls + '">' + String(p.side).slice(0, 5) + '</span></span>' +
        '<span class="mono" style="color:var(--t2)">' + (p.entry ? this.fmtP(p.entry) : '—') + '</span>' +
        '<span class="mono" style="color:' + (up >= 0 ? 'var(--green)' : 'var(--red)') + '">' +
        (up >= 0 ? '+' : '') + up.toFixed(2) + '</span>' +
        '<button type="button" class="close-btn" data-close="' + (p.id || '') + '">Close</button></div>';
    }).join('');
    el.querySelectorAll('[data-close]').forEach(btn => {
      btn.onclick = async () => {
        const id = btn.getAttribute('data-close');
        if (!id) return;
        try {
          await AB.api.post('/api/paper/close/' + id);
          if (AB.refreshSnapshot) AB.refreshSnapshot();
        } catch (e) { console.warn(e); }
      };
    });
  },

  async renderFundingStrip() {
    const el = document.getElementById('fundTable');
    if (!el) return;
    try {
      const rows = await AB.api.get('/api/funding');
      const list = Array.isArray(rows) ? rows : [];
      if (!list.length) {
        el.innerHTML = '<div class="empty" style="padding:8px">Funding loading…</div>';
        return;
      }
      const maxAbs = Math.max(...list.map(f => Math.abs(Number(f.deltaRate) || 0)), 1e-9);
      el.innerHTML = list.slice(0, 20).map(f => {
        const d = Number(f.deltaRate) || 0;
        const apr = Number(f.annualizedApr) || (d * 3 * 365);
        const pct = Math.min(100, (Math.abs(d) / maxAbs) * 100);
        return '<div class="fund-row">' +
          '<span class="fund-sym">' + String(f.symbol || '').replace(/USDT$/i, '') + '</span>' +
          '<span class="fund-delta">' + (d >= 0 ? '+' : '') + (d * 100).toFixed(4) + '%</span>' +
          '<span class="fund-apr">' + (apr * 100).toFixed(1) + '%</span>' +
          '<div><div class="fund-bar-bg"><div class="fund-bar-fill" style="width:' + pct.toFixed(0) + '%"></div></div></div></div>';
      }).join('');
    } catch {
      el.innerHTML = '<div class="empty" style="padding:8px">Funding N/A</div>';
    }
  },

  updateTradePanel(data) {
    const size = parseFloat(document.getElementById('sizeIn')?.value) || 100;
    const books = this.booksFor(data, this.selected);
    const top = books[this.selectedEx] || Object.values(books)[0];
    let mid = 0;
    if (top) mid = (Number(top.bestBid) + Number(top.bestAsk)) / 2;
    const est = document.getElementById('sizeEst');
    if (est && mid > 0) est.textContent = '≈ ' + (size / mid).toFixed(2) + ' ' + String(this.selected || '').replace(/USDT$/i, '');
    const marg = document.getElementById('marginOut');
    if (marg) marg.textContent = '$' + (size / (this.lev || 5)).toFixed(2);
  },

  /** Multi-venue mid overlay on one canvas — visual divergence */
  paintOverlayChart(data) {
    const canvas = document.getElementById('mainChart');
    if (!canvas) return;
    const sym = this.selected;
    const books = this.booksFor(data, sym);
    const venues = Object.keys(books);
    const now = Date.now();
    venues.forEach(ex => {
      const b = books[ex] || {};
      const bid = Number(b.bestBid ?? 0), ask = Number(b.bestAsk ?? 0);
      if (bid <= 0 || ask <= 0) return;
      const mid = (bid + ask) / 2;
      if (!this.overlayHist[ex]) this.overlayHist[ex] = [];
      const arr = this.overlayHist[ex];
      const last = arr[arr.length - 1];
      if (!last || now - last.t >= 250) {
        arr.push({ t: now, mid });
        while (arr.length > this.overlayMaxPts) arr.shift();
      } else last.mid = mid;
    });

    const parent = canvas.parentElement;
    const w = Math.max(100, parent?.clientWidth || canvas.clientWidth || 600);
    const h = Math.max(80, parent?.clientHeight || 280);
    const dpr = window.devicePixelRatio || 1;
    if (canvas.width !== Math.floor(w * dpr) || canvas.height !== Math.floor(h * dpr)) {
      canvas.width = Math.floor(w * dpr);
      canvas.height = Math.floor(h * dpr);
      canvas.style.width = w + 'px';
      canvas.style.height = h + 'px';
    }
    const ctx = canvas.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);
    ctx.fillStyle = '#060a10';
    ctx.fillRect(0, 0, w, h);

    const series = venues.map(ex => ({ ex, pts: this.overlayHist[ex] || [] })).filter(s => s.pts.length > 1);
    if (!series.length) {
      ctx.fillStyle = '#566878';
      ctx.font = '12px Inter,sans-serif';
      ctx.fillText('Waiting for live mids… select symbol with WS books', 16, h / 2);
      return;
    }
    let lo = Infinity, hi = -Infinity;
    series.forEach(s => s.pts.forEach(p => { lo = Math.min(lo, p.mid); hi = Math.max(hi, p.mid); }));
    const pad = (hi - lo) * 0.08 || lo * 0.0001;
    lo -= pad; hi += pad;
    const t0 = Math.min(...series.map(s => s.pts[0].t));
    const t1 = Math.max(...series.map(s => s.pts[s.pts.length - 1].t));
    const xOf = (t) => ((t - t0) / ((t1 - t0) || 1)) * (w - 16) + 8;
    const yOf = (m) => h - 12 - ((m - lo) / ((hi - lo) || 1)) * (h - 28);

    // divergence band between min/max venue at each time bucket
    ctx.strokeStyle = 'rgba(255,255,255,0.04)';
    for (let i = 0; i < 4; i++) {
      const y = (h / 4) * i + 8;
      ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(w, y); ctx.stroke();
    }

    series.forEach((s, i) => {
      const col = this.overlayColors[i % this.overlayColors.length];
      ctx.strokeStyle = col;
      ctx.lineWidth = 1.6;
      ctx.beginPath();
      s.pts.forEach((p, j) => {
        const x = xOf(p.t), y = yOf(p.mid);
        if (j === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
      });
      ctx.stroke();
      // last dot + label
      const last = s.pts[s.pts.length - 1];
      const lx = xOf(last.t), ly = yOf(last.mid);
      ctx.fillStyle = col;
      ctx.beginPath(); ctx.arc(lx, ly, 3, 0, Math.PI * 2); ctx.fill();
      ctx.font = '10px JetBrains Mono,monospace';
      ctx.fillText(s.ex.slice(0, 3), 8, 14 + i * 12);
    });

    // show current cross Δ
    if (series.length >= 2) {
      const lasts = series.map(s => s.pts[s.pts.length - 1].mid);
      const d = ((Math.max(...lasts) - Math.min(...lasts)) / Math.min(...lasts)) * 100;
      ctx.fillStyle = d >= 0.15 ? '#26d48a' : '#8fa4bc';
      ctx.font = '11px JetBrains Mono,monospace';
      ctx.fillText('Δ ' + d.toFixed(3) + '%', w - 90, 16);
    }
  },

  fmtP(p) {
    if (!p || !Number.isFinite(p)) return '—';
    if (p >= 1000) return p.toFixed(2);
    if (p >= 1) return p.toFixed(4);
    if (p >= 0.01) return p.toFixed(5);
    return p.toFixed(6);
  },
  fmtQ(q) {
    if (!Number.isFinite(q)) return '—';
    if (q >= 1e6) return (q / 1e6).toFixed(2) + 'M';
    if (q >= 1e3) return (q / 1e3).toFixed(1) + 'K';
    return q.toFixed(q >= 10 ? 0 : 2);
  }
};
