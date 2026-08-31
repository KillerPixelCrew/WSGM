# plugins

Device plugin packages. Each directory here is a separate deployable artifact with a minimal
manifest, hardware implementation, static glyph data, and required third-party notices — that is why
plugins live at the repository top level rather than under `src\`.

**No production project may add a `ProjectReference` to a plugin.** WSGM loads the sole installed
plugin at runtime from its package directory. The sole compile-time exception is the consolidated
`tests\WSGM.Tests` owner, which references a plugin only for fake-hardware regression tests.
The dependency-direction test in `tests\WSGM.Tests` fails the build if any other reference appears.

## What a plugin owns

Every hardware transport, protocol, layout, and command sequence for its device; identity and
firmware detection; power, fan, lighting, charge, and telemetry implementation; physical controller
acquisition, normalization, mode changes, and re-enumeration; rumble and output encoding; OEM button
sources and device-specific suppression; validation, ordering, readback, rollback, and its recovery
journal; its own controller artwork and glyph control map; and its declared dependencies.

## What a plugin never does

- Call VIIPER, own WSGM's Steam Input lease, or touch HidHide.
- Supply XAML, HTML, CSS, JavaScript, URLs, Steam selectors, patch logic, or arbitrary commands.
  Artwork and the control map are **data**: WSGM checks their integrity and ships the author's own
  bytes. It does not re-emit them, and the boundary is about who owns the Steam surface, not about
  containing a plugin — a plugin already runs as the user.
- Install, repair, register, or restart a dependency at runtime. Declare it; a missing dependency
  makes the affected capability unavailable, and nothing more.
- Perform UI work, or dispatch an action from a hook callback.
- Write to hardware when the exact prerequisite read failed, or select a firmware address by
  numerical proximity to a known one.

## Rules that apply to every package

- **Fail closed on identity.** An unknown board, unknown firmware descriptor, or failed prerequisite
  read degrades the capability. It never selects the nearest known layout.
- Revalidate identity, firmware, ownership, range, relationship, and current state on **every**
  hardware command. A value the package advertised earlier is not a permission slip.
- One serializer, cancellation, bounded retries, and circuit breaking per transport. No unbounded
  `Sleep` — every wait observes an ACK, a PnP event, a WMI event, or a deadline.
- Snapshot before you change, journal atomically around every ownership-changing transaction, and
  never substitute a hard-coded "factory" value for a snapshot that failed to read.
- Ownership is per resource. A controller conflict must not disable fan, lighting, power, charge, or
  OEM-event capabilities.
- Detect conflicting OEM software and report it; never terminate or reconfigure it. Process presence
  alone is not ownership — only demonstrated competing writes or exclusive-access failure is.
- Keep required third-party notices and record the upstream revision for copied or adapted assets.
  Hardware constants and device findings belong beside the implementation or in the device plan;
  do not build evidence ledgers or per-constant provenance machinery.
