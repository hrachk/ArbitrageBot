AB.pages = AB.pages || {};
AB.pages.screener = {
  selected: '',
  sortKey: 'intensity',
  mode: 'intensity',
  _midHist: {},
  _maxHist: 180,
  hist: {},
  colors: ['#2dd4bf', '#60a5fa', '#f472b6', '#fbbf24', '#a78bfa', '#34d399'],
  rows: [],


  /** Push mid sample for brush detector (call every snapshot). */
  pushMidSample(symbol, mid) {
    if (!symbol || !(mid > 0)) return;
    const h = this._midHist[symbol] || (this._midHist[symbol] = []);
    h.push({ t: Date.now(), m: mid });
    const max = this._maxHist || 180;
    if (h.length > max) h.splice(0, h.length - max);
  },

  /**
   * Ёршик / MM-range score 0–100 from mid path.
   * High score = tight corridor + many reversals + weak net trend (пила).
   */
  scoreBrush(symbol) {
    const h = this._midHist[symbol] || [];
    const ms = h.map(x => x.m).filter(m => m > 0);
    const n = ms.length;
    if (n < 12) {
      return { score: 0, rangePct: 0, osc: 0, cross: 0, trend: 1, samples: n, regime: 'cold', note: 'мало точек — копим историю mid' };
    }
    let hi = ms[0], lo = ms[0];
    for (const m of ms) { if (m > hi) hi = m; if (m < lo) lo = m; }
    const mid = (hi + lo) / 2;
    const range = hi - lo;
    const rangePct = mid > 0 ? (range / mid) * 100 : 0;
    let osc = 0;
    for (let i = 2; i < n; i++) {
      const d0 = ms[i - 1] - ms[i - 2];
      const d1 = ms[i] - ms[i - 1];
      if (d0 === 0 || d1 === 0) continue;
      if (d0 * d1 < 0) osc++;
    }
    let cross = 0;
    for (let i = 1; i < n; i++) {
      if ((ms[i - 1] - mid) * (ms[i] - mid) < 0) cross++;
    }
    const trend = range > 0 ? Math.abs(ms[n - 1] - ms[0]) / range : 1;

    // Ideal brush: range ~0.12%–2.0%, high osc/cross density, low trend
    let rangeScore = 0;
    if (rangePct >= 0.08 && rangePct <= 2.5) {
      rangeScore = rangePct <= 1.2 ? 30 : 22;
    } else if (rangePct > 2.5 && rangePct <= 4) rangeScore = 10;
    else if (rangePct > 0 && rangePct < 0.08) rangeScore = 6;

    const dens = osc / Math.max(1, n - 2);
    const oscScore = Math.min(35, dens * 120);
    const crossScore = Math.min(20, (cross / Math.max(1, n - 1)) * 80);
    const trendScore = Math.max(0, 15 * (1 - Math.min(1, trend)));

    let score = Math.round(Math.min(100, rangeScore + oscScore + crossScore + trendScore));
    // penalty tiny history
    if (n < 30) score = Math.round(score * (0.55 + 0.45 * (n / 30)));

    let regime = 'cold';
    let note = '';
    if (score >= 70 && trend < 0.55 && rangePct >= 0.1) {
      regime = 'brush';
      note = 'пила в коридоре — кандидат на ёршик (лимиты у краёв диапазона)';
    } else if (score >= 55) {
      regime = 'watch';
      note = 'похож на range — наблюдайте стакан / подтвердите глазом';
    } else if (trend > 0.75 && rangePct > 0.3) {
      regime = 'trend';
      note = 'скорее тренд/импульс, не классический ёршик';
    } else {
      regime = 'cold';
      note = 'слабо выраженный коридор или мало движения';
    }
    return { score, rangePct, osc, cross, trend, samples: n, regime, note, hi, lo, mid };
  },

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

      // mid for brush path: average of venue mids
      const avgMid = mids.length
        ? mids.reduce((s, x) => s + x.mid, 0) / mids.length
        : 0;
      this.pushMidSample(S, avgMid);
      const brush = this.scoreBrush(S);

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
        mids,
        brush: brush.score,
        rangePct: brush.rangePct,
        osc: brush.osc,
        crossN: brush.cross,
        trend: brush.trend,
        regime: brush.regime,
        brushNote: brush.note,
        brushSamples: brush.samples,
        rangeHi: brush.hi,
        rangeLo: brush.lo,
        rangeMid: brush.mid
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
    const brushOnly = AB.$('scr_brushOnly')?.checked;
    if (this.mode === 'brush' && brushOnly) rows = rows.filter(r => (r.brush || 0) >= 55);
    const sk = AB.$('scr_sort')?.value || this.sortKey;
    this.sortKey = sk;
    const dir = -1;
    rows.sort((a, b) => {
      const keys = {
        intensity: 'intensity', brush: 'brush', delta: 'crossDelta', vol: 'vol',
        net: 'net', depth: 'depth', range: 'rangePct', osc: 'osc', symbol: 'symbol'
      };
      let k = keys[sk] || 'intensity';
      if (this.mode === 'brush' && sk === 'intensity') k = 'brush';
      if (k === 'symbol') return a.symbol.localeCompare(b.symbol);
      return (Number(a[k]) - Number(b[k])) * dir;
    });
    this._view = rows;
  },

  paintKpis() {
    const rows = this._view || [];
    if (AB.$('scr_n')) AB.$('scr_n').textContent = String(rows.length);
    if (this.mode === 'brush') {
      if (AB.$('scr_hotLbl')) AB.$('scr_hotLbl').textContent = 'Brush ≥70';
      if (AB.$('scr_kpi3l')) AB.$('scr_kpi3l').textContent = 'Best brush';
      if (AB.$('scr_kpi4l')) AB.$('scr_kpi4l').textContent = 'Watch ≥55';
      if (AB.$('scr_hot')) AB.$('scr_hot').textContent = String(rows.filter(r => (r.brush || 0) >= 70).length);
      const best = rows.reduce((m, r) => Math.max(m, r.brush || 0), 0);
      if (AB.$('scr_maxDelta')) AB.$('scr_maxDelta').textContent = best ? String(best) : '—';
      if (AB.$('scr_arbN')) AB.$('scr_arbN').textContent = String(rows.filter(r => (r.brush || 0) >= 55).length);
      if (AB.$('scr_bannerText')) AB.$('scr_bannerText').textContent =
        'ёршик: коридор + развороты mid (live). Не автоторговля — только детекция. Откройте в Market и смотрите стакан.';
    } else {
      if (AB.$('scr_hotLbl')) AB.$('scr_hotLbl').textContent = 'Hot (≥70)';
      if (AB.$('scr_kpi3l')) AB.$('scr_kpi3l').textContent = 'Max cross Δ';
      if (AB.$('scr_kpi4l')) AB.$('scr_kpi4l').textContent = 'With arb signal';
      if (AB.$('scr_hot')) AB.$('scr_hot').textContent = String(rows.filter(r => r.intensity >= 70).length);
      const maxD = rows.reduce((m, r) => Math.max(m, r.crossDelta || 0), 0);
      if (AB.$('scr_maxDelta')) AB.$('scr_maxDelta').textContent = maxD ? maxD.toFixed(3) + '%' : '—';
      if (AB.$('scr_arbN')) AB.$('scr_arbN').textContent = String(rows.filter(r => r.net > 0).length);
      if (AB.$('scr_bannerText')) AB.$('scr_bannerText').textContent =
        'intensity / cross-Δ / arb. Режим «Ёршик» — поиск пилы MM в коридоре.';
    }
    if (AB.$('scr_hint')) {
      const nHist = Object.keys(this._midHist || {}).length;
      AB.$('scr_hint').textContent = ((this._data && this._data.discoverySource)
        ? ('discovery: ' + this._data.discoverySource + ' · ') : '') +
        'mid hist ' + nHist + ' sym · mode ' + this.mode;
    }
    // column visibility
    document.querySelectorAll('.scr-col-brush').forEach(el => {
      el.hidden = this.mode !== 'brush';
    });
    const bow = AB.$('scr_brushOnlyWrap');
    if (bow) bow.hidden = this.mode !== 'brush';
  },


  paintBrushDetail(r) {
    const box = AB.$('scr_brushBox');
    if (!box) return;
    if (this.mode !== 'brush' || !r) {
      box.hidden = true;
      box.innerHTML = '';
      return;
    }
    box.hidden = false;
    const lo = r.rangeLo != null ? Number(r.rangeLo) : 0;
    const hi = r.rangeHi != null ? Number(r.rangeHi) : 0;
    const mid = r.rangeMid != null ? Number(r.rangeMid) : 0;
    const levels = (hi > 0 && lo > 0)
      ? ('<div class="mono" style="margin-top:6px">Lo ' + lo.toPrecision(6) +
         ' · Mid ' + mid.toPrecision(6) + ' · Hi ' + hi.toPrecision(6) + '</div>')
      : '';
    box.innerHTML =
      '<div style="font-weight:700;margin-bottom:6px;color:var(--accent)">Ёршик / MM range</div>' +
      '<div><b>Score</b> ' + (r.brush || 0) + ' · <span class="scr-regime ' + (r.regime || '') + '">' +
      (r.regime || '') + '</span></div>' +
      '<div><b>Range</b> ' + (r.rangePct != null ? r.rangePct.toFixed(3) + '%' : '—') +
      ' · osc ' + (r.osc || 0) + ' · cross mid ' + (r.crossN || 0) +
      ' · trend ' + (r.trend != null ? r.trend.toFixed(2) : '—') +
      ' · samples ' + (r.brushSamples || 0) + '</div>' + levels +
      '<div style="margin-top:8px">' + (r.brushNote || '') + '</div>' +
      '<div class="muted" style="margin-top:8px;font-size:11px">Как MetaScalp: Market → стакан → лимиты у краёв. Авто-ёршик пока не торгует.</div>';
  },
  paintTable() {
    const body = AB.$('scr_body');
    if (!body) return;
    const rows = this._view || [];
    const brushMode = this.mode === 'brush';
    if (!rows.length) {
      body.innerHTML = '<tr><td colspan="11" class="empty">No pairs match filters / waiting for books…</td></tr>';
      return;
    }
    const maxInt = Math.max(...rows.map(r => brushMode ? (r.brush || 0) : r.intensity), 1);
    body.innerHTML = rows.map(r => {
      const score = brushMode ? (r.brush || 0) : r.intensity;
      const heat = score >= 70 ? 'hot' : (score >= 40 ? 'warm' : 'cool');
      const w = Math.max(4, (score / maxInt) * 100);
      const act = r.symbol === this.selected ? ' active' : '';
      const dlt = r.crossDelta > 0
        ? '<span class="pos mono">+' + r.crossDelta.toFixed(3) + '%</span>'
        : '<span class="muted">—</span>';
      const net = r.net > 0
        ? '<span class="pos mono">+' + r.net.toFixed(3) + '%</span>'
        : '<span class="muted">—</span>';
      const vol = r.vol >= 1e6 ? (r.vol / 1e6).toFixed(1) + 'M'
        : (r.vol >= 1e3 ? (r.vol / 1e3).toFixed(0) + 'K' : (r.vol ? r.vol.toFixed(0) : '—'));
      const brushCols = brushMode
        ? ('<td class="mono">' + (r.brush || 0) + '</td>' +
           '<td class="mono">' + (r.rangePct != null ? r.rangePct.toFixed(2) + '%' : '—') + '</td>' +
           '<td class="mono">' + (r.osc != null ? r.osc : '—') + '</td>' +
           '<td><span class="scr-regime ' + (r.regime || 'cold') + '">' + (r.regime || 'cold') + '</span></td>')
        : '';
      return '<tr class="scr-row' + act + '" data-sym="' + r.symbol + '">' +
        '<td><div class="scr-heat ' + heat + '">' + score +
        '</div><div class="scr-int-bar" style="margin-top:4px"><i style="width:' + w + '%"></i></div></td>' +
        '<td class="mono" style="font-weight:700;color:var(--blue)">' + r.symbol + '</td>' +
        '<td class="mono">' + r.venues + '</td>' +
        '<td class="mono">' + vol + '</td>' +
        '<td class="mono">' + (r.depth ? r.depth.toFixed(1) + '×' : '—') + '</td>' +
        '<td>' + dlt + '</td>' +
        '<td>' + net + '</td>' + brushCols + '</tr>';
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
    if (sub) {
      if (!row) sub.textContent = 'detail';
      else if (this.mode === 'brush')
        sub.textContent = 'brush ' + (row.brush || 0) + ' · ' + (row.regime || '') + ' · ' + row.venues + ' venues';
      else
        sub.textContent = 'intensity ' + row.intensity + ' · ' + row.venues + ' venues';
    }
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
      this.paintBrushDetail(null);
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
    this.paintBrushDetail(row);
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
    document.querySelectorAll('.scr-mode-btn').forEach(btn => {
      if (btn._scrBound) return;
      btn._scrBound = true;
      btn.addEventListener('click', () => {
        document.querySelectorAll('.scr-mode-btn').forEach(b => b.classList.remove('active'));
        btn.classList.add('active');
        AB.pages.screener.mode = btn.getAttribute('data-mode') || 'intensity';
        if (AB.pages.screener.mode === 'brush') {
          const sel = document.getElementById('scr_sort');
          if (sel) sel.value = 'brush';
          AB.pages.screener.sortKey = 'brush';
        }
        if (AB.pages.screener._data) AB.pages.screener.render(AB.pages.screener._data);
      });
    });
    ['scr_q', 'scr_multi', 'scr_depth', 'scr_brushOnly', 'scr_sort'].forEach(id => {
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
