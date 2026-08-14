# Core

Core contains cross-cutting, non-visual application primitives: configuration, Steam protocol/CEF,
Explorer control, elevation/de-elevation, splash assets, process mode selection, logging, and Win32
utilities.

- Keep public production APIs XML-documented and NativeAOT-safe; prefer source generation over
  reflection and `LibraryImport` with blittable signatures for native calls.
- `ConfigStore` owns the cross-process lock and atomic merge/save flow. Do not bypass it or write the
  real `%LOCALAPPDATA%\WSGM` configuration from tests.
- Read-modify-write operations must use the strict mutation load: an existing unreadable config is
  an aborted mutation, never permission to replace recovery snapshots with defaults.
- `ExplorerControl.ExitExplorerAndWait` is device-settled: use Explorer's exit command and fail open;
  never replace it with `Process.Kill` or Restart Manager shutdown.
- Steam interactions use protocol URLs or the CEF front-end bridge. Never call Steam internals from
  the injected gate; JS values embedded in CEF expressions must be JSON-encoded.
- CEF debugger sockets must remain loopback-only; artwork downloads accept bounded static PNG/JPEG
  data over HTTPS, and protocol/JavaScript errors remain distinct from an unreachable Steam client.
- Keep recovery paths (`--restore-shell`, legacy shell migration, de-elevation) usable before normal
  logging or Avalonia initialization.
- Display HDR is DisplayConfig advanced color on a target, not on its GDI source: query current
  support before showing or applying a saved HDR flag, and keep the interop packets blittable.
