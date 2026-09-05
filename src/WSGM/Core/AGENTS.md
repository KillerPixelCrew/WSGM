# Core

Core owns nonvisual product policy, configuration, integration lifetimes, package management, Steam
coordination, and recovery primitives. Native ABI declarations remain in Interop; UI presentation
remains in its feature scope.

## State and lifetime

- ConfigStore is the authority for per-user configuration. Preserve strict validation, atomic
  replacement, serialized mutations, and fresh-state read/modify/write behavior.
- A mutation uses the strict load path. If an existing configuration is unreadable, abort instead of
  writing defaults over its recovery snapshots.
- Recovery operations must be idempotent and usable before normal application initialization.
- ExplorerControl exits Explorer through its orderly exit command and fails open. Do not replace
  that path with Process.Kill or Restart Manager shutdown.
- One owner creates and disposes each long-lived integration. Do not let views acquire hardware,
  Steam, RTSS, or input resources.
- There is one installed device package slot. With integration disabled, skip plugin lifecycle,
  controller targeting, hardware writes, and AutoTDP.
- Capability writes are serialized. If the outcome is uncertain, surface it; do not automatically
  retry a potentially successful write.
- AutoTDP decisions are frametime-driven. CPU or GPU utilization may explain telemetry but must not
  become the control signal or create a persistent power floor.

## Steam boundaries

- SharedJSContext is the Steam execution surface for CEF modules and stores;
  Shell/SteamUiSessionHost owns the bridge, patch manager, modules, runtime, and their lifetime. Use
  the toolkit's module resolver for literal lookups or unique source fingerprints, and JSON-encode
  values across the JavaScript boundary. Features must not implement registry scans or raw require.
- Keep CEF debugger sockets loopback-only. Artwork queries request static assets, require HTTPS,
  infer supported formats from the URL suffix, and cap downloads at 16 MiB; those checks do not
  validate MIME headers or decoded PNG/JPEG content. An unreachable client remains distinct from a
  protocol or JavaScript failure.
- SteamInputBlocker balances named owner claims even when native acquisition fails. A settings
  handoff may register a claim synchronously, but cold acquisition stays off the UI thread.
- WSGM owns product policy and overlay coordination; steam-ui-toolkit owns reusable Steam discovery
  and hook mechanics. Fix behavior in the correct repository.
- The Steam Input shim is owned only when the deployed bytes contain the WSGM proxy signature. A
  sidecar file is never proof of ownership.
- Do not overwrite or move over a mapped Steam DLL. Replace a stale shim only during a proven Steam
  cold start; otherwise record UpdatePending and reconcile later.
- Reconcile shim state outside the ConfigStore lock and preserve the explicit elevation fallback.
  The shim is a WSGM runtime payload in Steam's directory, not permission to write arbitrary Steam
  files.
- HDR is advanced-color state on the DisplayConfig target, not its GDI source. Recheck support
  before showing or applying a saved HDR value.

Cover state transitions, failure paths, cancellation, and repeated cleanup with focused tests. Never
use a live Steam session, installed plugin, or user config as test state.
