// Clicks the production SIZE button and reports the resulting queue order.
(async () => {
  try {
    const p = Array.from(g_PopupManager.GetPopups()).find(
      (x) =>
        x.m_popup &&
        x.m_popup.document &&
        x.m_popup.document.querySelector('[data-rbd-droppable-id="1"]'),
    );
    const d = p.m_popup.document;
    const before = downloadsStore.QueuedTransfers.slice()
      .sort((a, b) => a.queue_index - b.queue_index)
      .map((t) => t.queue_index + "=" + appStore.GetAppOverviewByAppID(t.appid).display_name);

    let cap = null;
    for (const e of d.querySelectorAll("*")) {
      if (e.childElementCount === 0 && (e.textContent || "").trim() === "SORT:") {
        cap = e;
        break;
      }
    }
    if (!cap) return "no sort bar";
    const bar = cap.parentElement;
    const btn = Array.from(bar.children).find((c) => (c.innerText || "").trim().startsWith("SIZE"));
    if (!btn) return "no SIZE button";
    btn.click();
    await new Promise((r) => setTimeout(r, 2500));

    const after = downloadsStore.QueuedTransfers.slice()
      .sort((a, b) => a.queue_index - b.queue_index)
      .map((t) => t.queue_index + "=" + appStore.GetAppOverviewByAppID(t.appid).display_name);
    const label = Array.from(bar.children).map((c) => (c.innerText || "").trim());
    return JSON.stringify(
      {
        before,
        after,
        labels: label,
        paused: downloadsStore.CurrentViewingDownloadOverview.paused,
      },
      null,
      1,
    );
  } catch (e) {
    return "THREW: " + String((e && e.stack) || e);
  }
})();
