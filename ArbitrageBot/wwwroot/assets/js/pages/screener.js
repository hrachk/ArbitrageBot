AB.pages = AB.pages || {};
AB.pages.screener = {
  selected: '',
  sortKey: 'intensity',
  hist: {},
  colors: ['#2dd4bf', '#60a5fa', '#f472b6', '#fbbf24', '#a78bfa', '#34d399'],
  rows: [],

  render(data) {
    if (!data) return;
    this._data = data;
    this.rows = this.buildRows(data);
    this.applyFilters();
    this.paintTable();
    this.paintKpis();
    if (this.selected) this.paintDetail(this.selected);
  },

  buildRows(data) {
    const symbols = (data.symbols || []).slice();
    const disc = {};
    (data.discoveredSymbols || []).forEach(d => {
      const s = (d.symbol || d.Symbol || '').toUpperCase();
      if (s) disc[s] = d;
    });
    const oppsBySym = {};
    (data.opportunities || []).forEach(o => {
      const s = (o.symbol || '').toUpperCase();
      if (!s) return;
      const net = Number(o.netSpreadPercent != null ? o.netSpreadPercent : o.netProfitPercent) || 0;
      if (!oppsBySym[s] || net > oppsBySym[s].net) {
        oppsBySym[s] = {
          net,
          route: (o.longExchange || o.buyExchange || '?') + '→' + (o.shortExchange || o.sellExchange || '?')
        };
      }
    });
    const books = data.bookTickers || {};
    const now = Date.now();

    return symbols.map(sym => {
      const S = String(sym).toUpperCase();
      const d = disc[S] || {};
      const byEx = books[S] || books[sym] || {};
      const venues = Object.keys(byEx);
      const mids = [];
      venues.forEach(ex => {
        const b = byEx[ex] || {};
        const bid = Number(b.bestBid ?? b.BestBid ?? 0);
        const ask = Number(b.bestAsk ?? b.BestAsk ?? 0);
        if (bid > 0 && ask > 0) {
          const mid = (bid + ask) / 2;
          mids.push({ ex, mid, bid, ask });
          if (!this.hist[S]) this.hist[S] = {};
          if (!this.hist[S][ex]) this.hist[S][ex] = [];
          const arr = this.hist[S][ex];
          const last = arr[arr.length - 1];
          if (!last || now - last.t >= 400) {
            arr.push({ t: now, mid });
            while (arr.length > 120) arr.shift();
          } else {
            last.mid = mid;
            last.t = now;
          }
        }
      });
      let crossDelta = 0;
      if (mids.length >= 2) {
        const lo = Math.min(...mids.map(x => x.mid));
        const hi = Math.max(...mids.map(x => x.mid));
        crossDelta = lo > 0 ? ((hi - lo) / lo) * 100 : 0;
      }
      const vol = Number(d.medianQuoteVolume ?? d.MedianQuoteVolume ?? 0) || 0;
      const depth = Number(d.depthScore ?? d.DepthScore ?? 0) || 0;
      const exCount = Number(d.exchangeCount ?? d.ExchangeCount ?? venues.length) || venues.length;
      const arb = oppsBySym[S];
      const net = arb ? arb.net : 0;

      // Intensity 0–100: volume rank proxy + cross gap + depth + arb
      const volScore = vol > 0 ? Math.min(40, Math.log10(vol + 1) * 4) : 0;
      const deltaScore = Math.min(30, crossDelta * 40);
      const depthScore = Math.min(15, depth * 5);
      const arbScore = Math.min(15, Math.max(0, net) * 25);
      const intensity = Math.round(Math.min(100, volScore + deltaScore + depthScore + arbScore));

      return {
        symbol: S,
        intensity,
        venues: exCount,
        venueList: mids.map(m => m.ex),
        vol,
        depth,
        crossDelta,
        net,
        route: arb ? arb.route : '',
        mids
      };
    });
  },

  applyFilters() {
    const q = (AB.$('scr_q')?.value || '').trim().toUpperCase();
    const multi = AB.$('scr_multi')?.checked;
    const depthOnly = AB.$('scr_depth')?.checked;
    let rows = this.rows.slice();
    if (q) rows = rows.filter(r => r.symbol.includes(q));
    if (multi) rows = rows.filter(r => r.venues >= 2);
    if (depthOnly) rows = rows.filter(r => r.depth >= 1);
    const sk = AB.$('scr_sort')?.value || this.sortKey;
    this.sortKey = sk;
    const dir = -1;
    rows.sort((a, b) => {
      const keys = { intensity: 'intensity', delta: 'crossDelta', vol: 'vol', net: 'net', depth: 'depth', symbol: 'symbol' };
      const k = keys[sk] || 'intensity';
      if (k === 'symbol') return a.symbol.localeCompare(b.symbol);
      return (Number(a[k]) - Number(b[k])) * dir;
    });
    this._view = rows;
  },

  paintKpis() {
    const rows = this._view || [];
    if (AB.$('scr_n')) AB.$('scr_n').textContent = String(rows.length);
    if (AB.$('scr_hot')) AB.$('scr_hot').textContent = String(rows.filter(r => r.intensity >= 70).length);
    const maxD = rows.reduce((m, r) => Math.max(m, r.crossDelta || 0), 0);
    if (AB.$('scr_maxDelta')) AB.$('scr_maxDelta').textContent = maxD ? maxD.toFixed(3) + '%' : '—';
    if (AB.$('scr_arbN')) AB.$('scr_arbN').textContent = String(rows.filter(r => r.net > 0).length);
    if (AB.$('scr_hint')) {
      AB.$('scr_hint').textContent = (this._data && this._data.discoverySource)
        ? ('discovery: ' + this._data.discoverySource)
        : '';
    }
  },

  paintTable() {
    const body = AB.$('scr_body');
    if (!body) return;
    const rows = this._view || [];
    if (!rows.length) {
      body.innerHTML = '<tr><td colspan="7" class="empty">No pairs match filters / waiting for books…</td></tr>';
      return;
    }
    const maxInt = Math.max(...rows.map(r => r.intensity), 1);
    body.innerHTML = rows.map(r => {
      const heat = r.intensity >= 70 ? 'hot' : (r.intensity >= 40 ? 'warm' : 'cool');
      const w = Math.max(4, (r.intensity / maxInt) * 100);
      const act = r.symbol === this.selected ? ' active' : '';
      const dlt = r.crossDelta > 0
        ? '<span class="pos mono">+' + r.crossDelta.toFixed(3) + '%</span>'
        : '<span class="muted">—</span>';
      const net = r.net > 0
        ? '<span class="pos mono">+' + r.net.toFixed(3) + '%</span>'
        : '<span class="muted">—</span>';
      const vol = r.vol >= 1e6 ? (r.vol / 1e6).toFixed(1) + 'M'
        : (r.vol >= 1e3 ? (r.vol / 1e3).toFixed(0) + 'K' : (r.vol ? r.vol.toFixed(0) : '—'));
      return '<tr class="scr-row' + act + '" data-sym="' + r.symbol + '">' +
        '<td><div class="scr-heat ' + heat + '">' + r.intensity +
        '</div><div class="scr-int-bar" style="margin-top:4px"><i style="width:' + w + '%"></i></div></td>' +
        '<td class="mono" style="font-weight:700;color:var(--blue)">' + r.symbol + '</td>' +
        '<td class="mono">' + r.venues + '</td>' +
        '<td class="mono">' + vol + '</td>' +
        '<td class="mono">' + (r.depth ? r.depth.toFixed(1) + '×' : '—') + '</td>' +
        '<td>' + dlt + '</td>' +
        '<td>' + net + '</td></tr>';
    }).join('');

    body.querySelectorAll('tr.scr-row').forEach(tr => {
      tr.onclick = () => {
        this.selected = tr.getAttribute('data-sym');
        this.paintTable();
        this.paintDetail(this.selected);
      };
    });
  },

  paintDetail(sym) {
    const row = (this.rows || []).find(r => r.symbol === sym);
    const title = AB.$('scr_selTitle');
    const sub = AB.$('scr_selSub');
    const chips = AB.$('scr_chips');
    const meta = AB.$('scr_meta');
    const btn = AB.$('scr_toMarket');
    if (title) title.textContent = sym || 'Select a pair';
    if (sub) sub.textContent = row ? ('intensity ' + row.intensity + ' · ' + row.venues + ' venues') : 'detail';
    if (btn) {
      btn.disabled = !sym;
      btn.onclick = () => {
        if (!sym) return;
        if (AB.pages.market) AB.pages.market.selected = sym;
        document.querySelectorAll('.nav-item').forEach(b => {
          b.classList.toggle('active', b.getAttribute('data-page') === 'market');
        });
        document.querySelectorAll('.page').forEach(p => {
          p.classList.toggle('active', p.getAttribute('data-page') === 'market');
        });
        AB.state.page = 'market';
        if (AB.pages.market && this._data) AB.pages.market.render(this._data);
      };
    }
    if (!row) {
      if (chips) chips.innerHTML = '';
      if (meta) meta.textContent = 'No data for this symbol.';
      this.drawChart(sym, []);
      return;
    }
    if (chips) {
      chips.innerHTML = (row.mids || []).map((m, i) => {
        const c = this.colors[i % this.colors.length];
        return '<span class="scr-venue-chip"><span class="dot" style="background:' + c + '"></span>' +
          m.ex + ' <span class="mono">' + AB.fmt(m.mid, m.mid < 1 ? 6 : 4) + '</span></span>';
      }).join('') || '<span class="muted">No live mids</span>';
    }
    if (meta) {
      meta.innerHTML =
        '<b>Intensity</b> = volume + cross-Δ + depth + arb signal<br>' +
        'Cross Δ <b>' + (row.crossDelta ? row.crossDelta.toFixed(3) + '%' : '—') + '</b>' +
        (row.route ? ' · Best route <span class="mono">' + row.route + '</span>' : '') +
        (row.net > 0 ? ' · Net <span class="pos">+' + row.net.toFixed(3) + '%</span>' : '') +
        '<br>Depth score <b>' + (row.depth ? row.depth.toFixed(2) + '×' : '—') + '</b> · 24h vol proxy <b>' +
        (row.vol >= 1e6 ? (row.vol / 1e6).toFixed(1) + 'M' : row.vol || '—') + '</b>';
    }
    this.drawChart(sym, row.mids || []);
  },

  drawChart(sym, mids) {
    const canvas = AB.$('scr_chart');
    if (!canvas) return;
    const dpr = window.devicePixelRatio || 1;
    const w = canvas.clientWidth || 480;
    const h = 200;
    canvas.width = Math.floor(w * dpr);
    canvas.height = Math.floor(h * dpr);
    const ctx = canvas.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.fillStyle = '#0a0e14';
    ctx.fillRect(0, 0, w, h);

    const hist = (this.hist[sym] || {});
    const venues = Object.keys(hist);
    let minP = Infinity, maxP = -Infinity, minT = Infinity, maxT = -Infinity;
    venues.forEach(ex => {
      (hist[ex] || []).forEach(p => {
        minP = Math.min(minP, p.mid); maxP = Math.max(maxP, p.mid);
        minT = Math.min(minT, p.t); maxT = Math.max(maxT, p.t);
      });
    });
    if (!venues.length || !isFinite(minP)) {
      ctx.fillStyle = '#64748b';
      ctx.font = '12px sans-serif';
      ctx.fillText('Waiting for live mids…', 16, h / 2);
      return;
    }
    if (maxP - minP < 1e-15) { minP *= 0.9999; maxP *= 1.0001; }
    const pad = (maxP - minP) * 0.12 || maxP * 0.0001;
    minP -= pad; maxP += pad;
    if (maxT <= minT) maxT = minT + 1;

    ctx.strokeStyle = 'rgba(148,163,184,0.08)';
    for (let i = 0; i <= 4; i++) {
      const y = (h * i) / 4;
      ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(w, y); ctx.stroke();
    }

    venues.forEach((ex, i) => {
      const arr = hist[ex] || [];
      if (arr.length < 2) {
        if (arr.length === 1) {
          const p = arr[0];
          const x = w / 2, y = h - 8 - ((p.mid - minP) / (maxP - minP)) * (h - 16);
          ctx.fillStyle = this.colors[i % this.colors.length];
          ctx.beginPath(); ctx.arc(x, y, 4, 0, Math.PI * 2); ctx.fill();
        }
        return;
      }
      ctx.strokeStyle = this.colors[i % this.colors.length];
      ctx.lineWidth = 2;
      ctx.beginPath();
      arr.forEach((p, idx) => {
        const x = ((p.t - minT) / (maxT - minT)) * (w - 16) + 8;
        const y = h - 8 - ((p.mid - minP) / (maxP - minP)) * (h - 16);
        if (idx === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
      });
      ctx.stroke();
    });

    ctx.fillStyle = '#64748b';
    ctx.font = '10px monospace';
    ctx.fillText(AB.fmt(maxP, maxP < 1 ? 6 : 4), 6, 12);
    ctx.fillText(AB.fmt(minP, minP < 1 ? 6 : 4), 6, h - 6);

    // cross band
    if (mids.length >= 2) {
      const lo = Math.min(...mids.map(m => m.mid));
      const hi = Math.max(...mids.map(m => m.mid));
      const y1 = h - 8 - ((hi - minP) / (maxP - minP)) * (h - 16);
      const y2 = h - 8 - ((lo - minP) / (maxP - minP)) * (h - 16);
      ctx.fillStyle = 'rgba(45,212,191,0.1)';
      ctx.fillRect(w - 24, Math.min(y1, y2), 14, Math.abs(y2 - y1) || 2);
    }
  }
};

document.addEventListener('DOMContentLoaded', () => {
  const bind = () => {
    ['scr_q', 'scr_multi', 'scr_depth', 'scr_sort'].forEach(id => {
      const el = document.getElementById(id);
      if (!el || el._scrBound) return;
      el._scrBound = true;
      el.addEventListener('input', () => {
        if (AB.pages.screener._data) AB.pages.screener.render(AB.pages.screener._data);
      });
      el.addEventListener('change', () => {
        if (AB.pages.screener._data) AB.pages.screener.render(AB.pages.screener._data);
      });
    });
  };
  bind();
  setTimeout(bind, 500);
});
