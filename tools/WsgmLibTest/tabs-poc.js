// WSGM injected-tabs — run in Steam's SharedJSContext.
// Replicates TabMaster without Decky: capture webpack, find React, hijack the
// current dispatcher's useMemo, and rewrite the library tab array to append WSGM
// tabs. Each tab renders a FAKE in-memory collection (a plain object of the tab's
// appids' overviews) through Steam's own grid component — no real collection.
// This test build defines two tabs: the E: library's games and the D: library's.
(async () => {
  try {
    if (!window.webpackChunksteamui) {
      return JSON.stringify({ ok: false, err: 'no webpackChunksteamui (wrong target / not ready)' });
    }
    const W = window.__wsgm = window.__wsgm || {};

    // --- capture React (once) ---
    if (!W._react) {
      let req;
      window.webpackChunksteamui.push([[Symbol('wsgm')], {}, (r) => { req = r; }]);
      if (!req) return JSON.stringify({ ok: false, err: 'no require captured' });
      for (const id of Object.keys(req.m)) {
        let e; try { e = req(id); } catch (x) { continue; }
        if (e && e.createElement && e.useMemo && e.version) { W._react = e; break; }
      }
      if (!W._react) return JSON.stringify({ ok: false, err: 'React not found' });
    }
    const React = W._react;

    // --- build tab definitions from the install folders (per-drive appids) ---
    const folders = await SteamClient.InstallFolder.GetInstallFolders();
    const appidsFor = (prefix) => {
      const f = folders.find(x => (x.strFolderPath || '').toUpperCase().startsWith(prefix));
      return f ? (f.vecApps || []).map(a => a.nAppID != null ? a.nAppID : (a.appid != null ? a.appid : a)) : [];
    };
    W.tabs = [
      { title: 'E: Library', id: 'wsgm-e', appids: appidsFor('E:') },
      { title: 'D: Library', id: 'wsgm-d', appids: appidsFor('D:') },
    ];

    // --- helpers ---
    W.findInTree = function (node, pred, depth) {
      depth = depth || 0;
      if (depth > 40 || node == null) return null;
      if (Array.isArray(node)) {
        for (const n of node) { const r = W.findInTree(n, pred, depth + 1); if (r) return r; }
        return null;
      }
      if (typeof node !== 'object') return null;
      try { if (pred(node)) return node; } catch (e) { }
      const kids = node.props && node.props.children;
      return kids ? W.findInTree(kids, pred, depth + 1) : null;
    };

    W.makeCollection = function (id, title, appids) {
      const as = window.appStore;
      const apps = appids.map(a => as.GetAppOverviewByAppID(a)).filter(Boolean);
      const map = new Map(); apps.forEach(a => map.set(a.appid, a));
      return {
        id, displayName: title,
        allApps: apps, visibleApps: apps.slice(), apps: map,
        AsDeletableCollection: () => null, AsDragDropCollection: () => null, AsEditableCollection: () => null,
        bAllowsDragAndDrop: false, bIsDeletable: false, bIsDynamic: false, bIsEditable: false,
        GetAppCountWithToolsFilter: (f) => (f && f.Matches) ? apps.filter(x => f.Matches(x)).length : apps.length,
      };
    };

    // Rewrites a memoized value IF it is the library tab array (has an 'AllGames'
    // tab), appending a filtered tab per definition. Scoped to that array only.
    W.patchTabs = function (v) {
      try {
        const isNested = Array.isArray(v) && Array.isArray(v[0]);
        const tabs = isNested ? v[0] : v;
        if (!Array.isArray(tabs)) return v;
        const tmpl = tabs.find(t => t && t.id === 'AllGames');
        if (!tmpl) return v;

        if (!W._gridType) {
          const gridEl = W.findInTree(tmpl.content, el =>
            el && el.type && el.type.toString && el.type.toString().includes('Library_FilteredByHeader'));
          if (gridEl) { W._gridType = gridEl.type; W._gridProps = gridEl.props; }
        }

        const existing = new Set(tabs.map(t => t && t.id));
        const add = [];
        for (const d of (W.tabs || [])) {
          if (existing.has(d.id)) continue;
          const coll = W.makeCollection(d.id, d.title, d.appids || []);
          let content = tmpl.content;
          if (W._gridType && React) {
            content = React.createElement(W._gridType, Object.assign({}, W._gridProps, { collection: coll }));
          }
          add.push({
            title: d.title,
            id: d.id,
            content,
            footer: tmpl.footer,
            renderTabAddon: () => React ? React.createElement('span', null, String((d.appids || []).length)) : null,
          });
        }
        if (!add.length) return v;
        const out = [...tabs, ...add];
        return isNested ? [out, v[1]] : out;
      } catch (e) { return v; }
    };

    // --- install the dispatcher hijack once ---
    if (!W.tabsInstalled) {
      const internals = React.__CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE;
      if (!internals || !('H' in internals)) {
        return JSON.stringify({ ok: false, err: 'React dispatcher slot not found' });
      }
      const wrapped = new WeakMap();
      let cur = internals.H;
      Object.defineProperty(internals, 'H', {
        configurable: true,
        get() {
          const c = cur;
          if (!c || typeof c !== 'object' || typeof c.useMemo !== 'function') return c;
          let w = wrapped.get(c);
          if (!w) {
            const realUseMemo = c.useMemo;
            w = Object.create(c);
            w.useMemo = function (fn, deps) {
              return realUseMemo.call(c, function () { return W.patchTabs(fn()); }, deps);
            };
            wrapped.set(c, w);
          }
          return w;
        },
        set(v) { cur = v; },
      });
      W.disableTabs = () => {
        try { Object.defineProperty(internals, 'H', { configurable: true, writable: true, value: cur }); } catch (e) { }
        W.tabsInstalled = false;
      };
      W.tabsInstalled = true;
    }

    return JSON.stringify({ ok: true, react: React.version, tabs: W.tabs.map(t => ({ id: t.id, n: t.appids.length })) });
  } catch (e) {
    return JSON.stringify({ ok: false, err: String((e && e.stack) || e) });
  }
})()
