/* ArbitrageBot Telegram Mini App — Reports panel */
(function () {
  const tg = window.Telegram && window.Telegram.WebApp;
  if (tg) {
    try {
      tg.ready();
      tg.expand();
      if (tg.setHeaderColor) tg.setHeaderColor("#0b1220");
      if (tg.setBackgroundColor) tg.setBackgroundColor("#0b1220");
    } catch (_) { /* ignore */ }
  }

  let days = 7;
  const $ = (id) => document.getElementById(id);

  function money(n, dig = 2) {
    if (n == null || Number.isNaN(+n)) return "—";
    const v = +n;
    const sign = v > 0 ? "+" : "";
    return sign + v.toFixed(dig) + " USDT";
  }
  function pct(n, dig = 2) {
    if (n == null || Number.isNaN(+n)) return "—";
    const v = +n;
    const sign = v > 0 ? "+" : "";
    return sign + v.toFixed(dig) + "%";
  }
  function cls(n) {
    if (n == null || Number.isNaN(+n)) return "";
    return +n >= 0 ? "pos" : "neg";
  }
  function toast(msg) {
    const el = $("toast");
    el.textContent = msg;
    el.hidden = false;
    clearTimeout(toast._t);
    toast._t = setTimeout(() => { el.hidden = true; }, 2200);
  }

  function apiBase() {
    // Same origin when served from bot host (/tg/)
    return "";
  }

  async function load() {
    $("btnRefresh").disabled = true;
    try {
      const url = apiBase() + "/api/tg/report?days=" + encodeURIComponent(days);
      const res = await fetch(url, { cache: "no-store" });
      if (!res.ok) throw new Error("HTTP " + res.status);
      const data = await res.json();
      render(data);
      toast("Updated");
    } catch (e) {
      console.error(e);
      toast("Ошибка загрузки · " + (e.message || e));
      $("status").textContent = "offline / API error";
    } finally {
      $("btnRefresh").disabled = false;
    }
  }

  function render(data) {
    const p = data.performance || {};
    const s = data.summary || {};

    $("hdrMode").textContent =
      (data.mode || "PAPER") + (data.paused ? " · PAUSED" : "") + " · Reports";

    const net = p.netPnl;
    const netEl = $("netPnl");
    netEl.textContent = money(net);
    netEl.className = "hero-val " + cls(net);
    $("netPct").textContent =
      (p.netPctOfEquity != null ? pct(p.netPctOfEquity) + " of equity" : "") +
      (p.fromUtc && p.toUtc ? " · " + p.fromUtc + " → " + p.toUtc : "");

    $("winRate").textContent = p.winRate != null ? (+p.winRate).toFixed(1) + "%" : "—";
    $("trades").textContent = p.totalTrades != null ? String(p.totalTrades) : "—";
    $("pf").textContent = p.profitFactor != null ? (+p.profitFactor).toFixed(2) : "—";
    $("dd").textContent = p.maxDrawdown != null ? money(p.maxDrawdown) : "—";
    $("exp").textContent = p.expectancy != null ? money(p.expectancy) : "—";
    $("opens").textContent = data.opens != null ? String(data.opens) : "—";

    $("gross").textContent = money(p.grossPnl);
    $("gross").className = cls(p.grossPnl);
    $("fees").textContent = money(p.totalFees != null ? -Math.abs(+p.totalFees) : null);
    $("fees").className = "neg";
    $("funding").textContent = money(p.totalFunding);
    $("funding").className = cls(p.totalFunding);
    $("net2").textContent = money(p.netPnl);
    $("net2").className = cls(p.netPnl);

    const dayR = s.realizedPnlUsd ?? s.realizedDay ?? s.dayRealized ?? s.realized;
    $("dayReal").textContent = money(dayR);
    $("dayReal").className = cls(dayR);
    $("scans").textContent =
      (s.scans != null ? s.scans : "—") + " / " + (s.skips != null ? s.skips : "—");
    $("status").textContent =
      (data.mode || "—") + (data.paused ? " paused" : " running");

    // Heatmap last ~14 days
    const heat = $("heat");
    heat.innerHTML = "";
    const daily = Array.isArray(p.daily) ? p.daily.slice(-14) : [];
    if (!daily.length) {
      heat.innerHTML = '<div class="cell"><div class="d">no data</div></div>';
    } else {
      daily.forEach((d) => {
        const day = d.day || d.date || d.utc || "";
        const pnl = d.pnl ?? d.netPnl ?? 0;
        const tr = d.trades ?? d.closes ?? 0;
        const short = String(day).slice(5); // MM-DD
        const cell = document.createElement("div");
        cell.className = "cell " + cls(pnl);
        cell.innerHTML =
          '<div class="d">' + short + "</div>" +
          '<div class="p">' + (pnl > 0 ? "+" : "") + (+pnl).toFixed(1) + "</div>" +
          '<div class="d">' + tr + " tr</div>";
        heat.appendChild(cell);
      });
    }

    if (p.bestTrade) {
      $("bestL").textContent = "Best · " + (p.bestTrade.symbol || "");
      $("bestV").textContent = money(p.bestTrade.pnl);
    } else {
      $("bestL").textContent = "Best";
      $("bestV").textContent = "—";
    }
    if (p.worstTrade) {
      $("worstL").textContent = "Worst · " + (p.worstTrade.symbol || "");
      $("worstV").textContent = money(p.worstTrade.pnl);
    } else {
      $("worstL").textContent = "Worst";
      $("worstV").textContent = "—";
    }

    const t = data.utc ? new Date(data.utc) : new Date();
    $("updated").textContent =
      "UTC " + t.toISOString().replace("T", " ").slice(0, 19);
  }

  document.getElementById("rangeChips").addEventListener("click", (e) => {
    const btn = e.target.closest("button[data-d]");
    if (!btn) return;
    days = parseInt(btn.getAttribute("data-d"), 10);
    document.querySelectorAll("#rangeChips button").forEach((b) => b.classList.remove("on"));
    btn.classList.add("on");
    load();
  });

  $("btnRefresh").addEventListener("click", load);

  // Auto refresh every 45s
  load();
  setInterval(load, 45000);
})();
