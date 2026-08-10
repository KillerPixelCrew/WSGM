# WSGM application

This is the NativeAOT Avalonia executable. It owns Settings, the game-mode shell session, the
quick-access/taskbar surfaces, and the per-user configuration and boot manifest.

- Keep managed COM interop disabled. Cross a flat, blittable `LibraryImport` ABI to native helpers.
- Treat `Core\`, `Shell\`, `Overlay\`, `Input\`, `Settings\`, and `Interop\` as separate ownership
  boundaries; put new code in the narrowest applicable module.
- `--settings` and `--overlay-test` are the only safe local UI modes. Never run `--shell` or `--boot`
  on a development machine.
- Runtime config reload replaces the config object. Keep transient state in its controller/manager,
  not in `AppConfig` references.
- Update this file when this executable's responsibilities or safety boundaries change.
- `SkipNativeArtifacts=true` is a compile/test-only escape hatch for non-Windows CI diagnostics;
  release and supported verification builds must never set it.
