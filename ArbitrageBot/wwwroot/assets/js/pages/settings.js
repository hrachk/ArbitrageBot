AB.pages.settings = {
  async load() {
    try {
      const s = await AB.api.get('/api/settings');
      AB.state.settings = s;
      this.fill(s);
    } catch (e) {
      AB.$('s_msg').className = 'alert warn';
      AB.$('s_msg').textContent = 'Settings load failed: ' + e.message;
      AB.$('s_msg').classList.remove('hidden');
    }
  },
  fill(s) {
    const t = s.trading || {};
    AB.$('s_strategy').value = t.strategyMode || 'FuturesCross';
    AB.$('s_paper').checked = t.paperTrading !== false;
    AB.$('s_auto').checked = !!t.paperAutoExecute;
    AB.$('s_minProfit').value = t.minProfitPercent ?? 0.12;
    AB.$('s_size').value = t.quoteSize ?? 400;
    AB.$('s_lev').value = t.futuresPaperLeverage ?? 5;
    AB.$('s_maxPos').value = t.futuresMaxOpenPositions ?? 3;
    AB.$('s_stop').value = t.futuresStopLossUsd ?? -30;
    AB.$('s_dayLimit').value = t.futuresDailyLossLimitUsd ?? -100;

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
              <option value="read-only" ${c.permission==='read-only'?'selected':''}>read-only</option>
              <option value="trade" ${c.permission==='trade'?'selected':''}>trade</option>
            </select>
          </div>
        </div>
        <button type="button" class="btn primary btn-save-ex">Save ${name}</button>
      </div>
    `).join('');

    document.querySelectorAll('.btn-save-ex').forEach(btn => {
      btn.onclick = async () => {
        const card = btn.closest('.ex-card');
        const name = card.dataset.ex;
        const body = {
          enabled: card.querySelector('.ex-enabled').checked,
          apiKey: card.querySelector('.ex-key').value,
          apiSecret: card.querySelector('.ex-secret').value,
          passphrase: card.querySelector('.ex-pass').value,
          permission: card.querySelector('.ex-perm').value
        };
        try {
          await AB.api.post('/api/settings/exchanges/' + encodeURIComponent(name), body);
          AB.$('s_msg').className = 'alert ok';
          AB.$('s_msg').textContent = name + ' credentials saved (masked in UI, not returned). Still PAPER until live mode is enabled.';
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

document.getElementById('btnSaveTrading')?.addEventListener('click', async () => {
  const body = {
    strategyMode: AB.$('s_strategy').value,
    paperTrading: AB.$('s_paper').checked,
    paperAutoExecute: AB.$('s_auto').checked,
    minProfitPercent: parseFloat(AB.$('s_minProfit').value),
    quoteSize: parseFloat(AB.$('s_size').value),
    futuresPaperLeverage: parseFloat(AB.$('s_lev').value),
    futuresMaxOpenPositions: parseInt(AB.$('s_maxPos').value, 10),
    futuresStopLossUsd: parseFloat(AB.$('s_stop').value),
    futuresDailyLossLimitUsd: parseFloat(AB.$('s_dayLimit').value)
  };
  try {
    await AB.api.post('/api/settings/trading', body);
    AB.$('s_msg').className = 'alert ok';
    AB.$('s_msg').textContent = 'Trading settings saved to local store. Restart worker / process to apply runtime options fully (Phase 1: persistence; live apply next).';
    AB.$('s_msg').classList.remove('hidden');
  } catch (e) {
    AB.$('s_msg').className = 'alert warn';
    AB.$('s_msg').textContent = e.message;
    AB.$('s_msg').classList.remove('hidden');
  }
});
