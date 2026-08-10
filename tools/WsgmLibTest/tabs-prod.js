// Standalone live-Steam smoke probe for the tab mechanism. Production is authoritative in
// SteamLibraryTabs.cs; keep behavior changes synchronized there before using this probe.
(() => {
  try {
    var W = (window.__wsgm = window.__wsgm || {});
    if (!W._react) {
      if (!window.webpackChunksteamui) throw new Error("webpack not ready");
      var req;
      window.webpackChunksteamui.push([
        [Symbol("wsgm")],
        {},
        function (r) {
          req = r;
        },
      ]);
      if (!req) throw new Error("no require");
      for (var id of Object.keys(req.m)) {
        var e;
        try {
          e = req(id);
        } catch (x) {
          continue;
        }
        if (e && e.createElement && e.useMemo && e.version) {
          W._react = e;
          break;
        }
      }
      if (!W._react) throw new Error("React not found");
    }
    var React = W._react;
    W.findInTree = function (node, pred, depth) {
      depth = depth || 0;
      if (depth > 40 || node == null) return null;
      if (Array.isArray(node)) {
        for (var n of node) {
          var r = W.findInTree(n, pred, depth + 1);
          if (r) return r;
        }
        return null;
      }
      if (typeof node !== "object") return null;
      try {
        if (pred(node)) return node;
      } catch (e) {}
      var kids = node.props && node.props.children;
      return kids ? W.findInTree(kids, pred, depth + 1) : null;
    };
    W.makeCollection = function (id, title, appids) {
      var as = window.appStore;
      var apps = appids
        .map(function (a) {
          return as.GetAppOverviewByAppID(a);
        })
        .filter(Boolean);
      var map = new Map();
      apps.forEach(function (a) {
        map.set(a.appid, a);
      });
      return {
        id: id,
        displayName: title,
        allApps: apps,
        visibleApps: apps.slice(),
        apps: map,
        AsDeletableCollection: function () {
          return null;
        },
        AsDragDropCollection: function () {
          return null;
        },
        AsEditableCollection: function () {
          return null;
        },
        bAllowsDragAndDrop: false,
        bIsDeletable: false,
        bIsDynamic: false,
        bIsEditable: false,
        GetAppCountWithToolsFilter: function (f) {
          return f && f.Matches
            ? apps.filter(function (x) {
                return f.Matches(x);
              }).length
            : apps.length;
        },
      };
    };
    W.patchTabs = function (v) {
      try {
        var isNested = Array.isArray(v) && Array.isArray(v[0]);
        var tabs = isNested ? v[0] : v;
        if (!Array.isArray(tabs)) return v;
        var tmpl = tabs.find(function (t) {
          return t && t.id === "AllGames";
        });
        if (!tmpl) return v;
        if (!W._gridType) {
          var g = W.findInTree(tmpl.content, function (el) {
            return (
              el &&
              el.type &&
              el.type.toString &&
              el.type.toString().includes("Library_FilteredByHeader")
            );
          });
          if (g) {
            W._gridType = g.type;
            W._gridProps = g.props;
          }
        }
        var existing = new Set(
          tabs.map(function (t) {
            return t && t.id;
          }),
        );
        var add = [];
        for (var d of W.tabs || []) {
          if (existing.has(d.id)) continue;
          var coll = W.makeCollection(d.id, d.title, d.appids || []);
          var content = tmpl.content;
          if (W._gridType && React) {
            content = React.createElement(
              W._gridType,
              Object.assign({}, W._gridProps, { collection: coll }),
            );
          }
          (function (def, content) {
            add.push({
              title: def.title,
              id: def.id,
              content: content,
              footer: tmpl.footer,
              renderTabAddon: function () {
                return React
                  ? React.createElement("span", null, String((def.appids || []).length))
                  : null;
              },
            });
          })(d, content);
        }
        if (!add.length) return v;
        var out = tabs.concat(add);
        return isNested ? [out, v[1]] : out;
      } catch (e) {
        return v;
      }
    };
    if (!W.tabsInstalled) {
      var internals = React.__CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE;
      if (!internals || !("H" in internals)) throw new Error("React dispatcher slot not found");
      var wrapped = new WeakMap();
      var cur = internals.H;
      Object.defineProperty(internals, "H", {
        configurable: true,
        get: function () {
          var c = cur;
          if (!c || typeof c !== "object" || typeof c.useMemo !== "function") return c;
          var w = wrapped.get(c);
          if (!w) {
            var realUseMemo = c.useMemo;
            w = Object.create(c);
            w.useMemo = function (fn, deps) {
              return realUseMemo.call(
                c,
                function () {
                  return W.patchTabs(fn());
                },
                deps,
              );
            };
            wrapped.set(c, w);
          }
          return w;
        },
        set: function (v) {
          cur = v;
        },
      });
      W.disableTabs = function () {
        try {
          Object.defineProperty(internals, "H", { configurable: true, writable: true, value: cur });
        } catch (e) {}
        W.tabsInstalled = false;
      };
      W.tabsInstalled = true;
    }
    window.__wsgm.tabs = [
      { id: "wsgm-e", title: "E: Library", appids: [22600, 31280, 70600] },
      { id: "wsgm-d", title: "D: Library", appids: [293760, 1623730, 3602290] },
    ];
    if (window.__wsgm.forceRerender) window.__wsgm.forceRerender();
    return JSON.stringify({
      ok: true,
      installed: !!window.__wsgm.tabsInstalled,
      count: (window.__wsgm.tabs || []).length,
    });
  } catch (e) {
    return JSON.stringify({ ok: false, err: String((e && e.stack) || e) });
  }
})();
