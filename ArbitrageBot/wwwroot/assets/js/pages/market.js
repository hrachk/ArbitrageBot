AB.pages.market = {
  selected: '',
  render(data) {
    const opps = data.opportunities || [];
    const depth = data.orderBookDepth || {};
    const symbols = [...new Set([...(data.symbols||[]), ...Object.keys(depth), ...opps.map(o=>o.symbol)])];
    const sel = AB.$('m_symbol');
    const cur = this.selected || sel.value || symbols[0] || '';
    sel.innerHTML = symbols.map(s => `<option value="${s}">${s}</option>`).join('') || '<option>—</option>';
    if (cur && [...sel.options].some(o => o.value === cur)) { sel.value = cur; this.selected = cur; }

    AB.$('m_oppBody').innerHTML = opps.length ? opps.map(o => {
      const active = o.symbol === this.selected ? 'style="background:rgba(34,211,238,.08)"' : '';
      return `<tr data-sym="${o.symbol}" ${active} style="cursor:pointer">
        <td class="mono" style="color:var(--cyan)">${o.symbol}</td>
        <td class="mono" style="font-size:11px"><span class="pos">${o.buyExchange||o.longExchange}</span>→<span class="neg">${o.sellExchange||o.shortExchange}</span></td>
        <td class="mono">${AB.fmt(o.buyPriceVwap||o.longAskVwap)} / ${AB.fmt(o.sellPriceVwap||o.shortBidVwap)}</td>
        <td>${AB.fmtPct(o.grossSpreadVwapPercent)}</td>
        <td>${AB.fmtPct(o.netProfitPercent)}</td>
        <td class="mono">${AB.fmt(o.netProfitQuote||o.estNetPnlUsd,2)}</td>
      </tr>`;
    }).join('') : '<tr><td colspan="6" class="muted" style="text-align:center;padding:28px">No opportunities</td></tr>';

    document.querySelectorAll('#m_oppBody tr[data-sym]').forEach(tr => {
      tr.onclick = () => { this.selected = tr.dataset.sym; this.render(data); };
    });

    this.renderDepth(data);

    // tickers
    const books = data.bookTickers || {};
    const rows = [];
    for (const [symbol, byE] of Object.entries(books)) {
      for (const [ex, t] of Object.entries(byE)) {
        const mid = (Number(t.bestBid)+Number(t.bestAsk))/2;
        const spr = mid > 0 ? (Number(t.bestAsk)-Number(t.bestBid))/mid*100 : 0;
        rows.push({ symbol, ex, ...t, spr });
      }
    }
    rows.sort((a,b)=>a.symbol.localeCompare(b.symbol)||a.ex.localeCompare(b.ex));
    AB.$('m_bookBody').innerHTML = rows.map(r => `<tr>
      <td class="mono" style="color:var(--cyan)">${r.symbol}</td><td>${r.ex}</td>
      <td class="mono pos">${AB.fmt(r.bestBid)}</td>
      <td class="mono neg">${AB.fmt(r.bestAsk)}</td>
      <td class="mono muted">${AB.fmt(r.spr,4)}%</td>
    </tr>`).join('') || '<tr><td colspan="5" class="muted" style="text-align:center;padding:20px">—</td></tr>';
  },
  renderDepth(data) {
    const depth = data.orderBookDepth || {};
    const panels = AB.$('m_depth');
    if (!this.selected || !depth[this.selected]) {
      panels.innerHTML = '<div class="muted" style="padding:40px;text-align:center">Select a pair to view order books</div>';
      return;
    }
    const byEx = depth[this.selected];
    panels.innerHTML = Object.entries(byEx).map(([ex, book]) => {
      const bids = book.bids || [], asks = book.asks || [];
      const maxQ = Math.max(1, ...bids.map(x=>Number(x.qty||0)), ...asks.map(x=>Number(x.qty||0)));
      const n = Math.max(bids.length, asks.length, 1);
      let rows = '';
      for (let i=0;i<n;i++) {
        const b=bids[i], a=asks[i];
        const bw=b?Math.min(100,Number(b.qty)/maxQ*100):0;
        const aw=a?Math.min(100,Number(a.qty)/maxQ*100):0;
        rows += `<div class="ob-grid">
          <div class="ob-bar ob-bid pos" style="--w:${bw}%;text-align:right">${b?`<span>${AB.fmt(b.qty,4)}</span> <span>${AB.fmt(b.price)}</span>`:''}</div>
          <div class="muted" style="text-align:center;font-size:9px">${i+1}</div>
          <div class="ob-bar ob-ask neg" style="--w:${aw}%">${a?`<span>${AB.fmt(a.price)}</span> <span>${AB.fmt(a.qty,4)}</span>`:''}</div>
        </div>`;
      }
      return `<div class="card" style="margin-bottom:10px">
        <div style="display:flex;justify-content:space-between;margin-bottom:8px">
          <strong style="color:var(--cyan)">${ex}</strong>
          <span class="muted mono" style="font-size:10px">${book.source||'book'}</span>
        </div>
        <div class="ob-grid muted" style="font-size:9px;margin-bottom:4px"><div style="text-align:right">qty · bid</div><div></div><div>ask · qty</div></div>
        ${rows}
      </div>`;
    }).join('');
  },
  onShow(data) { if (data) this.render(data); }
};
document.getElementById('m_symbol')?.addEventListener('change', (e) => {
  AB.pages.market.selected = e.target.value;
  if (AB.state.snapshot) AB.pages.market.render(AB.state.snapshot);
});
