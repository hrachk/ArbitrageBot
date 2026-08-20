AB.pages.settings = {
  presets: {
    conservative: {
      minProfitPercent: 0.08,
      quoteSize: 800,
      leverage: 3,
      maxOpenPositions: 3,
      stopLossUsd: -40,
      dailyLossLimitUsd: -150,
      maxHoldMinutes: 12,
      closeBelowNetPercent: 0.015,
      maxMarginUsagePercent: 0.12,
      maxNotionalUsd: 2500,
      paperCooldownMs: 8000,
      paperRequireFullFill: false,
      requireRoundTripEdge: true,
      includeFunding: true
    },
    balanced: {
      minProfitPercent: 0.025,
      quoteSize: 2000,
      leverage: 5,
      maxOpenPositions: 6,
      stopLossUsd: -80,
      dailyLossLimitUsd: -400,
      maxHoldMinutes: 20,
      closeBelowNetPercent: 0.02,
      maxMarginUsagePercent: 0.25,
      maxNotionalUsd: 6000,
      paperCooldownMs: 4000,
      paperRequireFullFill: false,
      requireRoundTripEdge: false,
      includeFunding: true
    },
    aggressive: {
      minProfitPercent: 0.015,
      quoteSize: 3500,
      leverage: 5,
      maxOpenPositions: 8,
      stopLossUsd: -120,
      dailyLossLimitUsd: -600,
      maxHoldMinutes: 30,
      closeBelowNetPercent: 0.025,
      maxMarginUsagePercent: 0.35,
      maxNotionalUsd: 10000,
      paperCooldownMs: 2500,
      paperRequireFullFill: false,
      requireRoundTripEdge: false,
      includeFunding: true
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
      } catch (_) {}
    } catch (e) {
      AB.$('s_msg').className = 'alert warn';
      AB.$('s_msg').textContent = 'Settings load failed: ' + e.message;
      AB.$('s_msg').classList.remove('hidden');
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
    if (AB.$('s_fullFill')) AB.$('s_fullFill').checked = !!r.paperRequireFullFill;
    if (AB.$('s_reqRt')) AB.$('s_reqRt').checked = !!r.requireRoundTripEdge;
    if (AB.$('s_funding')) AB.$('s_funding').checked = r.includeFunding !== false;
  },

  applyPreset(name) {
    const p = this.presets[name];
    if (!p) return;
    this.fillRisk(p);
    AB.$('s_msg').className = 'alert info';
    AB.$('s_msg').textContent = 'Preset «' + name + '» loaded into form — нажми Apply trading parameters.';
    AB.$('s_msg').classList.remove('hidden');
  },

  fill(s) {
    const t = s.trading || {};
    if (AB.$('s_strategy')) AB.$('s_strategy').value = t.strategyMode || 'FuturesCross';
    if (AB.$('s_paper')) AB.$('s_paper').checked = t.paperTrading !== false;
    if (AB.$('s_auto')) AB.$('s_auto').checked = !!t.paperAutoExecute;
    if (AB.$('s_minProfit')) AB.$('s_minProfit').value = t.minProfitPercent ?? 0.025;
    if (AB.$('s_size')) AB.$('s_size').value = t.quoteSize ?? 2000;
    if (AB.$('s_lev')) AB.$('s_lev').value = t.futuresPaperLeverage ?? 5;
    if (AB.$('s_maxPos')) AB.$('s_maxPos').value = t.futuresMaxOpenPositions ?? 6;
    if (AB.$('s_stop')) AB.$('s_stop').value = t.futuresStopLossUsd ?? -80;
    if (AB.$('s_dayLimit')) AB.$('s_dayLimit').value = t.futuresDailyLossLimitUsd ?? -400;

    const conns = s.connections || {};
    AB.$('s_exchanges').innerHTML = Object.entries(conns).map(([name, c]) => `
      <div class="card ex-card" data-ex="${name}">
        <div class="ex-head">
          <strong>${name}</strong>
          <label style="display:flex;align-items:center;gap:6px;margin:0">
            <input type="checkbox" class="ex-enabled" ${c.enabled?'checked':''}/> Enabled
          </label>
        </div>
        <div class="muted" style="font-size:11px">${c.hasApiKey ? 'Key: '+c.apiKeyMasked : 'No API key stored'} · ${c.permission||'read-only'}</div>
        <div class="form-row">
          <div class="field"><label>API Key</label><input class="ex-key" type="text" placeholder="${c.hasApiKey?'•••• leave blank to keep':''}" autocomplete="off"/></div>
          <div class="field"><label>API Secret</label><input class="ex-secret" type="password" placeholder="${c.hasApiSecret?'•••• leave blank to keep':''}" autocomplete="new-password"/></div>
        </div>
        <div class="form-row">
          <div class="field"><label>Passphrase (OKX / Bitget)</label><input class="ex-pass" type="password" placeholder="${c.hasPassphrase?'•••• keep':''}" autocomplete="new-password"/></div>
          <div class="field"><label>Permission</label>
            <select class="ex-perm">
              <option value="read-only" ${c.permission!=='trade'?'selected':''}>read-only</option>
              <option value="trade" ${c.permission==='trade'?'selected':''}>trade</option>
            </select>
          </div>
        </div>
        <button class="btn primary save-ex">Save ${name}</button>
      </div>`).join('') || '<div class="muted">No exchanges in config</div>';

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
          AB.$('s_msg').textContent = name + ' credentials saved (masked in UI). Still PAPER until live is enabled.';
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
document.getElementById('presetBalanced')?.addEventListener('click', () => AB.pages.settings.applyPreset('balanced'));
document.getElementById('presetAggressive')?.addEventListener('click', () => AB.pages.settings.applyPreset('aggressive'));

document.getElementById('btnSaveTrading')?.addEventListener('click', async () => {
  const num = (id, fallback) => {
    const v = parseFloat(AB.$(id)?.value);
    return Number.isFinite(v) ? v : fallback;
  };
  const int = (id, fallback) => {
    const v = parseInt(AB.$(id)?.value, 10);
    return Number.isFinite(v) ? v : fallback;
  };

  const trading = {
    strategyMode: AB.$('s_strategy')?.value || 'FuturesCross',
    paperTrading: !!AB.$('s_paper')?.checked,
    paperAutoExecute: !!AB.$('s_auto')?.checked,
    minProfitPercent: num('s_minProfit', 0.025),
    quoteSize: num('s_size', 2000),
    futuresPaperLeverage: Math.min(10, Math.max(1, num('s_lev', 5))),
    futuresMaxOpenPositions: int('s_maxPos', 6),
    futuresStopLossUsd: num('s_stop', -80),
    futuresDailyLossLimitUsd: num('s_dayLimit', -400)
  };
  const risk = {
    minProfitPercent: trading.minProfitPercent,
    quoteSize: trading.quoteSize,
    leverage: trading.futuresPaperLeverage,
    maxOpenPositions: trading.futuresMaxOpenPositions,
    stopLossUsd: trading.futuresStopLossUsd,
    dailyLossLimitUsd: trading.futuresDailyLossLimitUsd,
    maxHoldMinutes: int('s_hold', 20),
    closeBelowNetPercent: num('s_closeWidth', 0.02),
    maxMarginUsagePercent: Math.min(0.9, Math.max(0.05, num('s_marginUse', 0.25))),
    maxNotionalUsd: num('s_maxNotional', 6000),
    paperCooldownMs: int('s_cooldown', 4000),
    paperRequireFullFill: !!AB.$('s_fullFill')?.checked,
    requireRoundTripEdge: !!AB.$('s_reqRt')?.checked,
    includeFunding: !!AB.$('s_funding')?.checked
  };
  try {
    await AB.api.post('/api/settings/trading', trading);
    await AB.api.post('/api/settings/risk', risk);
    AB.$('s_msg').className = 'alert ok';
    AB.$('s_msg').textContent = 'Trading parameters applied at runtime. Paper engine uses new limits immediately.';
    AB.$('s_msg').classList.remove('hidden');
  } catch (e) {
    AB.$('s_msg').className = 'alert warn';
    AB.$('s_msg').textContent = e.message;
    AB.$('s_msg').classList.remove('hidden');
  }
});
