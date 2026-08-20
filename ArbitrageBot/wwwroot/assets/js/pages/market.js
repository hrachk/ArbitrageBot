AB.pages.market = {
  selected: '',
  depthFp: '',
  lastDepthPaint: 0,
  chart: null,
  candleSeries: null,
  chartSymbol: '',
  chartInterval: '15m',
  loadingChart: false,

  tvWidget: null,

  loadChart(symbol, interval) {
    if (!symbol || typeof TradingView === 'undefined') return;
    const iv = ({ '1m': '1', '5m': '5', '15m': '15', '1h': '60' })[interval] || '15';
    if (symbol === this.chartSymbol && iv === this.chartInterval && this.tvWidget) return;
    this.chartSymbol = symbol;
    this.chartInterval = iv;
    const el = AB.$('tv_chart');
    if (!el) return;
    el.innerHTML = '';
    // USDT-M perpetual on TradingView
    const tvSymbol = 'BINANCE:' + symbol.replace('USDT', '') + 'USDT.P';
    try {
      this.tvWidget = new TradingView.widget({
        autosize: true,
        symbol: tvSymbol,
        interval: iv,
        timezone: 'Etc/UTC',
        theme: 'dark',
        style: '1',
        locale: 'en',
        toolbar_bg: '#0a101c',
        enable_publishing: false,
        hide_top_toolbar: false,
        hide_legend: false,
        save_image: false,
        container_id: 'tv_chart',
        backgroundColor: '#0a101c',
        gridColor: 'rgba(30,41,59,0.5)',
        studies: ['Volume@tv-basicstudies'],
      });
      AB.$('m_chartSrc').textContent = tvSymbol + ' · ' + interval;
    } catch (e) {
      console.warn(e);
      AB.$('m_chartSrc').textContent = 'TradingView error';
    }
  },

  render(data) {
    const opps = data.opportunities || [];
    const depth = data.orderBookDepth || {};
    const fp = data.futuresPaper || data.paper || {};
    const symbols = [...new Set([...(data.symbols || []), ...Object.keys(depth), ...opps.map(o => o.symbol)])];
    const sel = AB.$('m_symbol');
    if (!sel) return;
    const cur = this.selected || sel.value || symbols[0] || '';
    sel.innerHTML = symbols.map(s => `<option value="${s}">${s}</option>`).join('') || '<option>—</option>';
    if (cur && [...sel.options].some(o => o.value === cur)) {
      sel.value = cur;
      this.selected = cur;
    }

    // header metrics for selected
    const forSym = opps.filter(o => o.symbol === this.selected);
    if (forSym.length) {
      const best = forSym.reduce((a, b) => Number(a.netProfitPercent) > Number(b.netProfitPercent) ? a : b);
      AB.$('m_bestNet').innerHTML = AB.fmtPct(best.netProfitPercent);
      AB.$('m_route').innerHTML = `<span class="pos">${best.buyExchange || best.longExchange}</span> long → <span class="neg">${best.sellExchange || best.shortExchange}</span> short`;
    } else {
      AB.$('m_bestNet').textContent = '—';
      AB.$('m_route').textContent = 'no signal for pair';
    }

    // opportunities table
    const ordered = [...opps].sort((a, b) => Number(b.netProfitPercent || 0) - Number(a.netProfitPercent || 0));
    AB.$('m_oppBody').innerHTML = ordered.length ? ordered.map(o => {
      const active = o.symbol === this.selected ? 'background:rgba(34,211,238,.1)' : '';
      return `<tr data-sym="${o.symbol}" style="cursor:pointer;${active}">
        <td class="mono" style="color:var(--cyan)">${o.symbol}</td>
        <td class="mono" style="font-size:11px"><span class="pos">${o.buyExchange || o.longExchange}</span>→<span class="neg">${o.sellExchange || o.shortExchange}</span></td>
        <td>${AB.fmtPct(o.netProfitPercent)}</td>
        <td class="muted mono" style="font-size:11px">${o.grossSpreadVwapPercent != null ? AB.fmt(o.grossSpreadVwapPercent, 3) + '%' : '—'}</td>
        <td class="mono">${AB.fmt(o.netProfitQuote || o.estNetPnlUsd, 2)}</td>
        <td>${o.fullyFilled ? '<span class="pos">full</span>' : '<span style="color:var(--amber)">part</span>'}</td>
      </tr>`;
    }).join('') : '<tr><td colspan="6" class="muted" style="text-align:center;padding:24px">No signals above min open edge — wait for dislocation</td></tr>';

    document.querySelectorAll('#m_oppBody tr[data-sym]').forEach(tr => {
      tr.onclick = () => {
        this.selected = tr.dataset.sym;
        this.render(data);
        this.loadChart(this.selected, AB.$('m_interval')?.value || '15m');
      };
    });

    this.renderDepth(data);
    this.depthFp = JSON.stringify(data.orderBookDepth?.[this.selected] || {}).slice(0, 800);
    this.lastDepthPaint = Date.now();
    this.renderPositions(fp);

    // ticker tape
    const books = data.bookTickers || {};
    const rows = [];
    for (const [symbol, byE] of Object.entries(books)) {
      for (const [ex, t] of Object.entries(byE)) {
        const mid = (Number(t.bestBid) + Number(t.bestAsk)) / 2;
        const spr = mid > 0 ? (Number(t.bestAsk) - Number(t.bestBid)) / mid * 100 : 0;
        rows.push({ symbol, ex, ...t, spr });
      }
    }
    rows.sort((a, b) => a.symbol.localeCompare(b.symbol) || a.ex.localeCompare(b.ex));
    const tape = this.selected ? rows.filter(r => r.symbol === this.selected) : rows;
    AB.$('m_bookBody').innerHTML = tape.length ? tape.map(r => `<tr>
      <td class="mono" style="color:var(--cyan)">${r.symbol}</td><td>${r.ex}</td>
      <td class="mono pos">${AB.fmt(r.bestBid)}</td>
      <td class="mono neg">${AB.fmt(r.bestAsk)}</td>
      <td class="mono muted">${AB.fmt(r.spr, 4)}%</td>
    </tr>`).join('') : '<tr><td colspan="5" class="muted" style="text-align:center;padding:16px">—</td></tr>';

    if (this.selected) this.loadChart(this.selected, AB.$('m_interval')?.value || '15m');
  },

  renderDepth(data) {
    const depth = data.orderBookDepth || {};
    const panels = AB.$('m_depth');
    if (!panels) return;
    if (!this.selected || !depth[this.selected]) {
      panels.innerHTML = '<div class="muted" style="padding:32px;text-align:center">Select a pair — books appear here without hunting</div>';
      return;
    }
    const byEx = depth[this.selected];
    panels.innerHTML = Object.entries(byEx).map(([ex, book]) => {
      const bids = (book.bids || []).slice(0, 12);
      const asks = (book.asks || []).slice(0, 12);
      const maxQ = Math.max(1, ...bids.map(x => Number(x.qty || 0)), ...asks.map(x => Number(x.qty || 0)));
      const n = Math.max(bids.length, asks.length, 1);
      let rows = '';
      for (let i = 0; i < n; i++) {
        const b = bids[i], a = asks[i];
        const bw = b ? Math.min(100, Number(b.qty) / maxQ * 100) : 0;
        const aw = a ? Math.min(100, Number(a.qty) / maxQ * 100) : 0;
        rows += `<div class="ob-grid">
          <div class="ob-bar ob-bid pos" style="--w:${bw}%;text-align:right">${b ? `<span>${AB.fmt(b.qty, 4)}</span> <span>${AB.fmt(b.price)}</span>` : ''}</div>
          <div class="muted" style="text-align:center;font-size:9px">${i + 1}</div>
          <div class="ob-bar ob-ask neg" style="--w:${aw}%">${a ? `<span>${AB.fmt(a.price)}</span> <span>${AB.fmt(a.qty, 4)}</span>` : ''}</div>
        </div>`;
      }
      const bestBid = bids[0]?.price, bestAsk = asks[0]?.price;
      const mid = bestBid && bestAsk ? (Number(bestBid) + Number(bestAsk)) / 2 : 0;
      const spr = mid ? ((Number(bestAsk) - Number(bestBid)) / mid * 100) : 0;
      return `<div class="ob-venue">
        <div class="ob-venue-head">
          <strong style="color:var(--cyan)">${ex}</strong>
          <span class="mono muted" style="font-size:10px">${book.source || 'book'} · spr ${AB.fmt(spr, 4)}%</span>
        </div>
        <div class="ob-grid muted" style="font-size:9px;margin-bottom:4px"><div style="text-align:right">qty · bid</div><div></div><div>ask · qty</div></div>
        <div class="ob-levels">${rows}</div>
      </div>`;
    }).join('');
  },

  renderPositions(fp) {
    const positions = fp.positions || [];
    const el = AB.$('m_positions');
    if (!el) return;
    if (!positions.length) {
      el.innerHTML = '<div class="empty-state">No open paper hedges</div>';
      return;
    }
    el.innerHTML = positions.map(p => AB.posCardHtml(p)).join('');
  },

  onTick(data, tick) {
    if (!data) return;
    // Throttle DOM depth paint — prevents infinite flicker (data is WS memory, not REST)
    const now = Date.now();
    const fp = JSON.stringify(data.orderBookDepth?.[this.selected] || {}).slice(0, 800);
    if (fp !== this.depthFp && now - this.lastDepthPaint > 700) {
      this.depthFp = fp;
      this.lastDepthPaint = now;
      this.renderDepth(data);
    }
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
    // Update last price often (cheap); tape table only with depth throttle
    if (this.selected && books[this.selected]) {
      const first = Object.values(books[this.selected])[0];
      if (first && AB.$('m_last')) {
        const mid = (Number(first.bestBid) + Number(first.bestAsk)) / 2;
        AB.$('m_last').textContent = AB.fmt(mid, mid < 1 ? 6 : 4);
      }
    }
    if (now - this.lastDepthPaint < 50 && AB.$('m_bookBody') && rows.length) {
      // painted depth this tick — refresh tape once
      AB.$('m_bookBody').innerHTML = rows.map(r => `<tr>
        <td class="mono" style="color:var(--cyan)">${r.symbol}</td><td>${r.ex}</td>
        <td class="mono pos">${AB.fmt(r.bestBid)}</td>
        <td class="mono neg">${AB.fmt(r.bestAsk)}</td>
        <td class="mono muted">${AB.fmt(r.spr, 4)}%</td>
      </tr>`).join('');
    }
  },

  onShow(data) {
    if (data) this.render(data);
    if (this.selected) this.loadChart(this.selected, AB.$('m_interval')?.value || '15m');
  }
};

document.getElementById('m_symbol')?.addEventListener('change', (e) => {
  AB.pages.market.selected = e.target.value;
  AB.pages.market.chartSymbol = ''; // force reload
  if (AB.state.snapshot) AB.pages.market.render(AB.state.snapshot);
});
document.getElementById('m_interval')?.addEventListener('change', () => {
  AB.pages.market.chartSymbol = '';
  const sym = AB.pages.market.selected;
  if (sym) AB.pages.market.loadChart(sym, AB.$('m_interval').value);
});
