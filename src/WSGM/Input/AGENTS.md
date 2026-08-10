# Input

Input converts SDL3 controller state and raw-input observation into navigation, chords, and shortcut
recording for WSGM's own UI.

- `SdlGamepads` is the process-wide SDL event-pump owner. Do not introduce another event pump per
  window or service instance.
- Never globally intercept mouse or keyboard input. Raw input is observation-only; the keyboard hook
  is permitted only for an explicit `KeyRecorder` recording lifetime.
- Preserve edge-triggered button events, direction repeat, and the TextBox navigation skip so touch
  keyboard behavior and gamepad focus remain stable.
- Extend the established diagnostic logs (`Gamepad added:`, `Controller input:`, `Gamepad nav:`) for
  every device-dependent change; remote device logs are the controller test harness.
- Peer-window edge callbacks must log the attempted direction before transferring focus.
