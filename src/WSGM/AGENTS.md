# Main application

This scope is the tracked CoreCLR desktop application. It owns process startup, Avalonia
composition, resources, recovery one-shots, and application-wide lifetime. Use the nearer subsystem
guide for detailed rules.

- Keep shell-anchor, restore-shell, and unregister-shell ahead of logging, configuration, Avalonia,
  and GPU initialization. Other one-shot modes retain their deliberate position after logging, or
  initialize logging within their own maintenance path. In particular, restore-shell must work when
  the normal application cannot start.
- Preserve the explicit mode precedence in Program.Main. Do not make shell or boot behavior an
  accidental consequence of UI startup.
- Runtime configuration reload replaces the AppConfig instance. Keep transient state in its owning
  manager rather than retaining references into an old config object.
- Application services have one clear owner and deterministic disposal. Background work must observe
  cancellation and marshal UI state through the dispatcher.
- Keep policy in Core, Shell, Settings, or Overlay rather than App.axaml.cs. Keep native
  declarations in Interop and presentation-only primitives in Controls and Themes.
- Reference SDK and reusable libraries as projects or pinned submodules. Do not copy their source
  into this project.
- SkipNativeArtifacts is a compile-only escape hatch. It does not prove a package or installer is
  complete.
- Route user-facing behavior documentation through docs/README.md and add focused regression tests
  under tests/WSGM.Tests.

Validate a narrow change with a filtered test. Follow the root validation policy for full gate runs.
