# Steam UI bootstrap source

WSGM's half of the injected asset, which is currently empty. `eng/build-steam-assets.mjs` takes the
bridge, the ownership primitives, every revived Valve surface (`gates/`) and the Quick Access row
host (`components.ts`) from the `steam-ui-toolkit` submodule, adds any fragments here, type-checks
the combined program, strips the TypeScript annotations and formats one reviewable injected asset.
It is compiled as a single unit because it is evaluated in a single CDP call, and the fragments
deliberately share one lexical scope: a gate closes over the bridge's private functions and must not
publish a second runtime API merely to cross a source-file boundary.

Everything Steam-shaped — a literal module id, a store's field names, a localization token, a row —
belongs in the toolkit, so that another host can feed its own data into the same surface. A fragment
lives here only when it is WSGM's own feature and no other host could want it. Today nothing
qualifies: WSGM's library tabs, card badge and download sorting are resident scripts and patches of
their own, not fragments of this asset.

- `gates/` would hold WSGM-only, independently reversible service/store integrations, one file per
  gate, each registering itself with `registerGate(name, gate)`.
- A fragment beside this file would extend the row host or add a surface of its own.

**Adding a fragment is a new file here and nothing else.** The builder discovers fragments by
directory rather than holding a list, and orders them so the emitted asset is byte-stable. The
`--check` mode rebuilds the same combined program and rejects a stale generated file, a stale hash
in `SteamUiAssetCatalog`, an asset that is not exactly one bounded UTF-8 file, or a second `.js`
appearing beside it.
