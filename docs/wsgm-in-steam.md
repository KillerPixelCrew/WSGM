# WSGM in Steam

WSGM has a row of its own in Big Picture's main menu, the left flyout with Home, Library, Store and
Power. It is called WSGM, sits just above Power, and opens WSGM's settings page. The page is laid
out like Steam's own Settings, with a sidebar of pages, and B leaves it the way it leaves Settings.

## What is on it

A limited set of WSGM's global settings, every one of which configures WSGM itself:

| Page              | Settings                                                                                                                                                                                                                                                                     | Takes effect                                                                                                           |
| ----------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------- |
| Steam integration | the CEF master switch; library tabs, the SD-card library manager, SD formatting, formatting from Steam's storage page, the Home carousel and its uninstalled games; the Wi-Fi indicator, the native Quick Access bridge, keep awake during downloads, download queue sorting | at once, through the shell's config reload                                                                             |
| Startup           | start WSGM at sign-in; start in game or desktop mode                                                                                                                                                                                                                         | at the next start; boot.json is rewritten with the setting                                                             |
| Steam Input       | blocking Steam Input while WSGM's panels are open; Steam Input management, with the shim's state                                                                                                                                                                             | the lease at the next panel; management at once, with the same elevation and pending-update behaviour as WSGM Settings |
| Plugins           | each installed plugin's on/off switch and, while it runs, its declared settings; the device plugin's settings, by section                                                                                                                                                    | as in WSGM Settings and Quick Access                                                                                   |

Turning the CEF master switch off asks first, in Steam's own destructive confirm: it removes this
page and every WSGM feature in Steam, which come back only from WSGM's overlay or WSGM Settings.
Turning the native Quick Access bridge off asks too, but the page stays: WSGM's pages follow CEF
itself, not the bridge. A plugin's secret is never sent to Steam. Its row says whether one is set
and takes a new one.

Windows and other external state are not here, as they are not in WSGM Settings; they are on the
overlay and in Quick Access. Artwork credentials and the Game Library defaults stay in WSGM
Settings.

## How a change is saved

Each change is one field, written through the config store's read-modify-write path, so nothing else
in the file is rewritten. The shell's config reload then applies it exactly as a save from WSGM
Settings would, and the page shows the new value straight away rather than after the reload. The
start settings rewrite boot.json in the same transaction, and Steam Input management reconciles the
shim after the save and outside the lock, through the helper Settings uses.

WSGM Settings saves by writing its whole snapshot back. The fields this page writes are the
exception: a Settings window keeps whatever is saved for any of them it did not change itself, so a
change made in Steam while it was open survives its next save.

## What is drawn with what

Everything is Steam's own UI, found by fingerprints checked against the live client:

- The menu row is Valve's own route entry, taken from the entries the menu renders. It is active on
  the page, and selecting it navigates with Valve's own action.
- The page is the toolkit's settings renderer: the routed sidebar Steam's Settings is built on, its
  settings sections, toggle, dropdown, slider, text and value fields, small buttons, and the generic
  confirm modal.
- WSGM's mark and the sidebar icons are single-colour glyphs drawn the way Valve draws its own.

The reusable parts are in the toolkit, which documents each fingerprint in its reference: the
navigation panel surface and `settings.ts`. WSGM's side is `WsgmSteamSettingsService`, which decides
what is on the page and saves changes, `SteamWsgmSettingsSurface`, and the thin `wsgm-settings.ts`.
