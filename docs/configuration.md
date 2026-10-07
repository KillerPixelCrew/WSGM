# Configuration, persistence and live reload

This describes the persistence contract from a Settings edit to the resident session and logon
service. Field meanings live beside [`AppConfig`](../src/WSGM/Core/AppConfig.cs) and its nested
configuration types. Profile inheritance is documented in [profiles](profiles.md); this guide covers
how the document is read and changed safely.

## Files and owners

| State                                                          | Owner                                               | Purpose                                                                          |
| -------------------------------------------------------------- | --------------------------------------------------- | -------------------------------------------------------------------------------- |
| `%LOCALAPPDATA%\WSGM\config.json`                              | `ConfigStore` over `UserDataContext`                | User preferences, profile choices and recovery snapshots                         |
| `config.bad.<SHA256>.json` beside it                           | `ConfigStore.PreserveCorrupt`                       | The exact bytes of an unparseable document, once per distinct content hash       |
| `config.v<schema>.json` beside it                              | `ConfigStore.PreserveOlderSchema`                   | The first older-schema document before a current-schema write replaces it        |
| `boot.json` beside it                                          | `BootManifestWriter` and shared `BootManifestStore` | Minimal startup/elevation projection for the logon service                       |
| Plugin state, theme libraries, artwork and other feature files | The respective feature owner                        | Separate data with its own lifetime and format; not arbitrary `AppConfig` fields |

`UserDataContext.ForCurrentUser()` chooses the production directory and the `Local\WSGM.Config`
mutex name. Tests can supply a distinct root and mutex identity. Installed packages and application
binaries use `InstallLayout`, not the per-user configuration directory.

`ConfigJsonContext` supplies source-generated JSON metadata. Config property names retain their C#
names, enum values serialize as strings, and output is indented. This is separate from the Steam
bridge's camelCase wire format. `ConfigJson.Clone` round-trips through that same metadata so an
editor copy has the production persistence shape.

## Reads distinguish absence from failure

[`ConfigReadResult`](../src/WSGM/Core/ConfigReadResult.cs) is deliberately classified:

| Outcome      | Configuration value                                | Consequence                                                               |
| ------------ | -------------------------------------------------- | ------------------------------------------------------------------------- |
| `Loaded`     | Parsed, repaired, normalized and migrated document | May be used by a writer                                                   |
| `Absent`     | New `AppConfig` defaults                           | A first write is allowed                                                  |
| `Corrupt`    | `null`                                             | Preserve the bytes if possible; do not replace the original with defaults |
| `Unreadable` | `null`                                             | Preserve existing state; report the access/lock failure                   |

`Read()` normally takes the same mutex as a writer, with a two-second timeout. Only a mutex timeout
falls back to reading without that lock. Because publication replaces a complete file, this still
reads a whole document, although it need not reflect the completion of a multi-file Settings save.
Other acquisition/read failures return `Unreadable`. Calling `Read()` through the same store while
its current thread owns a transaction returns a failure; use the transaction's `Read` instead.

Startup can use defaults to open the application after a failed read, and Settings explains that
saving is refused until the existing file is repaired or removed. This does not make those defaults
authoritative. `RequireConfig()` accepts only `Loaded` or `Absent`, and the boot projection leaves
the existing manifest untouched after a failed read.

## Repair and migration

[`ConfigRepair`](../src/WSGM/Core/ConfigRepair.cs) first parses an object-shaped JSON document.
Missing or non-integer schema versions read as schema 0, the unversioned 2.0 format. Known enum
fields are repaired using generated metadata and their own default template; an invalid nullable
enum can become null. Wrong structural shapes and oversized unparseable numeric values are not
turned into invented preferences. `AppConfigRules.Normalize` then enforces the application's value
rules and reports diagnostics.

The current schema is `AppConfig.CurrentSchemaVersion` (1). Migration from schema 0 changes an
editor-authored display layout's old rotation value 1 to 0, meaning keep the current rotation. A
pending desktop-return capture keeps its exact rotation. Migration changes the in-memory document;
reading alone does not overwrite `config.json`.

Before the first save of an older document, the store preserves its original bytes as
`config.v<schema>.json`. An existing copy is retained. If a required new copy cannot be written, the
configuration write is refused. A corrupt document is preserved by content hash without deleting
earlier distinct evidence, and the original remains in place.

A document from a newer schema is loaded best effort and marked with the current schema in memory.
The log explicitly says that settings this build does not know are dropped on the next save. This is
not lossless forward compatibility.

## Writer transactions

[`ConfigStore.Transaction`](../src/WSGM/Core/ConfigStore.cs) acquires the mutex and reads the latest
valid document while holding it. Its nested `ConfigTransaction` owns that fresh configuration,
optional old-schema copy and lock until disposal. It is thread-affine: acquire, save and dispose on
the same thread. Do not hold it across `await`, start a nested writer on the same store, or call the
store's `Read()` from inside it.

`Save()` publishes explicitly; disposal alone never saves. A replacement document can be passed to
`Save(replacement)`, which updates the transaction's `Read` reference. The shared `AtomicFile`
writes a unique sibling temporary file, requests write-through and a disk flush for configuration
writes, then moves the complete file over the destination. Cleanup attempts remove the temporary
file and report failures. This makes one file publication atomic; it does not make every related
file one filesystem transaction.

For a small independent preference, `Update(edit)` obtains the same fresh writer scope. It saves
only when the callback reports a change and the serialized document actually changed. Mutate only
the fields the operation owns, so other surfaces' preferences and recovery snapshots survive.

Settings saves have additional work in
[`SettingsViewModel.Save`](../src/WSGM/Settings/SettingsViewModel.Save.cs). They stage potentially
large splash assets before taking the writer lock, merge the user's intended edits into the fresh
document, save, promote the assets, repair saved references if promotion failed, and write the boot
projection within the established scope. Post-save machine/integration work runs outside that lock.
A reported failure may therefore follow a completed configuration write; it is not evidence that no
part of the save happened.

## The service projection

[`BootManifestWriter`](../src/WSGM/Core/BootManifestWriter.cs) writes the shared
[`BootManifest`](../src/Shared/Boot/BootManifest.cs) format. It contains its own schema version,
Game Mode boot and Desktop-resident choices, elevation intent and the installed executable path.
`StartAtSignIn` gates both startup choices; `StartMode` chooses between them. The service need not
load WSGM's full configuration, logging or Avalonia dependencies.

`WriteCurrent` requires a trustworthy read. On failure it logs and returns false; an older manifest
may still request the previous startup behavior, so callers must not report a successfully applied
startup preference merely because `config.json` was saved. `WriteSignInDisabled` is the explicit
recovery path used by restore-shell and the crash-loop breaker even when a normal config save is
unavailable. Service-side validation and token handling are in [boot and shell](boot-and-shell.md).

The Avalonia Settings save surfaces a failed boot projection. The current Steam settings command
path calls `WriteCurrent` without checking its boolean result, so that surface can report the
configuration change while the log records a failed boot-manifest write. Check the manifest/log when
diagnosing a startup preference that did not take effect at sign-in.

## Live reload

[`ShellSession.Config`](../src/WSGM/Shell/ShellSession.Config.cs) owns the `FileSystemWatcher` and
debounce timer for `config.json`. Changed/renamed events advance a generation and schedule one 500
ms debounce. A worker reads the file; only a valid result is posted to the Avalonia dispatcher. Both
before reading and immediately before applying, the session rejects a disposed owner or stale
generation. A failed read keeps the currently running configuration.

`ApplyReloadedConfig` replaces `_config` and invokes existing owners in a defined sequence. It
updates logging, profiles, device/common plugins, performance, the CEF gate and surfaces, Steam
Input management, content features, display mute, chord behavior, accent, mode policy, overlay,
startup apps and keep-awake policy. Each synchronous step is isolated through `TryApply`; one
failure is logged without skipping the remaining owners. Operations that continue asynchronously
retain their own cancellation, serialization and error reporting.

A watcher error logs, schedules a fresh read and re-arms the existing watcher. Shutdown invalidates
the generation, disables the watcher and disposes the debounce timer. Managers must use newly
supplied values rather than retaining nested objects from an old configuration instance.

To extend configuration, document the field where declared, add any needed generated JSON metadata,
normalize its bounds/defaults, define how its owner observes changes, and extend migration only if
an existing persisted meaning changed. Keep transient handles, pending commands and live telemetry
in the owner described by the [architecture guide](architecture.md).
