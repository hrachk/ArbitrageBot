AB.pages = AB.pages || {};
AB.pages.live = {
  async load() {
    const cards = document.getElementById('live_balCards');
    const totalEl = document.getElementById('live_total');
    try {
      const data = await AB.api.get('/api/live/balances');
      const exs = data.exchanges || data.items || [];
      let sum = Number(data.totalUsdtApprox) || 0;
      if (cards) {
        if (!exs.length) {
          cards.innerHTML = '<div class="empty">No exchange data</div>';
        } else {
          cards.innerHTML = exs.map(e => {
            if (!e.ok) {
              return '<div class="kpi" style="border-color:rgba(248,113,113,.3)"><div class="kpi-l">' + (e.exchange || '?') +
                ' <span class="neg" style="font-size:10px">● error</span></div><div class="neg" style="font-size:11px;margin-top:6px">' +
                (e.error || 'failed') + '</div></div>';
            }
            const u = Number(e.usdtTotal != null ? e.usdtTotal : 0);
            return '<div class="kpi" style="border-color:rgba(56,189,248,.25)"><div class="kpi-l">' + (e.exchange || '?') +
              ' <span class="pos" style="font-size:10px">● live</span></div><div class="kpi-v" style="color:var(--blue);font-size:16px">$' +
              u.toFixed(2) + '</div><div class="muted" style="font-size:11px;margin-top:4px">' +
              (e.accountMode || e.permission || '') + '</div></div>';
          }).join('');
        }
      }
      if (totalEl) totalEl.textContent = '$' + sum.toFixed(2);
    } catch (e) {
      if (cards) cards.innerHTML = '<div class="empty">' + (e.message || e) + '</div>';
    }
  },
  render() { this.load(); }
};
document.addEventListener('DOMContentLoaded', () => {
  const b = document.getElementById('btnLiveRefresh');
  if (b) b.addEventListener('click', () => AB.pages.live.load());
});
