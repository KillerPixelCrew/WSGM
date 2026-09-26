# Steam UI bootstrap source

This is WSGM's half of the injected asset: the renderers for WSGM's own pages in Steam.

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
Four qualify, each with a gate of its own; the first three are pages registered with
`registerSteamPageRenderer`:

- `artwork-browser.ts`, the Change Artwork page.
- `library-import.ts`, the Game Library's import page.
- `wsgm-settings.ts`, WSGM's settings page, opened from WSGM's row in Steam's main menu. It is only
  the page's data and commands: the toolkit's `renderSteamSettings` draws it with Steam's own
  Settings components, because any host could want a settings page that looks like Steam's.
- `chord-reset.ts`, the guide-chord editor's reset hook. It wraps
  `SteamClient.Input.SetSelectedConfigForApp` and reports a reset of the chord pseudo-app to WSGM,
  which puts Valve's template back before Steam reloads it. See docs/steam-input.md, "Guide button
  chord edits".

The library tabs and download sorting are resident scripts and patches of their own, and the card
badge became a toolkit surface (`gates/library-badge.ts`) that WSGM only feeds data into. A new
fragment that would draw its own imitation of a Steam element does not belong here: the element goes
into the toolkit, resolved from Steam's own components, and the fragment uses it.

## Adding one is a new file here and nothing else

The builder finds fragments by directory rather than holding a list, and orders them so the emitted
asset is byte-stable. The `--check` mode rebuilds the same combined program and rejects a stale
generated file, a stale hash in `SteamUiAssetCatalog`, an asset that is not exactly one bounded
UTF-8 file, or a second `.js` appearing beside it.
