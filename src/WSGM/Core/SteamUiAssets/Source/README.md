# Steam UI bootstrap source

This is WSGM's half of the injected asset: the renderers for WSGM's own pages in Steam.

`eng/build-steam-assets.mjs` takes the bridge, the ownership and RPC primitives, the module
resolver, the row and section glyphs (`icons.ts`), every revived Valve surface (`gates/`) and the
Quick Access row host (`components.ts`) from the `steam-ui-toolkit` submodule, in the order its
`eng/steam-ui-fragments.mjs` defines, adds the fragments from here, type-checks the combined
program, strips the TypeScript annotations and formats one reviewable injected asset. Each fragment
opens with a `// @fragment <label>` line (`consumer/<file>` for these), which the toolkit's checks
use to take a whole fragment out of the asset by name.

It is compiled as a single unit because it is evaluated in a single CDP call, and the fragments
deliberately share one lexical scope: a gate closes over the bridge's private functions and must not
publish a second runtime API just to cross a source-file boundary.

## What belongs here, and what does not

Reusable Valve surfaces belong in the toolkit, so another host can feed its own data into the same
surface. WSGM-only features keep their own fingerprints but resolve them through the toolkit's
`SteamUiModuleResolver` rather than scanning the registry themselves.

A fragment lives here only when it is WSGM's own feature and no other host could possibly want it.
Ten qualify. Six are pages registered with the toolkit's `registerSteamPage`:

- `artwork-browser.ts`, the Change Artwork page.
- `library-import.ts`, the Game Library's import page.
- `wsgm-settings.ts`, WSGM's settings page, opened from WSGM's row in Steam's main menu. It is only
  the page's data and commands: the toolkit's `renderSteamSettings` draws it with Steam's own
  Settings components, because any host could want a settings page that looks like Steam's.
- `wsgm-graphics.ts`, the Graphics page, opened from its row in Steam's main menu while a graphics
  package runs. Drawn by the same renderer, one sidebar page per adapter and display; a game
  override is marked in Steam's accent blue, with no Use global control.
- `themes.ts`, the Themes page: CSSLoader-compatible themes browsed from DeckThemes, installed and
  managed.
- `animations.ts`, the Animations page: SteamDeckRepo's boot movies browsed, downloaded and chosen
  for Big Picture's start.

Those two share `page-kit.ts`, which is no feature of its own: the command sender and the tabbed
frame's tab switch and banner, which differ between them only by patch id.

Four are gates of their own, registered with `registerGate`:

- `chord-reset.ts`, the guide-chord editor's reset hook. It claims
  `SteamClient.Input.SetSelectedConfigForApp` with the toolkit's `claimMember` and reports a reset
  of the chord pseudo-app to WSGM, which puts Valve's template back before Steam reloads it. See
  docs/steam-input.md, "Guide button chord edits".
- `controller-caps.ts`, the controller capability hook. It claims the one generated RPC every store
  reads the controller list through, `SteamInputManager.GetControllerList`, and clears the
  capability bits the active glyph profile marks absent (trackpads, touch-sensing sticks) on WSGM's
  virtual pad, so the pages stop drawing settings for controls the handheld does not have. See
  docs/steam-cef.md, "Physical glyphs are CSS".
- `download-sort.ts`, the Name / Size / Type buttons in the download queue's header. Its header
  transform sits on the toolkit's shared JSX-runtime claim, and a sort renumbers the queue through
  `SteamClient.Downloads.SetQueueIndex`, reporting the positions Steam refused to WSGM
  (`SteamDownloadSort.cs`). See docs/steam-cef.md §12.
- `library-tabs.ts`, the library tabs' transform on the toolkit's shared `useMemo` claim. The tabs
  themselves are the resident script in `SteamLibraryTabs.cs`, which installs through this gate.

The card badge became a toolkit surface (`gates/library-badge.ts`) that WSGM only feeds data into. A
new fragment that would draw its own imitation of a Steam element does not belong here: the element
goes into the toolkit, resolved from Steam's own components, and the fragment uses it.

## Adding one is a new file here and nothing else

The builder finds fragments by directory rather than holding a list, and orders them so the emitted
asset is byte-stable. A fragment opens with runtime code, not a `type` alias: TypeScript erases the
comments that lead an erased declaration, and the builder refuses an asset that lost a fragment
marker. The `--check` mode rebuilds the same combined program and rejects a stale generated file, an
asset that is not exactly one UTF-8 file without a byte-order mark, or a second `.js` appearing
beside it. `SteamUiAssetCatalog` hashes the embedded bytes when it loads them; that hash is the
identity the bridge uses to replace a script a previous build left running.
