// Read-only: is the production sort bar rendered, and where?
(() => {
  try {
    const out = [];
    for (const p of Array.from(g_PopupManager.GetPopups())) {
      const d = p.m_popup && p.m_popup.document;
      if (!d || !d.body) continue;
      const drop = d.querySelector('[data-rbd-droppable-id="1"]');
      const hit = [];
      for (const e of d.querySelectorAll("*")) {
        if (e.childElementCount === 0 && (e.textContent || "").trim() === "SORT:") hit.push(e);
      }
      let bar = null;
      if (hit.length) {
        bar = hit[0].parentElement;
      }
      const r = (el) => {
        const b = el.getBoundingClientRect();
        return { l: Math.round(b.left), r: Math.round(b.right), w: Math.round(b.width) };
      };
      out.push({
        popup: p.m_strName,
        hasQueuedSection: !!drop,
        sortBars: hit.length,
        barText: bar ? (bar.innerText || "").replace(/\n/g, " ") : null,
        barRect: bar ? r(bar) : null,
        headerText: drop
          ? (drop.parentElement.querySelector("h3").innerText || "").replace(/\n/g, " | ")
          : null,
        buttons: bar ? Array.from(bar.children).map((c) => (c.innerText || "").trim()) : null,
      });
    }
    return JSON.stringify(out, null, 1);
  } catch (e) {
    return "THREW: " + String((e && e.stack) || e);
  }
})();
