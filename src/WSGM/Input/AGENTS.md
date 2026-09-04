# Input

This scope owns process-wide controller observation, action mapping, recording, repeat behavior, and
source switching.

- SdlGamepads has one process-wide event pump. Do not create competing SDL owners or pollers in
  views.
- Preserve deterministic switching between managed-controller and SDL sources. Disconnect,
  cancellation, focus loss, and disposal must release held state.
- Raw input is observation, not interception. The only low-level keyboard hook is KeyRecorder during
  its bounded recording lifetime.
- Keep edge detection and repeat timing explicit. Do not turn a held sample into repeated navigation
  through accidental resubscription.
- Gamepad navigation skips ordinary TextBox focus stops so controller users reach the on-screen
  keyboard path. Log a peer-window edge before transferring focus.
- High-rate paths avoid per-sample allocation, synchronous I/O, and log spam. Preserve the
  diagnostic prefixes Gamepad added:, Controller input:, and Gamepad nav:; log lifecycle changes and
  actionable failures, not every sample.
- Main-app input code is device-neutral. MSI Claw chord suppression belongs in
  external/WSGM.Device.Msi.Claw8A2Vm/src/WSGM.Device.Msi.Claw8A2Vm, including
  FirmwareChordSuppressor.
- Controls report input intent; Shell, Settings, or Overlay owns the resulting policy and lease
  transitions.

Add deterministic tests for edges, repeats, cancellation, source changes, and recording cleanup. Do
not require attached controllers or global hooks in the test suite.
