AB.pages.market = {
  selected: '',
  depthFp: '',
  lastDepthPaint: 0,
  chartKey: '',
  overlayHist: {},
  overlayMaxPts: 180,
  overlayColors: ['#2dd4bf', '#60a5fa', '#f472b6', '#fbbf24', '#a78bfa', '#34d399'],

  render(data) {
    if (!data) return;
    const symbols = (data.symbols || []).length
      ? data.symbols
      : Object.keys(data.bookTickers || {});
    const sel = AB.$('m_symbol');
    if (sel) {
      const cur = this.selected || sel.value;
      sel.innerHTML = symbols.map(s =>
        `<option value="${s}" ${s === cur ? 'selected' : ''}>${s}</option>`
      ).join('') || '<option value="">—</option>';
      if (!this.selected && symbols[0]) this.selected = symbols[0];
      if (this.selected) sel.value = this.selected;
    }

    if (AB.$('m_streams')) {
      const live = data.streamsLive ?? '—';
      const total = data.streamsTotal ?? Object.keys(data.connectionStatus || {}).length;
      AB.$('m_streams').textContent = `streams ${live}/${total}`;
    }

    // opportunities
    const opps = data.opportunities || [];
    if (AB.$('m_oppBody')) {
      AB.$('m_oppBody').innerHTML = opps.length
        ? opps.slice(0, 20).map(o => `<tr>
            <td class="mono" style="color:var(--blue)">${o.symbol}</td>
            <td class="mono" style="font-size:11px"><span class="pos">${o.longExchange || o.buyExchange}</span>→<span class="neg">${o.shortExchange || o.sellExchange}</span></td>
            <td>${AB.fmtPct(o.netSpreadPercent ?? o.netProfitPercent)}</td>
            <td class="mono">${AB.fmt(o.netRoundTripPercent, 3)}%</td>
            <td>${AB.fmtUsd(o.estNetPnlUsd ?? o.netProfitQuote)}</td>
          </tr>`).join('')
        : '<tr><td colspan="5" class="empty">No signals ≥ threshold</td></tr>';
    }

    const fp = data.futuresPaper || {};
    if (AB.$('m_positions')) {
      const positions = fp.positions || [];
      AB.$('m_positions').innerHTML = positions.length
        ? positions.map(p => AB.posCardHtml(p)).join('')
        : '<div class="empty">No open hedges</div>';
    }

    this.renderDepth(data);
    this.renderTape(data);
    this.syncLastPrice(data);
    this.paintOverlay(data);

    if (this.selected) this.loadChart(this.selected);
  },

  syncLastPrice(data) {
    const books = data.bookTickers || {};
    const by = books[this.selected];
    if (!by || !AB.$('m_last')) return;
    const first = Object.values(by)[0];
    if (!first) return;
    const mid = (Number(first.bestBid) + Number(first.bestAsk)) / 2;
    AB.$('m_last').textContent = AB.fmt(mid, mid < 1 ? 6 : 4);
    AB.$('m_last').style.color = 'var(--text)';
  },


  paintOverlay(data) {
    const canvas = AB.$('m_overlay');
    if (!canvas) return;
    const sym = this.selected || (data.symbols && data.symbols[0]) || '';
    if (!sym) {
      this._overlayMsg(canvas, 'Select a symbol');
      return;
    }
    if (!this.overlayHist) this.overlayHist = {};
    if (!this.overlayColors) this.overlayColors = ['#2dd4bf', '#60a5fa', '#f472b6', '#fbbf24', '#a78bfa', '#34d399'];
    if (!this.overlayMaxPts) this.overlayMaxPts = 200;

    // Resolve books: bookTickers[sym] or case-insensitive key
    let books = (data.bookTickers || {})[sym] || {};
    if (!Object.keys(books).length && data.bookTickers) {
      const key = Object.keys(data.bookTickers).find(k => k.toUpperCase() === sym.toUpperCase());
      if (key) books = data.bookTickers[key] || {};
    }
    // Fallback: orderBookDepth top of book
    if (!Object.keys(books).length && data.orderBookDepth) {
      const depth = data.orderBookDepth[sym] || data.orderBookDepth[Object.keys(data.orderBookDepth).find(k => k.toUpperCase() === sym.toUpperCase()) || ''] || {};
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

    const venues = Object.keys(books);
    const now = Date.now();
    venues.forEach(ex => {
      const b = books[ex] || {};
      const bid = Number(b.bestBid ?? b.BestBid ?? 0);
      const ask = Number(b.bestAsk ?? b.BestAsk ?? 0);
      if (bid <= 0 || ask <= 0) return;
      const mid = (bid + ask) / 2;
      if (!this.overlayHist[ex]) this.overlayHist[ex] = [];
      const arr = this.overlayHist[ex];
      const last = arr[arr.length - 1];
      if (!last || now - last.t >= 300) {
        arr.push({ t: now, mid });
        while (arr.length > this.overlayMaxPts) arr.shift();
      } else {
        last.mid = mid;
        last.t = now;
      }
    });

    const mids = venues.map(ex => {
      const b = books[ex] || {};
      const bid = Number(b.bestBid ?? b.BestBid ?? 0);
      const ask = Number(b.bestAsk ?? b.BestAsk ?? 0);
      if (bid <= 0 || ask <= 0) return null;
      return { ex, mid: (bid + ask) / 2, bid, ask };
    }).filter(Boolean);

    const divEl = AB.$('m_overlayDiv');
    const legEl = AB.$('m_overlayLegend');
    const hintEl = AB.$('m_overlayHint');

    // Spread bar visualization in legend area
    if (mids.length >= 2) {
      const sorted = [...mids].sort((a, b) => a.mid - b.mid);
      const lo = sorted[0], hi = sorted[sorted.length - 1];
      const pct = lo.mid > 0 ? ((hi.mid - lo.mid) / lo.mid) * 100 : 0;
      if (divEl) {
        divEl.innerHTML = '<span style="color:' + (pct >= 0.08 ? '#34d399' : '#94a3b8') + '">Δ ' +
          pct.toFixed(3) + '%</span> · LONG ' + lo.ex + ' / SHORT ' + hi.ex;
      }
      if (hintEl) hintEl.textContent = sym + ' · live mid overlay · ' + mids.length + ' venues';
    } else {
      if (divEl) divEl.textContent = venues.length ? 'waiting mids…' : 'no book data for ' + sym;
      if (hintEl) hintEl.textContent = 'live mid · need WS books';
    }

    if (legEl) {
      if (!mids.length) {
        legEl.innerHTML = '<span class="muted">No quotes — check streams / pick another symbol</span>';
      } else {
        const minM = Math.min(...mids.map(x => x.mid));
        const maxM = Math.max(...mids.map(x => x.mid));
        const range = maxM - minM || 1;
        legEl.innerHTML = mids.map((m, i) => {
          const c = this.overlayColors[i % this.overlayColors.length];
          const bar = Math.max(4, ((m.mid - minM) / range) * 100);
          return '<div style="display:inline-flex;align-items:center;gap:6px;margin:2px 12px 2px 0">' +
            '<span style="color:' + c + '">●</span><span class="mono">' + m.ex + '</span>' +
            '<span class="mono">' + AB.fmt(m.mid, m.mid < 1 ? 6 : 4) + '</span>' +
            '<span style="display:inline-block;height:6px;width:' + bar + 'px;background:' + c +
            ';border-radius:3px;opacity:0.85"></span></div>';
        }).join('');
      }
    }

    const dpr = window.devicePixelRatio || 1;
    const w = Math.max(canvas.clientWidth || 900, 300);
    const h = 220;
    if (canvas.width !== Math.floor(w * dpr) || canvas.height !== Math.floor(h * dpr)) {
      canvas.width = Math.floor(w * dpr);
      canvas.height = Math.floor(h * dpr);
    }
    const ctx = canvas.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);
    ctx.fillStyle = '#0a0e14';
    ctx.fillRect(0, 0, w, h);

    let minP = Infinity, maxP = -Infinity, minT = Infinity, maxT = -Infinity;
    let anyPts = 0;
    venues.forEach(ex => {
      (this.overlayHist[ex] || []).forEach(p => {
        minP = Math.min(minP, p.mid); maxP = Math.max(maxP, p.mid);
        minT = Math.min(minT, p.t); maxT = Math.max(maxT, p.t);
        anyPts++;
      });
    });

    if (!isFinite(minP) || anyPts < 1) {
      this._overlayMsg(canvas, 'Waiting for live mids… (books on this symbol?)', ctx, w, h);
      return;
    }
    if (maxP - minP < 1e-15) { minP *= 0.9999; maxP *= 1.0001; }
    const pad = (maxP - minP) * 0.12 || maxP * 0.0001;
    minP -= pad; maxP += pad;
    if (maxT <= minT) maxT = minT + 1;

    // spread band between min/max series at each time — approximate with fill between extreme lines
    ctx.strokeStyle = 'rgba(148,163,184,0.1)';
    ctx.lineWidth = 1;
    for (let i = 0; i <= 4; i++) {
      const y = (h * i) / 4;
      ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(w, y); ctx.stroke();
    }

    // Draw divergence fill: at latest time, vertical band hint
    if (mids.length >= 2) {
      const lo = Math.min(...mids.map(x => x.mid));
      const hi = Math.max(...mids.map(x => x.mid));
      const y1 = h - 8 - ((hi - minP) / (maxP - minP)) * (h - 16);
      const y2 = h - 8 - ((lo - minP) / (maxP - minP)) * (h - 16);
      ctx.fillStyle = 'rgba(45,212,191,0.08)';
      ctx.fillRect(w - 28, Math.min(y1, y2), 20, Math.abs(y2 - y1) || 2);
    }

    venues.forEach((ex, i) => {
      const arr = this.overlayHist[ex] || [];
      if (!arr.length) return;
      const c = this.overlayColors[i % this.overlayColors.length];
      ctx.strokeStyle = c;
      ctx.fillStyle = c;
      ctx.lineWidth = 2;
      ctx.beginPath();
      arr.forEach((pt, idx) => {
        const x = ((pt.t - minT) / (maxT - minT)) * (w - 16) + 8;
        const y = h - 8 - ((pt.mid - minP) / (maxP - minP)) * (h - 16);
        if (idx === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
      });
      if (arr.length === 1) {
        const pt = arr[0];
        const x = ((pt.t - minT) / (maxT - minT)) * (w - 16) + 8;
        const y = h - 8 - ((pt.mid - minP) / (maxP - minP)) * (h - 16);
        ctx.beginPath();
        ctx.arc(x, y, 3, 0, Math.PI * 2);
        ctx.fill();
      } else {
        ctx.stroke();
      }
    });

    ctx.fillStyle = '#64748b';
    ctx.font = '10px monospace';
    ctx.fillText(AB.fmt(maxP, maxP < 1 ? 6 : 4), 6, 12);
    ctx.fillText(AB.fmt(minP, minP < 1 ? 6 : 4), 6, h - 6);
  },

  _overlayMsg(canvas, msg, ctx, w, h) {
    if (!ctx) {
      const dpr = window.devicePixelRatio || 1;
      w = canvas.clientWidth || 900;
      h = 220;
      canvas.width = Math.floor(w * dpr);
      canvas.height = Math.floor(h * dpr);
      ctx = canvas.getContext('2d');
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    }
    ctx.fillStyle = '#0a0e14';
    ctx.fillRect(0, 0, w, h);
    ctx.fillStyle = '#64748b';
    ctx.font = '12px sans-serif';
    ctx.fillText(msg, 16, h / 2);
  },

  loadChart(symbol) {
    if (!symbol) return;
    if (symbol !== this._overlaySym) {
      this._overlaySym = symbol;
      this.overlayHist = {};
    }
    const interval = AB.$('m_interval')?.value || '15';
    const key = symbol + '|' + interval;
    if (key === this.chartKey) return;
    this.chartKey = key;

    // TradingView embed (iframe) — reliable vs JS widget on hidden tabs
    const base = symbol.replace(/USDT$/i, '');
    const tvSym = encodeURIComponent('BINANCE:' + base + 'USDT.P');
    const src =
      'https://s.tradingview.com/widgetembed/' +
      '?frameElementId=tv_frame' +
      '&symbol=' + tvSym +
      '&interval=' + encodeURIComponent(interval) +
      '&hidesidetoolbar=0' +
      '&symboledit=1' +
      '&saveimage=0' +
      '&toolbarbg=0c121c' +
      '&studies=%5B%5D' +
      '&theme=dark' +
      '&style=1' +
      '&timezone=Etc%2FUTC' +
      '&withdateranges=1' +
      '&hideideas=1' +
      '&hidevolume=0' +
      '&locale=en';

    const frame = AB.$('tv_frame');
    const ph = AB.$('tv_placeholder');
    if (ph) ph.style.display = 'none';
    if (frame) {
      frame.style.display = 'block';
      frame.src = src;
    }
    if (AB.$('m_chartSrc'))
      AB.$('m_chartSrc').textContent = 'BINANCE:' + base + 'USDT.P · ' + interval + 'm';
  },

  renderDepth(data) {
    const el = AB.$('m_depth');
    if (!el) return;
    const depth = (data.orderBookDepth || {})[this.selected];
    if (!depth || typeof depth !== 'object') {
      el.innerHTML = '<div class="empty">No depth for ' + (this.selected || '—') + '</div>';
      return;
    }
    const venues = Object.keys(depth);
    if (!venues.length) {
      el.innerHTML = '<div class="empty">Books not synced yet</div>';
      return;
    }
    el.innerHTML = venues.map(ex => {
      const book = depth[ex] || {};
      const bids = book.bids || book.Bids || [];
      const asks = book.asks || book.Asks || [];
      const maxB = Math.max(...bids.map(x => Number(x[1] || x.qty || 0)), 1e-12);
      const maxA = Math.max(...asks.map(x => Number(x[1] || x.qty || 0)), 1e-12);
      const n = Math.max(bids.length, asks.length, 1);
      let rows = '';
      for (let i = 0; i < Math.min(n, 12); i++) {
        const b = bids[i];
        const a = asks[i];
        const bp = b ? Number(b[0] ?? b.price) : null;
        const bq = b ? Number(b[1] ?? b.qty) : null;
        const ap = a ? Number(a[0] ?? a.price) : null;
        const aq = a ? Number(a[1] ?? a.qty) : null;
        const bw = bq != null ? Math.min(100, (bq / maxB) * 100) : 0;
        const aw = aq != null ? Math.min(100, (aq / maxA) * 100) : 0;
        rows += `<div class="ob-row">
          <div class="ob-cell bid" style="--w:${bw}%"><span class="pos">${bp != null ? AB.fmt(bp, bp < 1 ? 5 : 2) : ''}</span></div>
          <div class="muted" style="text-align:center;font-size:9px">${i + 1}</div>
          <div class="ob-cell ask" style="--w:${aw}%"><span class="neg">${ap != null ? AB.fmt(ap, ap < 1 ? 5 : 2) : ''}</span></div>
        </div>`;
      }
      return `<div class="ob-ex">
        <div class="ob-ex-hd"><span>${ex}</span><span class="muted mono">${this.selected}</span></div>
        ${rows}
      </div>`;
    }).join('');
  },

  renderTape(data) {
    const books = data.bookTickers || {};
    const rows = [];
    for (const [symbol, byE] of Object.entries(books)) {
      if (this.selected && symbol !== this.selected) continue;
      for (const [ex, t] of Object.entries(byE)) {
        const mid = (Number(t.bestBid) + Number(t.bestAsk)) / 2;
        const spr = mid > 0 ? (Number(t.bestAsk) - Number(t.bestBid)) / mid * 100 : 0;
        rows.push({ symbol, ex, ...t, spr });
      }
    }
    if (AB.$('m_bookBody')) {
      AB.$('m_bookBody').innerHTML = rows.length
        ? rows.map(r => `<tr>
            <td class="mono" style="color:var(--blue)">${r.symbol}</td>
            <td>${r.ex}</td>
            <td class="mono pos">${AB.fmt(r.bestBid)}</td>
            <td class="mono neg">${AB.fmt(r.bestAsk)}</td>
            <td class="mono muted">${AB.fmt(r.spr, 4)}%</td>
          </tr>`).join('')
        : '<tr><td colspan="5" class="empty">No tickers</td></tr>';
    }
  },

  onTick(data) {
    if (!data) return;
    const now = Date.now();
    const fp = JSON.stringify(data.orderBookDepth?.[this.selected] || {}).slice(0, 600);
    if (fp !== this.depthFp && now - this.lastDepthPaint > 800) {
      this.depthFp = fp;
      this.lastDepthPaint = now;
      this.renderDepth(data);
      this.renderTape(data);
    }
    this.syncLastPrice(data);
    if (AB.$('m_streams') && data.streamsLive != null)
      AB.$('m_streams').textContent = `streams ${data.streamsLive}/${data.streamsTotal ?? '—'}`;
  },

  onShow(data) {
    if (data) this.render(data);
    else if (AB.state.snapshot) this.render(AB.state.snapshot);
    // force chart reload when opening Market tab
    this.chartKey = '';
    if (this.selected) this.loadChart(this.selected);
  }
};

document.getElementById('m_symbol')?.addEventListener('change', (e) => {
  AB.pages.market.selected = e.target.value;
  AB.pages.market.chartKey = '';
  if (AB.state.snapshot) AB.pages.market.render(AB.state.snapshot);
});
document.getElementById('m_interval')?.addEventListener('change', () => {
  AB.pages.market.chartKey = '';
  if (AB.pages.market.selected) AB.pages.market.loadChart(AB.pages.market.selected);
});
