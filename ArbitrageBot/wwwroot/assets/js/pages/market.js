AB.pages.market = {
  selected: '',
  depthFp: '',
  lastDepthPaint: 0,
  chartKey: '',

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

  loadChart(symbol) {
    if (!symbol) return;
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
