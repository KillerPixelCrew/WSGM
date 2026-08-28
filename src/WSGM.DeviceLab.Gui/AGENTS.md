# WSGM.DeviceLab.Gui

The Device Lab desktop surface. This project is a thin Avalonia client over
`WSGM.DeviceLab.Core`; workflows, validation, evidence policy, redaction, package generation, and
hardware-safety decisions belong in Core so the GUI and `wsgm-device` CLI cannot drift.

- Expose every safe Core workflow with the same inputs and result model as the CLI. UI code may
  select files/directories, show progress, cancel, and render results; it must not reimplement a
  workflow.
- Read-only remains the default. Never open WMI methods, HID handles, device transports, production
  DeviceHost IPC, or plugin lifecycle directly from this project.
- The only mutation entry is Core's reviewed interactive trial workflow. Keep its local-console,
  exact-device, hash, generation, lease, and emergency-plan requirements visible and intact; never
  add a bulk-run, remembered consent, unattended, or CI path.
- Require explicit input and output paths. Never read or write the live `%LOCALAPPDATA%\WSGM`
  directory, the repository root, a broad home directory, or an implicit current-directory output.
- Keep long work cancellable and off the UI thread. Marshal only immutable progress/results back to
  Avalonia, prevent duplicate submission while a workflow is active, and surface bounded failures
  without losing the last successful result.
- Treat imported captures, manifests, evidence, recipes, and packages as untrusted data and never as
  authority. Preserve Core's redaction report and explicit export preview in the UI.
