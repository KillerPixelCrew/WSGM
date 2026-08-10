# Core

Core contains cross-cutting, non-visual application primitives: configuration, Steam protocol/CEF,
Explorer control, elevation/de-elevation, splash assets, process mode selection, logging, and Win32
utilities.

- Keep public production APIs XML-documented and NativeAOT-safe; prefer source generation over
  reflection and `LibraryImport` with blittable signatures for native calls.
- `ConfigStore` owns the cross-process lock and atomic merge/save flow. Do not bypass it or write the
  real `%LOCALAPPDATA%\WSGM` configuration from tests.
- `ExplorerControl.ExitExplorerAndWait` is device-settled: use Explorer's exit command and fail open;
  never replace it with `Process.Kill` or Restart Manager shutdown.
- Steam interactions use protocol URLs or the CEF front-end bridge. Never call Steam internals from
  the injected gate; JS values embedded in CEF expressions must be JSON-encoded.
- Keep recovery paths (`--restore-shell`, legacy shell migration, de-elevation) usable before normal
  logging or Avalonia initialization.
