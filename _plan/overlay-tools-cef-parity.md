# Overlay Tools: complete CEF feature parity

Build CSS Loader, Video Switcher, Steam Artwork Changer and Library Importer as complete native
Overlay tools. Every operation, setting, preview and result available in the current WSGM CEF
version must be available without leaving the Overlay. This is the implementation plan; feature
implementation and live validation have not started.

The baseline is the checked-in CEF code at WSGM `3159ec85`, with SteamUiToolkit `3a65078`. Changes
to a CEF contract during implementation must update the parity checklist rather than silently
reducing the Overlay's scope.

## Current state and the first failure

The session already constructs `ThemeService`, `AnimationService`, `GameLibraryService` and
`SteamArtworkBrowserSource`. The first three have partial Overlay views. Their absence is not
evidence that the services need replacing.

`OverlayWindow.Workspace.RefreshWorkspace` derives the rail from visible destination-root
`ActionButton`s with an `OverlayPage` command parameter. Themes and Animations lack that parameter.
Game Library also lacks it and is still declared under Steam. Consequently all three are omitted
from the new rail. Artwork has no Overlay host, route or source attachment at all.

| Tool                  | Existing shared owner                                                   | Current Overlay gap                                                                                                                                                                                          |
| --------------------- | ----------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| CSS Loader            | `ThemeService`, `Core/Themes`, `SteamThemesSurface`                     | Missing rail entry; text-only browsing/detail; no screenshot gallery or complete Settings page; slider patches and color components use less suitable editors.                                               |
| Video Switcher        | `AnimationService`, `Core/Animations`, `SteamAnimationsSurface`         | Missing rail entry; no video playback, thumbnail gallery, local WebM picker or complete Settings page.                                                                                                       |
| Steam Artwork Changer | `SteamArtworkBrowserSource`, `Core/Artwork`, toolkit `SteamApps`        | Entire native Overlay UI and attachment missing.                                                                                                                                                             |
| Library Importer      | `GameLibraryService`, `GameLibraryArtwork`, `SteamLibraryImportSurface` | Wrong destination and omitted rail entry; no local folder-add flow, complete filtered review or artwork workspace. Existing selection, scan, launch cycling, acknowledgement and apply code can be retained. |

The CEF source fragments are `themes.ts`, `animations.ts`, `artwork-browser.ts` and
`library-import.ts` under `src/WSGM/Core/SteamUiAssets/Source`. Their corresponding typed C#
surfaces define the command baseline. These are WSGM's CEF versions, not every feature offered by
upstream Decky plugins. For example, DeckThemes account starring and submissions are not in scope.

## Additional defects found in the same audit

These findings are established from current control flow. Their user-visible reproduction still
needs the maintainer's manual pass; no live Steam session was modified and no test suite was run.

| ID  | Priority | Finding and evidence                                                                                                                                                                                                                                                                                                                                                                             | Planned correction                                                                                                                                                                                                   |
| --- | -------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| A1  | P1       | Back bypasses a service view's deeper levels. `TryCancelSubView` applies its depth-at-most-two rail/home rules before `CancelOpenPage`. A `ServiceSubView` keeps its own detail stack while the window stays at depth two, so Back can focus the rail and then leave for Quick Access instead of returning from detail to browse. This affects existing Sounds and other self-drawing views too. | Expose the active host's nested-level state and give its Back operation priority before the section-root rail rules. Apply the same order to B, Escape and header Back.                                              |
| A2  | P1       | Live feature visibility does not reconcile the copied rail. `ApplyCefVisibility` changes view-model properties and XAML tile visibility, but the window has no corresponding view-model subscription to refresh rail entries or retire an open disabled tool. Plugins have their own visibility refresh, which does not cover these properties.                                                  | Reconcile eligible routes on config/source changes; retire a removed route and its dialogs, restore a surviving rail entry and reject stale commands.                                                                |
| A3  | P2       | Busy/unavailable action rows can look usable and do nothing. `OverlaySubView.Row` leaves its button enabled when `onClick` is null; some partial views also pass an empty action when busy or already selected.                                                                                                                                                                                  | Make command availability explicit, disable unavailable controls, retain the reason/status and remove placeholder no-op handlers.                                                                                    |
| A4  | P2       | Service refresh can destroy an editor and lose focus. `ServiceSubView.SetContent` rebuilds the subtree and focuses its first control; restoration needs a tag, while `ChoiceRow` creates an untagged ComboBox. A background publication or a successful selection can therefore reset the user's position or close an editor.                                                                    | Reconcile by stable item/control identity; retain editor instances, drafts, dropdown state, scroll and focus. Move focus only when its target is actually removed.                                                   |
| A5  | P2       | An unavailable Steam library can be presented as an empty library. `OverlaySubView.SafeGamesAsync` catches any exception and returns an empty list. The launch-picker path also has an empty-list fallback.                                                                                                                                                                                      | Use an explicit load result for success, empty, unavailable and failed; show Retry and the actual refusal instead of a false "no games" result. Reuse this contract in the new Artwork picker.                       |
| A6  | P2       | Themes' shared browse/detail state has insufficient surface ownership. The Overlay remembers a detail id and can reopen it after another surface changes the service's global detail; its local `_detailOpened` flag cannot identify a later CEF-owned detail.                                                                                                                                   | Scope browse/detail state and cancellation to a view session while keeping installed themes, profiles, writes and busy state under the existing shared service. Use the same ownership model for movies and artwork. |
| A7  | P1       | Overlay theme/movie deletion bypasses the CEF confirmation. `ThemesView.RenderTheme` and `AnimationsView.RenderEntry` call `DeleteAsync` immediately and navigate back, while CEF asks the user to confirm theme/profile/movie removal.                                                                                                                                                          | Match the existing CEF confirmation, identify the exact item and any selected-movie restoration, await the result and retain the failure in context.                                                                 |

The root-menu inventory found only the three missing route parameters described above. Sounds
already has a route parameter and a specific `SoundsHost.Open` rail hook; it is not another missing
entry. However, adding the other parameters alone would still omit their initialization. All
service-backed pages need the same explicit entry lifecycle rather than another one-off hook.

## Navigation and presentation

Add four direct entries to Tools: **CSS Loader**, **Video Switcher**, **Steam Artwork Changer** and
**Library Importer**. Preserve the existing System, Storage, Display, Sounds, Keyboard, About and
conditional Performance/Plugins entries.

Use the existing `SystemThemes`, `SystemAnimations` and `SteamGameLibrary` page identities where
possible. Rehome the importer to the Tools destination in the navigation model, host registry,
destination-memory logic and XAML together. Remove its obsolete Steam-root entry. Add one new
Artwork page identity. Internal CEF routes such as `/wsgm/themes` and `/wsgm/animations` stay
intact.

Replace the rail's reliance on discovering arbitrary XAML buttons with explicit section descriptors:
stable key, title/icon, page, availability, entry action and leave action. XAML actions, rail
selection, remembered selection and programmatic navigation must use that same registration. Do not
retain parallel lists that can omit a feature again. Dynamic device sections continue to project
their SDK descriptors through the same navigation mechanism.

Each tool gets an internal tab strip or equivalent subnavigation and a controls pane. Browse uses
thumbnails/cards, settings use actual editors and details use an image/video pane alongside actions.
Use the existing theme tokens, centered headers and tack actions where whole-section pinning is
already supported. Match CEF capabilities and outcomes while adapting the layout to the Overlay.

Keep existing master CEF visibility policy. A configured tool stays visible while Steam is
temporarily unavailable and explains which operations need a connection. Catalog browsing, local
library management and previews must not require Big Picture's visible window merely to render.
Turning off a feature or removing its source closes its owned transient UI safely. Never change CEF
readiness policy just to make an Overlay entry appear.

## Shared infrastructure and ownership

`ShellSession` remains the sole service owner. Extend `OverlaySources`, source attachment,
`OverlayController` wiring and teardown to supply artwork and any new view sessions. Views project
typed snapshots and call the same backend operations as CEF; they do not construct alternate
installers, import pipelines, provider clients, config stores or CDP transports.

Separate transient browsing context from shared durable state. A view session owns its query,
filter, pagination, selected detail/app, candidate authorization and cancellation generation.
Installed content, selected movie, profiles, imported records and mutations remain in the existing
services. CEF's adapters and Overlay's adapters use these sessions. A committed change publishes to
both surfaces. Closing a preview cancels that preview; closing the Overlay does not silently cancel
a session-owned import or install already in progress.

Use stable app/content ids throughout. On command acceptance capture the intended id, slot and view
generation; never apply a late image or result to the game selected afterwards. Serialize persistent
writes through the existing owners and preserve their uncertainty/refusal behavior.

### File and folder selection

Build one in-window picker usable by controller, keyboard, touch and mouse: drives, directory
breadcrumbs, Up, folder/file rows, extension filtering, loading/access errors, selection and Cancel.
Use `OverlaySubView` navigation and the existing keyboard surface for optional path entry. It
returns a local path to the calling backend and performs no install or write itself. Enumerate off
the UI thread, cancel stale directory loads and preserve selection on refresh. Do not depend on
Explorer or require typing a full path as the primary flow.

Use it for local WebM import, local artwork and importer shortcut folders. Reuse it for Sounds'
existing ZIP-import action, which currently asks the user to type a path. Keep extension, size,
format and destination validation in each existing backend. No new per-file permission prompts.

### Images, animated artwork and video

Add a bounded preview cache with asynchronous loading, decode-size limits, disposal and stable keys.
Decode ordinary thumbnails into Avalonia bitmaps; load only visible cards and nearby items. Theme
screenshots need a detail gallery. Artwork needs thumbnails, full-size detail and animated preview.
Movie playback is explicit in the detail pane, not thousands of autoplaying list items.

Use a narrow Windows WebView2 media viewport for WebM and animated-image detail previews. The
surrounding page, tabs, controls, selection and commands remain Avalonia. The viewport loads
packaged media markup and a host-approved media resource; it does not embed the CEF tool page or
open a separate browser. Avalonia supports native-child hosting through `NativeControlHost`, and
WebView2's controller exposes size, visibility and focus management.
([Avalonia native hosting](https://docs.avaloniaui.net/docs/app-development/native-interop),
[Microsoft controller contract](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/icorewebview2controller))

Keep native preview content outside the focus order. Avalonia owns Play/Pause, seek, volume, replay
and Back; translate these into a closed media-command vocabulary. Disable popups, downloads,
external navigation and arbitrary remote HTML. Use the existing download bounds for image/movie
resources, with cancellable temporary preview files and correctly typed local responses. Never turn
a preview download into an installed movie without the explicit Download action.

Account for native-child airspace: suspend/hide the viewport before an in-window keyboard,
confirmation, picker or other surface covers it; restore it only for the current visible detail.
Update bounds for scrolling, clipping and DPI. Stop playback and release resources on leaving the
tool, changing the selected media, closing the Overlay or shutdown. No idle player or background
sound. Use fakes in headless captures.

Package the x64 WebView2 loader and license notices. Setup detects the Evergreen Runtime and
supplies the verified standalone installer only when needed, so the installed feature also works
offline. Microsoft requires the runtime and documents this offline deployment model.
([Microsoft runtime distribution](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution))
Runtime acquisition belongs in the build/setup owner; feature views must not install it. An
unsupported or failed runtime has a specific visible diagnostic, not a false successful preview.

Prove this integration first with local WebM/Opus, animated WebP, DPI, blur, topmost placement,
controller focus and covered-surface clipping. This is a technical acceptance gate, not a reduced
scope or a handoff-to-Steam fallback.

## CSS Loader parity

Provide Browse, Installed, Profiles and Settings. Preserve all current state fields, notices,
loading/errors, dependency behavior and installed/outdated/local status.

| CEF command or feature                     | Overlay implementation                                                                                                                                                                                                                 |
| ------------------------------------------ | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `setTab`, `browse`, `loadMore`             | Internal tabs; DeckThemes search, target filter/counts, sort, paginated thumbnail grid, empty/error/loading states.                                                                                                                    |
| `open`, `closeDetail`                      | Detail page with every screenshot, description, author/version/target/counts, dependency list and installed dependency status. Owned detail lifetime.                                                                                  |
| `install`, `update`, `updateAll`, `delete` | Install/reinstall/update with dependencies, individual/all updates, busy/progress/refusal states and the same delete confirmation.                                                                                                     |
| `setEnabled`, `setPatch`, `setComponent`   | Real toggles, indexed option sliders, dropdowns, note-only patches, color picker with editable value, image/text value editors and conditional components. Preserve exact backend values and only show the active option's components. |
| `setProfile`, `createProfile`              | None, selected/invalid profile state, named profile creation, selection and deletion/management offered by the CEF Profiles tab. Reflect manual changes that invalidate the selected profile.                                          |
| `refresh`, `setHidden`                     | Reload/check updates and hide/show in Steam Quick Access without hiding the theme from Overlay management.                                                                                                                             |
| `setSetting`, `dismiss`                    | Install themes into Steam toggle; auto/stable/beta class translations; translations count/fetch status, folder/link diagnostics; dismiss notice/error.                                                                                 |

Do not apply CSS Loader CSS to the Overlay itself. Continue using the existing Steam injection,
class translations, config reload, profiles and dependency resolution. Search/detail failures must
not erase installed themes or turn a partially completed installation into a success message.

## Video Switcher parity

Provide Browse, Library and Settings. This is the existing boot-movie feature: it does not add a
SteamOS suspend animation or arbitrary new movie slots.

| CEF command or feature                | Overlay implementation                                                                                                                                                                    |
| ------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `setTab`, `browse`, `more`, `refresh` | Repository search/sort, thumbnails, author/date/likes/downloads, downloaded markers and the existing 48-item page size.                                                                   |
| `open`, `closeDetail`                 | Native detail with thumbnail/video, metadata/description and playback controls for remote listings and local library files.                                                               |
| `download`, `delete`, `addFile`       | Download into the shared library, remove with the CEF confirmation and backend behavior, and import a chosen local `.webm` through the in-window picker. Preserve errors and busy states. |
| `select`, `shuffle`                   | Select by library id, restore Steam's own choice and shuffle immediately. Duplicate movie names remain separate choices.                                                                  |
| `setShuffleOnStart`, `setBootVolume`  | Shuffle on WSGM start and 0–100% boot volume, keeping the existing step and Opus-only behavior. Preview volume is separate transient player state.                                        |
| `dismiss` and Settings diagnostics    | Clear notices/errors; library/override paths, selection, restart-needed state and the same explanation of when the change takes effect.                                                   |

Preserve override ownership, `.wsgm-original` restoration and Steam startup-choice set-aside.
Changing a movie does not restart Steam. Removing the chosen movie and selecting Steam's own must
produce the same restoration and restart notice as CEF.

## Steam Artwork Changer parity

Add `ArtworkView` backed by the shared artwork service. Tools opens a Steam game/shortcut picker
with search; use a known current game as an initial suggestion, not an inferred replacement for an
explicit selection. Reuse `SteamLibraryData.ListGamesAsync` through the typed availability result.
Library importer entry points can open this page for an existing `AppId` and then return to the same
importer entry/filter/scroll position.

| CEF command or feature                   | Overlay implementation                                                                                                                                                                                 |
| ---------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `selectTab`                              | Capsule, Wide Capsule, Hero, Logo, Icon and Manage, respecting configured enabled tabs and default tab.                                                                                                |
| Results and asset details                | Responsive thumbnail grid with size control; full-size/animated detail; provider, dimensions, format, author, style/notes and animated/adult/humor/epilepsy badges. Same candidate ids and validation. |
| `setFilter`, `searchGames`, `selectGame` | Full styles, dimensions and MIME choices; Static/Animated and content flags; provider game search, manual match and Use Steam game reset. Persist through the shared artwork state store.              |
| `loadMore`, `apply`, `applyOfficial`     | Paginated provider results, apply an offered result and choose official Steam assets when offered. Preserve provider-specific readiness/errors.                                                        |
| `applyLocal`, `applyInvisible`, `clear`  | Local image picker with supported formats; invisible artwork where allowed, excluding Icon; slot-level clear and current-custom/default previews in Manage.                                            |
| `saveLogoPosition`, `resetLogoPosition`  | Nine anchors, 5–100% width/height sliders, sample preview, Save/Cancel and Reset. Read an existing valid logo position through the toolkit when opening the editor.                                    |

Keep immediate artwork writes separate from the importer's staged artwork selections. Credentials
stay in existing WSGM Settings. Missing credentials or failed Steam operations get a useful state
and retry action; they do not produce a blank tool. Add `IChangeSource`/view-session projection to
the existing artwork owner rather than a second independent artwork backend.

## Library Importer parity

Provide Sources, Review, entry detail, title artwork and All artwork inside Tools. Maintain the same
source pipeline: Xbox, Epic, GOG, Ubisoft, Battle.net, itch, Amazon, Prism, ATLauncher and shortcut
folders. Add no new launcher adapters or ROM-importer scope.

| CEF command or feature                                          | Overlay implementation                                                                                                                                                                                         |
| --------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `scan`, `cancel`, `setSourceEnabled`                            | Installed/enabled source state and counts, scan progress/errors and cancellation with the existing stop-after-current-item semantics.                                                                          |
| `addFolder`, `removeFolder`, `setCollections`                   | In-window folder selection, include-subfolders switch, editable extension list, remove-folder behavior and one Steam collection per source/folder.                                                             |
| Review UI, `toggleEntry`, `select`                              | All, New, Imported, Needs attention and Left out tabs; source and text filters, action badges, grid/list views, artwork slot choice and selection scoped to the displayed group/query. Removals stay explicit. |
| `setMode`, `cycleLaunch`, `setRoute`, `details`                 | Exact available route choice, packaged input modes, existing acknowledgement, launch/install/identity evidence and multiplayer notes. No silent fallback between controller-only and injection.                |
| `exclude`, `include`                                            | Persist exclusion and re-include across scans with the same labels and selectable/editable rules.                                                                                                              |
| `artworkOptions`, `cycleArtwork`, `pickArtwork`, `clearArtwork` | Candidate thumbnail picker for grid/wide/hero/logo/icon, cycling, selected slot/provider/counts and clear/keep/default/none states. All changes remain staged until Apply.                                     |
| `searchMatch`, `setMatch`                                       | Provider game-match search, choose a match, clear it and regenerate candidate sets with the same persistent per-title choices.                                                                                 |
| `fillArtwork`, `resetArtwork`                                   | All artwork matrix for selected titles; launcher-first or provider-first fill, fill all/only empty, per-slot fill and reset. Keep, pick and empty states remain distinct.                                      |
| `openArtwork`                                                   | Existing imported title opens native Artwork in the Overlay, not a compulsory Steam handoff. CEF keeps its existing page-opening behavior.                                                                     |
| `apply`                                                         | Review/save selected changes, progress and per-entry results; distinguish added/updated/adopted/skipped/removed/conflicted/artwork-only outcomes and uncertain writes.                                         |

Retain Steam's running-client write API, serialization, settle/readback confirmation, owned records
and no-retry rule for an unconfirmed addition. Steam stopped or unreachable refuses Apply. Do not
write `shortcuts.vdf` as an offline fallback. Opening and closing the Overlay must preserve a
running scan/apply and reattach to current progress without starting it again.

## Implementation order and file ownership

1. **Navigation and shared defects.** Introduce route descriptors and entry/leave hooks; rehome the
   importer; add Artwork attachment and Tools entries; correct A1–A5 across affected existing views.
   Main files: `OverlayWindow.Workspace`, `OverlayWindow.Navigation`, `OverlayNavigation`,
   `OverlayWindow.axaml`, `OverlayViewModel`, `OverlaySources`, `OverlayController`,
   `ServiceSubView` and `OverlaySubView`. First deliverable is real populated pages, not menu
   entries alone.
2. **Picker, preview and scoped state.** Implement shared file/folder selection, image cache, media
   viewport, view-session ownership and A6. Extend Shell owners/CEF adapters where required; add the
   runtime payload to setup/build. Prove the codec and native-host lifecycle before expanding
   galleries. Keep normal startup, restore-shell and non-preview Overlay paths free of media
   initialization.
3. **CSS Loader.** Complete `ThemesView` browse/detail/profiles/settings, deletion confirmation (A7)
   and all editors against the command matrix. Preserve CEF functionality during state-session
   extraction.
4. **Video Switcher.** Complete `AnimationsView`, removal confirmation (A7), local import and
   playback/settings using the shared preview infrastructure. Review stock restoration and
   cached-startup messaging.
5. **Artwork.** Implement the complete `ArtworkView`, game picker, filters/details/manage/logo
   editor, typed state subscription and immediate-write results.
6. **Library Importer.** Complete `GameLibraryView` review/source/folder workflows, staged title and
   bulk artwork, exact launch choices and cross-tool return navigation. Reuse the completed Artwork
   page only for immediate editing of already imported games.
7. **Parity and regression closure.** Walk every matrix row and every additional finding; reconcile
   docs/guidance/tracker, compile and render the complete flows, then deliver for manual testing.
   Fix reported gaps before marking a feature complete.

Reusable Steam client operations belong in SteamUiToolkit; Overlay presentation and WSGM policy stay
in WSGM. No toolkit change is needed merely to build an Avalonia page. If client or CEF adapter
contracts change, update their tests and regenerate `NativeQamBootstrap.js` through the asset build.
Commit/push changed children before the WSGM gitlink, directly on their default branches.

## Validation and acceptance

Before the maintainer's manual pass: formatting, guidance/asset drift checks, warning-free Release
compilation and isolated preview renders. Render empty, loaded, busy, error, detail, picker and
confirmation states at the 980×640 floor, 1280×800 and a larger viewport, including DPI scaling.
Existing headless previews omit these services, so extend their fixtures instead of using an empty
Tools image as evidence. Supply media/player and backend fakes; headless capture starts no live
Steam, network transfer, install or library mutation.

Manual acceptance covers each complete workflow with controller, keyboard, mouse and touch;
Back/Escape/header Back; picker cancellation; focus/scroll preservation during publication; no
background preview audio; integration off; config reload while open; Steam loss/reconnect; Overlay
close/reopen during work; and changes made from CEF reflected in Overlay and vice versa. Use
temporary media/content fixtures and maintainer-selected test games/folders for live writes.

After the maintainer reports manual testing, run focused policy/UI tests, update/review affected
baselines and run `eng/verify.ps1` once for the broad implementation. This work changes shared
navigation/editor infrastructure and potentially setup dependencies, so that full gate is justified.
Then use narrow affected checks for follow-ups.

Add focused coverage for route completeness and initialization; nested Back precedence; route
removal on config reload; disabled command state; editor/focus retention; typed unavailable versus
empty library; confirmation and failed-delete retention; independent view-session generations; stale
asset/app selection; every command mapping in the four matrices; staged versus immediate artwork;
codec/player lifetime; and import resume/uncertainty. Existing service tests remain the backend
regression baseline. Execution stays deferred under the repository's manual-first policy.

Completion requires all four Tools entries reachable and populated, every CEF operation and preview
usable in the Overlay, no mandatory handoff to Steam, the existing CEF version still functional,
shared config/state and results consistent, the additional defects closed, and manual and deferred
automated validation recorded accurately. Build a new setup only when the maintainer requests the
implementation handoff; do not bump the release Version or create a GitHub release.
