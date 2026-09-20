# Steam plugin review repairs

## Scope

Fix the five findings from the worktree review without replacing the requested SteamGridDB
port with an overlay shortcut. Preserve the task branches and existing edits. No commits or
publishing are requested. The maintainer has explicitly waived design approval and authorized
unattended Steam testing and restarts.

## Execution

- Restore the common-plugin Steam projection, including generation-bound action identities,
  lifecycle withdrawal and regression coverage. Restore accidentally removed imports.
- Make the Extensions-tab probe recognize its own durable claim and render controller-native
  actions. Cover repeated installation and removal.
- Capture game-menu components before their first render through the shared element interceptor,
  not through visible DOM in SharedJSContext. Cover first-open insertion and cleanup.
- Port the SteamGridDB artwork screen into CEF using reusable toolkit page and input primitives.
  Preserve the upstream tabbed gallery, filters, details and management flows rather than opening
  the existing Avalonia artwork picker. Track any remaining parity gaps explicitly.
- Regenerate the composed Steam asset, compile warning-clean and exercise the live surfaces.
  Follow the repository's manual-first rule for running test suites; do not run the mutating full
  cleanup gate concurrently with any build or editing work.

## Review focus

Stale plugin IDs after reload, shutdown during action dispatch, a second probe after installation,
controller-only activation, the first game-menu opening, navigating between games during a search,
and removing a surface while it is open must retain truthful state and deterministic cleanup.

## Evidence

The review reproduced the missing-type compiler failure. The initial build needed
`-p:UsedAvaloniaProducts=` to avoid an inaccessible Avalonia telemetry log; this only disables
build telemetry, not compilation or analyzers. No live visual parity pass has been recorded.

The host projection and four bounded repairs are implemented. WSGM and toolkit test projects
compile in Release with zero warnings and errors; regression execution remains deferred under the
manual-first rule. The composed asset passes its drift check.

On the local Steam client, a temporary isolated bridge installed both repaired gates successfully:
the Extensions tab resolved its native focusable component and claimed the QAM memo; the game-menu
gate installed its shared element interceptor. Both removals reported success and unclaimed state.
The temporary bridge was disposed and its property removed; the original bridge's hash remained
unchanged. No artwork was written. This was an installation/removal check, not a visible menu,
controller, first-render or full UI-parity pass.

Independent repair review also found that two valid 128-character labels could exceed the injected
menu's 160-character limit. The projection now preserves the action label and shortens only package
attribution, with regression coverage.

The gear-menu action now opens a toolkit-owned Steam route instead of the Avalonia overlay. The
provider-backed page implements the upstream tab order, controller-focusable result gallery,
details/apply flow and management/reset view, with SteamGridDB and Screenscraper merged behind the
existing provider contract. A live fixture pass installed the page and artwork gates, published the
route and model, navigated to `/wsgm/artwork/480`, and observed 30 Valve routes plus the added route;
late installation initially exposed a mounted-router defect, which is now covered by the page check.

The port is still not 1:1 complete. Upstream filter controls and query parameters, manual game
override/search, pagination, local-file browsing, icon cache/shortcut writes, and interactive logo
positioning remain open. The local deploy script also refused this unregistered `EQS_RTX` board, so
the host-backed build has compile evidence and the toolkit has live fixture evidence, but there is no
installed end-to-end product pass on this machine yet.
