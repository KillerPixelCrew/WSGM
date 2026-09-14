# Steam UI bootstrap source

This is WSGM's half of the injected asset, and right now it is empty.

`eng/build-steam-assets.mjs` takes the bridge, the ownership and RPC primitives, the module
resolver, the row and section glyphs (`icons.ts`), every revived Valve surface (`gates/`) and the
Quick Access row host (`components.ts`) from the `steam-ui-toolkit` submodule, adds any fragments
from here, type-checks the combined program, strips the TypeScript annotations and formats one
reviewable injected asset.

It is compiled as a single unit because it is evaluated in a single CDP call, and the fragments
deliberately share one lexical scope: a gate closes over the bridge's private functions and must not
publish a second runtime API just to cross a source-file boundary.

## What belongs here, and what does not

Reusable Valve surfaces belong in the toolkit, so another host can feed its own data into the same
surface. WSGM-only features keep their own fingerprints but resolve them through the toolkit's
`SteamUiModuleResolver` rather than scanning the registry themselves.

A fragment lives here only when it is WSGM's own feature and no other host could possibly want it.
Nothing qualifies today: the library tabs and download sorting are resident scripts and patches of
their own, and the card badge became a toolkit surface (`gates/library-badge.ts`) that WSGM only
feeds data into.

If something did qualify:

- `gates/` would hold WSGM-only, independently reversible service and store integrations, one file
  per gate, each registering itself with `registerGate(name, gate)`.
- A fragment beside this file would extend the row host or add a surface of its own.

## Adding one is a new file here and nothing else

The builder finds fragments by directory rather than holding a list, and orders them so the emitted
asset is byte-stable. The `--check` mode rebuilds the same combined program and rejects a stale
generated file, a stale hash in `SteamUiAssetCatalog`, an asset that is not exactly one bounded
UTF-8 file, or a second `.js` appearing beside it.
