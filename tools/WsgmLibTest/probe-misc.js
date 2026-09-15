// Download sort bar and bridge presence probes, merged from the former probe-click.js,
// probe-crash.js, probe-final.js and probe-verify.js.
//   node run-file.mjs probe-misc.js --section <name>
// Without --section the script only lists its sections.
//
// Read-only sections:
//   crash   which __steamUi bridges exist and whether Audio and Perf are bridge-owned namespaces
//   final   whether the injected bridge exists, its asset hash prefix and Perf ownership
//   verify  whether the production download sort bar is rendered, and where
// Mutating sections, attended only:
//   click   clicks the production SIZE sort button and reports the resulting queue order
(() => {
  const readOnly = {
    crash() {
      const out = {};
      out.bridge = Object.keys(window).filter((k) => k.indexOf("__steamUi") === 0);
      const s = window.SteamClient && window.SteamClient.System;
      out.audio = !!(s && s.Audio);
      out.audioOwned = !!(s && s.Audio && s.Audio.__steamUiOwnedNamespace === true);
      out.perf = !!(s && s.Perf);
      out.perfOwned = !!(s && s.Perf && s.Perf.__steamUiOwnedNamespace === true);
      return JSON.stringify(out);
    },

    final() {
      const b = window.__steamUi_v1_28d7c54a;
      if (!b) return JSON.stringify({ bridge: false });
      return JSON.stringify({
        bridge: true,
        asset: b.assetHash && b.assetHash.slice(0, 8),
        perfOwned: !!window.SteamClient?.System?.Perf?.__steamUiOwnedNamespace,
      });
    },

    // Is the production sort bar rendered, and where?
    verify() {
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
    },
  };

  const mutating = {
    // Clicks the production SIZE button and reports the resulting queue order.
    async click() {
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
        const btn = Array.from(bar.children).find((c) =>
          (c.innerText || "").trim().startsWith("SIZE"),
        );
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
    },
  };

  const section = typeof __probe === "string" ? __probe : null;
  if (section !== null && Object.hasOwn(readOnly, section)) return readOnly[section]();
  if (section !== null && Object.hasOwn(mutating, section)) return mutating[section]();
  return JSON.stringify({
    section,
    readOnly: Object.keys(readOnly),
    mutating: Object.keys(mutating),
  });
})();
