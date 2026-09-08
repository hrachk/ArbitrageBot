AB.pages.settings = {
  presets: {
    professional: {
      minProfitPercent: 0.10,
      quoteSize: 500,
      leverage: 5,
      maxOpenPositions: 3,
      stopLossUsd: -25,
      dailyLossLimitUsd: -80,
      maxHoldMinutes: 0,
      closeBelowNetPercent: 0.02,
      maxMarginUsagePercent: 0.35,
      maxNotionalUsd: 500,
      paperCooldownMs: 15000,
      paperRequireFullFill: true,
      requireRoundTripEdge: true,
      includeFunding: true,
      liveEquityPerExchangeUsd: 2500,
      liveMarginUsageFraction: 0.35,
      liveMaxNotionalUsd: 500,
      liveMaxOpenPositions: 3,
      liveStopLossUsd: -25
    },
    micro5: {
      minProfitPercent: 0.10,
      quoteSize: 500,
      leverage: 5,
      maxOpenPositions: 3,
      stopLossUsd: -25,
      dailyLossLimitUsd: -80,
      maxHoldMinutes: 0,
      closeBelowNetPercent: 0.02,
      maxMarginUsagePercent: 0.35,
      maxNotionalUsd: 500,
      paperCooldownMs: 15000,
      paperRequireFullFill: true,
      requireRoundTripEdge: true,
      includeFunding: true,
      liveEquityPerExchangeUsd: 2500,
      liveMarginUsageFraction: 0.35,
      liveMaxNotionalUsd: 500,
      liveMaxOpenPositions: 3,
      liveStopLossUsd: -25
    },
    conservative: {
      minProfitPercent: 0.12,
      quoteSize: 50,
      leverage: 3,
      maxOpenPositions: 1,
      stopLossUsd: -8,
      dailyLossLimitUsd: -25,
      maxHoldMinutes: 0,
      closeBelowNetPercent: 0.02,
      maxMarginUsagePercent: 0.30,
      maxNotionalUsd: 50,
      paperCooldownMs: 20000,
      paperRequireFullFill: true,
      requireRoundTripEdge: true,
      includeFunding: true,
      liveEquityPerExchangeUsd: 2500,
      liveMarginUsageFraction: 0.30,
      liveMaxNotionalUsd: 50,
      liveMaxOpenPositions: 1,
      liveStopLossUsd: -8
    },
    balanced: {
      minProfitPercent: 0.10,
      quoteSize: 500,
      leverage: 5,
      maxOpenPositions: 3,
      stopLossUsd: -25,
      dailyLossLimitUsd: -80,
      maxHoldMinutes: 0,
      closeBelowNetPercent: 0.02,
      maxMarginUsagePercent: 0.35,
      maxNotionalUsd: 500,
      paperCooldownMs: 15000,
      paperRequireFullFill: true,
      requireRoundTripEdge: true,
      includeFunding: true,
      liveEquityPerExchangeUsd: 2500,
      liveMarginUsageFraction: 0.35,
      liveMaxNotionalUsd: 500,
      liveMaxOpenPositions: 3,
      liveStopLossUsd: -25
    }
  },

  async load() {
    try {
      const s = await AB.api.get('/api/settings');
      AB.state.settings = s;
      this.fill(s);
      try {
        const r = await AB.api.get('/api/settings/risk');
        this.fillRisk(r);
        // Apply Live ($5) preset on first load if nothing saved yet
        if (r && (r.minProfitPercent == null || r.minProfitPercent === 0)) {
          this.applyPreset('professional');
          if (AB.$('s_paper')) AB.$('s_paper').checked = true;
          if (AB.$('s_auto')) AB.$('s_auto').checked = true;
        }
      } catch (_) {}
      // Show active mode in settings header
      this._updateModeHint(s);
    } catch (e) {
      AB.$('s_msg').className = 'alert warn';
      AB.$('s_msg').textContent = 'Settings load failed: ' + e.message;
      AB.$('s_msg').classList.remove('hidden');
    }
  },

  _updateModeHint(s) {
    const t = (s && s.trading) || {};
    const isPaper = t.paperTrading !== false;
    const pill = document.getElementById('s_modePill');
    if (pill) {
      pill.textContent = isPaper ? 'PAPER' : 'LIVE';
      pill.className = 'mode-pill ' + (isPaper ? 'paper' : 'live');
    }
  },

  fillRisk(r) {
    if (!r) return;
    const set = (id, v) => { if (AB.$(id) != null && v != null && !Number.isNaN(v)) AB.$(id).value = v; };
    set('s_minProfit', r.minProfitPercent);
    set('s_size', r.quoteSize);
    set('s_lev', r.leverage);
    set('s_maxPos', r.maxOpenPositions);
    set('s_stop', r.stopLossUsd);
    set('s_dayLimit', r.dailyLossLimitUsd);
    set('s_hold', r.maxHoldMinutes);
    set('s_closeWidth', r.closeBelowNetPercent);
    set('s_marginUse', r.maxMarginUsagePercent);
    set('s_maxNotional', r.maxNotionalUsd);
    set('s_cooldown', r.paperCooldownMs);
    set('s_liveEquity', r.liveEquityPerExchangeUsd ?? 5);
    set('s_liveUsage', r.liveMarginUsageFraction ?? 0.6);
    set('s_liveMaxN', r.liveMaxNotionalUsd ?? 100);
    set('s_liveMaxOpen', r.liveMaxOpenPositions ?? 1);
    set('s_liveStop', r.liveStopLossUsd ?? -2.5);
    if (AB.$('s_fullFill')) AB.$('s_fullFill').checked = !!r.paperRequireFullFill;
    if (AB.$('s_reqRt')) AB.$('s_reqRt').checked = !!r.requireRoundTripEdge;
    if (AB.$('s_funding')) AB.$('s_funding').checked = r.includeFunding !== false;

    set('s_minGross', r.minGrossSpreadPercent);
    set('s_minTp', r.minTakeProfitUsd);
    set('s_persistMs', r.minSpreadPersistMs);
    set('s_bookAge', r.maxBookAgeMs);
    set('s_scanMs', r.scanIntervalMs);
    set('s_holdSec', r.futuresMaxHoldSeconds);
    set('s_closeFee', r.paperCloseFeeFactor);
    set('s_edgeBuffer', r.openEdgeBufferPercent);
    set('s_depthScore', r.minDepthScoreForUniverse);
    set('s_maxLegs', r.maxLegsPerVenue);
    set('s_maxWidthExp', r.maxWidthExpansionPercent);
    set('s_dynTopN', r.dynamicTopN);
    set('s_dynMinVol', r.dynamicMinQuoteVolumeUsd);
    set('s_dynMaxVol', r.dynamicMaxQuoteVolumeUsd);
    set('s_dynRefresh', r.dynamicRefreshMinutes);
    set('s_paperStart', r.paperStartingQuote);
    if (AB.$('s_scalp') && r.spatialScalpMode != null) AB.$('s_scalp').checked = !!r.spatialScalpMode;
    if (AB.$('s_spreadEdge') && r.requireSpreadingEdge != null) AB.$('s_spreadEdge').checked = !!r.requireSpreadingEdge;
    if (AB.$('s_depthFill') && r.requireDepthFullFill != null) AB.$('s_depthFill').checked = r.requireDepthFullFill !== false;
    if (AB.$('s_dynOn') && r.dynamicSymbols != null) AB.$('s_dynOn').checked = r.dynamicSymbols !== false;
  },

  applyPreset(name) {
    const p = this.presets[name] || this.presets.professional || this.presets.micro5;
    this.fillRisk(p);
    if (AB.$('s_paper')) AB.$('s_paper').checked = true;
    if (AB.$('s_auto')) AB.$('s_auto').checked = true;
    // Keep size and max notional aligned
    if (AB.$('s_size') && AB.$('s_maxNotional')) {
      const sz = Number(AB.$('s_size').value) || 100;
      const mx = Number(AB.$('s_maxNotional').value) || sz;
      if (sz > mx) AB.$('s_size').value = mx;
      if (AB.$('s_liveMaxN')) AB.$('s_liveMaxN').value = AB.$('s_maxNotional').value;
      if (AB.$('s_liveMaxOpen') && AB.$('s_maxPos')) AB.$('s_liveMaxOpen').value = AB.$('s_maxPos').value;
    }
    AB.$('s_msg').className = 'alert info';
    AB.$('s_msg').textContent = 'Preset «' + name + '» (paper=live gates) — Save all.';
    AB.$('s_msg').classList.remove('hidden');
  },

  fill(s) {
    const t = s.trading || {};
    if (AB.$('s_strategy')) AB.$('s_strategy').value = t.strategyMode || 'FuturesCross';
    if (AB.$('s_paper')) AB.$('s_paper').checked = t.paperTrading !== false;
    if (AB.$('s_auto')) AB.$('s_auto').checked = !!t.paperAutoExecute;
    if (AB.$('s_minProfit')) AB.$('s_minProfit').value = t.minProfitPercent ?? 0.10;
    if (AB.$('s_size')) AB.$('s_size').value = t.quoteSize ?? 500;
    if (AB.$('s_lev')) AB.$('s_lev').value = t.futuresPaperLeverage ?? 5;
    if (AB.$('s_maxPos')) AB.$('s_maxPos').value = t.futuresMaxOpenPositions ?? 2;
    if (AB.$('s_stop')) AB.$('s_stop').value = t.futuresStopLossUsd ?? -12;
    if (AB.$('s_dayLimit')) AB.$('s_dayLimit').value = t.futuresDailyLossLimitUsd ?? -40;
    if (AB.$('s_liveEquity')) AB.$('s_liveEquity').value = t.liveEquityPerExchangeUsd ?? 2500;
    if (AB.$('s_liveUsage')) AB.$('s_liveUsage').value = t.liveMarginUsageFraction ?? 0.6;
    if (AB.$('s_liveMaxN')) AB.$('s_liveMaxN').value = t.liveMaxNotionalUsd ?? 500;
    if (AB.$('s_liveMaxOpen')) AB.$('s_liveMaxOpen').value = t.liveMaxOpenPositions ?? 1;
    if (AB.$('s_liveStop')) AB.$('s_liveStop').value = t.liveStopLossUsd ?? -2.5;
    if (AB.$('s_maxNotional')) AB.$('s_maxNotional').value = t.maxNotionalUsd ?? t.liveMaxNotionalUsd ?? 500;
    if (AB.$('s_marginUse')) AB.$('s_marginUse').value = t.maxMarginUsagePercent ?? 0.35;
    if (AB.$('s_hold')) AB.$('s_hold').value = t.maxHoldMinutes ?? 0;
    if (AB.$('s_closeWidth')) AB.$('s_closeWidth').value = t.closeBelowNetPercent ?? 0.02;
    if (AB.$('s_cooldown')) AB.$('s_cooldown').value = t.paperCooldownMs ?? 15000;
    if (AB.$('s_fullFill')) AB.$('s_fullFill').checked = t.paperRequireFullFill !== false;
    if (AB.$('s_reqRt')) AB.$('s_reqRt').checked = t.requireRoundTripEdge !== false;
    if (AB.$('s_funding')) AB.$('s_funding').checked = t.includeFunding !== false;

    const conns = s.connections || {};
    AB.$('s_exchanges').innerHTML = Object.entries(conns).map(([name, c]) => {
        const on = !!c.enabled;
        const has = !!(c.hasKey || c.apiKeyMasked || c.keyHint);
        const mask = c.apiKeyMasked || c.keyHint || (has ? '••••••••' : 'нет ключа');
        const perm = c.permission || 'read-only';
        return `<div class="ex-card" data-ex="${name}">
          <div class="ex-card-hd">
            <div class="ex-name"><span class="ex-dot ${on ? '' : 'off'}"></span>${name}</div>
            <label class="ex-enable-row"><input type="checkbox" class="ex-enabled" ${on ? 'checked' : ''}/> Включён</label>
          </div>
          <div class="ex-meta">Key: ${mask} · ${perm}</div>
          <div class="settings-grid">
            <div class="field"><label>API Key</label><input class="ex-key" type="password" placeholder="•••• leave blank to keep" autocomplete="off"/></div>
            <div class="field"><label>API Secret</label><input class="ex-secret" type="password" placeholder="•••• leave blank to keep" autocomplete="new-password"/></div>
            <div class="field"><label>Passphrase</label><input class="ex-pass" type="password" placeholder="${name==='OKX'||name==='Bitget'?'required / keep':'optional / keep'}" autocomplete="new-password"/></div>
            <div class="field"><label>Permission</label>
              <select class="ex-perm">
                <option value="read-only" ${perm!=='trade'?'selected':''}>read-only</option>
                <option value="trade" ${perm==='trade'?'selected':''}>trade</option>
              </select>
            </div>
          </div>
          <div class="ex-actions">
            <button type="button" class="btn primary save-ex">Save ${name}</button>
            <button type="button" class="btn clear-ex">Clear</button>
          </div>
        </div>`;
      }).join('') || '<div class="muted">No exchanges in config</div>';

    document.querySelectorAll('.save-ex').forEach(btn => {
      btn.onclick = async () => {
        const card = btn.closest('.ex-card');
        const name = card.dataset.ex;
        const body = {
          enabled: card.querySelector('.ex-enabled').checked,
          apiKey: card.querySelector('.ex-key').value || null,
          apiSecret: card.querySelector('.ex-secret').value || null,
          passphrase: card.querySelector('.ex-pass').value || null,
          permission: card.querySelector('.ex-perm').value
        };
        try {
          await AB.api.post('/api/settings/exchanges/' + encodeURIComponent(name), body);
          AB.$('s_msg').className = 'alert ok';
          AB.$('s_msg').textContent = name + ' credentials saved → local-settings.json';
          AB.$('s_msg').classList.remove('hidden');
          this.load();
        } catch (e) {
          AB.$('s_msg').className = 'alert warn';
          AB.$('s_msg').textContent = e.message;
          AB.$('s_msg').classList.remove('hidden');
        }
      };
    });
    document.querySelectorAll('.clear-ex').forEach(btn => {
      btn.onclick = async () => {
        const card = btn.closest('.ex-card');
        const name = card.dataset.ex;
        if (!confirm('Clear all API keys for ' + name + '?')) return;
        try {
          await AB.api.del('/api/settings/exchanges/' + encodeURIComponent(name));
          AB.$('s_msg').className = 'alert ok';
          AB.$('s_msg').textContent = name + ' keys cleared.';
          AB.$('s_msg').classList.remove('hidden');
          this.load();
        } catch (e) {
          AB.$('s_msg').className = 'alert warn';
          AB.$('s_msg').textContent = e.message;
          AB.$('s_msg').classList.remove('hidden');
        }
      };
    });
  },
  onShow() { this.load(); }
};

document.getElementById('presetConservative')?.addEventListener('click', () => AB.pages.settings.applyPreset('conservative'));
document.getElementById('presetBalanced')?.addEventListener('click', () => AB.pages.settings.applyPreset('professional'));
document.getElementById('presetAggressive')?.addEventListener('click', () => AB.pages.settings.applyPreset('balanced'));

document.getElementById('btnSaveTrading')?.addEventListener('click', async () => {
  const num = (id, fallback) => {
    const v = parseFloat(AB.$(id)?.value);
    return Number.isFinite(v) ? v : fallback;
  };
  const int = (id, fallback) => {
    const v = parseInt(AB.$(id)?.value, 10);
    return Number.isFinite(v) ? v : fallback;
  };

  // Unified professional profile: size ≤ max notional; live mirrors paper
  let size = num('s_size', 500);
  let maxN = num('s_maxNotional', 500);
  if (size > maxN) size = maxN;
  if (maxN < size) maxN = size;
  const maxOpen = int('s_maxPos', 3);
  const trading = {
    strategyMode: AB.$('s_strategy')?.value || 'FuturesCross',
    paperTrading: !!AB.$('s_paper')?.checked,
    paperAutoExecute: !!AB.$('s_auto')?.checked,
    minProfitPercent: num('s_minProfit', 0.10),
    quoteSize: size,
    futuresPaperLeverage: Math.min(10, Math.max(1, num('s_lev', 5))),
    futuresMaxOpenPositions: maxOpen,
    futuresStopLossUsd: num('s_stop', -25),
    futuresDailyLossLimitUsd: num('s_dayLimit', -80),
    maxHoldMinutes: int('s_hold', 0),
    closeBelowNetPercent: num('s_closeWidth', 0.02),
    maxMarginUsagePercent: Math.min(0.9, Math.max(0.05, num('s_marginUse', 0.35))),
    maxNotionalUsd: maxN,
    paperCooldownMs: int('s_cooldown', 15000),
    paperRequireFullFill: !!AB.$('s_fullFill')?.checked,
    requireRoundTripEdge: !!AB.$('s_reqRt')?.checked,
    includeFunding: !!AB.$('s_funding')?.checked,
    minGrossSpreadPercent: num('s_minGross', 0.28),
    minTakeProfitUsd: num('s_minTp', 0.8),
    minSpreadPersistMs: int('s_persistMs', 500),
    maxBookAgeMs: int('s_bookAge', 400),
    scanIntervalMs: int('s_scanMs', 250),
    futuresMaxHoldSeconds: int('s_holdSec', 0),
    spatialScalpMode: !!AB.$('s_scalp')?.checked,
    requireSpreadingEdge: !!AB.$('s_spreadEdge')?.checked,
    paperCloseFeeFactor: num('s_closeFee', 0.85),
    openEdgeBufferPercent: num('s_edgeBuffer', 0.02),
    requireDepthFullFill: AB.$('s_depthFill') ? !!AB.$('s_depthFill').checked : true,
    minDepthScoreForUniverse: num('s_depthScore', 0.85),
    maxLegsPerVenue: int('s_maxLegs', 2),
    maxWidthExpansionPercent: num('s_maxWidthExp', 0.12),
    dynamicSymbols: AB.$('s_dynOn') ? !!AB.$('s_dynOn').checked : true,
    dynamicTopN: int('s_dynTopN', 12),
    dynamicMinQuoteVolumeUsd: num('s_dynMinVol', 8000000),
    dynamicMaxQuoteVolumeUsd: num('s_dynMaxVol', 500000000),
    dynamicRefreshMinutes: int('s_dynRefresh', 5),
    paperStartingQuote: num('s_paperStart', 2500),
    liveEquityPerExchangeUsd: num('s_liveEquity', 2500),
    liveMarginUsageFraction: Math.min(0.85, Math.max(0.2, num('s_liveUsage', 0.35))),
    liveMaxNotionalUsd: maxN,
    liveMaxOpenPositions: int('s_liveMaxOpen', maxOpen) || maxOpen,
    liveStopLossUsd: num('s_liveStop', -25),
    liveDailyLossLimitUsd: num('s_dayLimit', -80)
  };
  const risk = {
    minProfitPercent: trading.minProfitPercent,
    quoteSize: trading.quoteSize,
    leverage: trading.futuresPaperLeverage,
    maxOpenPositions: trading.futuresMaxOpenPositions,
    stopLossUsd: trading.futuresStopLossUsd,
    dailyLossLimitUsd: trading.futuresDailyLossLimitUsd,
    maxHoldMinutes: trading.maxHoldMinutes,
    closeBelowNetPercent: trading.closeBelowNetPercent,
    maxMarginUsagePercent: trading.maxMarginUsagePercent,
    maxNotionalUsd: trading.maxNotionalUsd,
    paperCooldownMs: trading.paperCooldownMs,
    paperRequireFullFill: trading.paperRequireFullFill,
    requireRoundTripEdge: trading.requireRoundTripEdge,
    includeFunding: trading.includeFunding,
    liveEquityPerExchangeUsd: trading.liveEquityPerExchangeUsd,
    liveMarginUsageFraction: trading.liveMarginUsageFraction,
    liveMaxNotionalUsd: trading.liveMaxNotionalUsd,
    liveMaxOpenPositions: trading.liveMaxOpenPositions,
    liveStopLossUsd: trading.liveStopLossUsd
  };
  try {
    const res = await AB.api.post('/api/settings/trading', trading);
    await AB.api.post('/api/settings/risk', risk);
    const eff = (res && res.effective) || {};
    const qs = eff.quoteSize != null ? eff.quoteSize : trading.quoteSize;
    const mn = eff.maxNotionalUsd != null ? eff.maxNotionalUsd : trading.maxNotionalUsd;
    const start = eff.paperStartingQuote != null ? eff.paperStartingQuote : trading.paperStartingQuote;
    let msg = 'Saved · size $' + qs + ' · max notional $' + mn + ' · paper start $' + start + '/venue';
    if (res && res.reseededBalances) msg += ' · balances RESEEDED';
    else if (res && res.openPositions > 0) msg += ' · ' + res.openPositions + ' open — Reset paper after close to apply equity';
    else if (res && res.tip) msg += ' · ' + res.tip;
    AB.$('s_msg').className = 'alert ok';
    AB.$('s_msg').textContent = msg;
    AB.$('s_msg').classList.remove('hidden');
    if (AB.refreshSnapshot) AB.refreshSnapshot();
  } catch (e) {
    AB.$('s_msg').className = 'alert warn';
    AB.$('s_msg').textContent = e.message;
    AB.$('s_msg').classList.remove('hidden');
  }
});

async function refreshLiveStatus() {
  try {
    const st = await AB.api.get('/api/live/status');
    _applyLiveStatus(st);
  } catch (e) {
    if (AB.$('live_status')) AB.$('live_status').textContent = String(e.message || e);
  }
}

function _applyLiveStatus(st) {
  const phase = st.phase || 'PAPER_ONLY';
  const canOrder = st.canPlaceOrders;
  const isEnabled = st.enabled;
  const isReadOnly = st.readOnly;

  // Badge
  const badge = AB.$('livePhase');
  if (badge) {
    if (canOrder) {
      badge.textContent = '🔴 LIVE — ORDERS ON';
      badge.style.background = 'rgba(248,113,113,0.2)';
      badge.style.color = '#f87171';
    } else if (isEnabled && isReadOnly) {
      badge.textContent = '🟡 LIVE — READ ONLY';
      badge.style.background = 'rgba(251,191,36,0.15)';
      badge.style.color = '#fbbf24';
    } else {
      badge.textContent = '📄 PAPER_ONLY';
      badge.style.background = 'rgba(148,163,184,0.15)';
      badge.style.color = '#94a3b8';
    }
  }

  // Status card labels
  const modeEl = AB.$('live_mode_label');
  const ordersEl = AB.$('live_orders_label');
  const phaseEl = AB.$('live_phase_label');
  if (modeEl) modeEl.innerHTML = canOrder ? '<span style="color:#f87171">🔴 LIVE</span>' : (isEnabled ? '<span style="color:#fbbf24">🟡 LIVE-RO</span>' : '📄 PAPER');
  if (ordersEl) ordersEl.innerHTML = canOrder ? '<span style="color:#f87171">✅ включены</span>' : (isEnabled && isReadOnly ? '<span style="color:#fbbf24">👁 read-only</span>' : '🚫 выключены');
  if (phaseEl) phaseEl.textContent = phase;

  // Panel border color
  const panel = document.getElementById('livePanel');
  if (panel) panel.style.borderColor = canOrder ? 'rgba(248,113,113,0.8)' : (isEnabled ? 'rgba(251,191,36,0.5)' : 'rgba(248,113,113,0.5)');

  if (AB.$('live_phase')) AB.$('live_phase').textContent = phase;
  if (AB.$('live_status')) AB.$('live_status').textContent = JSON.stringify(st, null, 2);
}

document.getElementById('btnLiveVerify_REPLACED')?.addEventListener('click', async () => {
  try {
    const r = await AB.api.get('/api/live/balances');
    if (AB.$('live_status')) AB.$('live_status').textContent = JSON.stringify(r, null, 2);
    if (AB.$('livePhase') && r.guard) AB.$('livePhase').textContent = r.guard.phase || '—';
  } catch (e) {
    if (AB.$('live_status')) AB.$('live_status').textContent = String(e.message || e);
  }
});

document.getElementById('btnLiveEnable')?.addEventListener('click', async () => {
  const phrase = AB.$('live_phrase')?.value || '';
  const readOnly = !!AB.$('live_readonly')?.checked;
  if (!phrase) { alert('Введи confirm phrase: ENABLE LIVE TRADING'); return; }
  if (!readOnly && !confirm('⚠️ Enable LIVE ORDERS? Реальные деньги на биржах!')) return;
  try {
    const r = await AB.api.post('/api/live/enable', { confirmPhrase: phrase, readOnly });
    if (r.status) _applyLiveStatus(r.status);
    if (AB.$('live_status')) AB.$('live_status').textContent = JSON.stringify(r, null, 2);
    if (r.ok) {
      AB.$('live_phrase').value = '';
      alert('✅ ' + (r.message || 'Live enabled'));
    } else {
      alert('❌ ' + (r.message || 'Failed'));
    }
  } catch (e) {
    alert('Error: ' + e.message);
    if (AB.$('live_status')) AB.$('live_status').textContent = String(e.message || e);
  }
});

document.getElementById('btnLiveDisable')?.addEventListener('click', async () => {
  try {
    const r = await AB.api.post('/api/live/disable');
    if (AB.$('live_status')) AB.$('live_status').textContent = JSON.stringify(r, null, 2);
    await refreshLiveStatus();
    if (AB.$('s_paper')) AB.$('s_paper').checked = true;
    if (AB.$('s_auto')) AB.$('s_auto').checked = true;
    if (AB.refreshSnapshot) AB.refreshSnapshot();
    alert(r.message || 'Live off → DEMO paper on real books');
  } catch (e) {
    if (AB.$('live_status')) AB.$('live_status').textContent = String(e.message || e);
  }
});

document.getElementById('btnLiveKill')?.addEventListener('click', async () => {
  if (!confirm('KILL SWITCH — disable all live immediately?')) return;
  try {
    const r = await AB.api.post('/api/live/kill', { reason: 'ui-kill' });
    if (AB.$('live_status')) AB.$('live_status').textContent = JSON.stringify(r, null, 2);
  } catch (e) {
    if (AB.$('live_status')) AB.$('live_status').textContent = String(e.message || e);
  }
});

const _oldOnShow = AB.pages.settings.onShow;
AB.pages.settings.onShow = function () {
  if (typeof _oldOnShow === 'function') _oldOnShow.call(this);
  refreshLiveStatus();
};

// ── Live panel button handlers (updated) ─────────────────────────────────────
document.getElementById('btnLiveVerify')?.addEventListener('click', async () => {
  try {
    const r = await AB.api.get('/api/live/balances');
    if (AB.$('live_status')) AB.$('live_status').textContent = JSON.stringify(r, null, 2);
    if (r.guard) _applyLiveStatus(r.guard);
    const det = document.querySelector('#livePanel details');
    if (det) det.open = true;
  } catch (e) {
    if (AB.$('live_status')) AB.$('live_status').textContent = String(e.message || e);
  }
});

document.getElementById('btnLiveDisable')?.removeEventListener?.('click', null);
document.getElementById('btnLiveDisable')?.addEventListener('click', async () => {
  if (!confirm('Disable Live — переключиться в PAPER?')) return;
  try {
    const r = await AB.api.post('/api/live/disable');
    _applyLiveStatus(r);
    if (AB.$('live_status')) AB.$('live_status').textContent = JSON.stringify(r, null, 2);
  } catch (e) {
    if (AB.$('live_status')) AB.$('live_status').textContent = String(e.message || e);
  }
});

document.getElementById('btnLiveKill')?.removeEventListener?.('click', null);
document.getElementById('btnLiveKill')?.addEventListener('click', async () => {
  if (!confirm('⛔ KILL SWITCH — немедленно выключить всё Live?')) return;
  try {
    const r = await AB.api.post('/api/live/kill', { reason: 'ui-kill' });
    _applyLiveStatus(r);
    if (AB.$('live_status')) AB.$('live_status').textContent = JSON.stringify(r, null, 2);
  } catch (e) {
    if (AB.$('live_status')) AB.$('live_status').textContent = String(e.message || e);
  }
});


// Settings tabs (RU)
document.querySelectorAll('.settings-tab').forEach(btn => {
  btn.addEventListener('click', () => {
    const id = btn.getAttribute('data-stab');
    document.querySelectorAll('.settings-tab').forEach(b => b.classList.toggle('active', b === btn));
    document.querySelectorAll('.settings-pane').forEach(p => {
      p.classList.toggle('active', p.getAttribute('data-spane') === id);
    });
  });
});
