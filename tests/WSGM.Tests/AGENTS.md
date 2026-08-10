# WSGM.Tests

Tests cover pure logic, state transitions, source-generated configuration serialization, and isolated
registry/config round trips.

- Never touch `%LOCALAPPDATA%\WSGM`, shell takeover, boot mode, Explorer, Steam protocol, UAC, device
  input, or lock-screen flows. Use temporary directories and the available test seams.
- Test names are the executable specification; prefer focused behavior tests around device-only
  boundaries instead of unattended automation of those flows.
- `Log` deliberately remains uninitialized in tests. Keep it a no-op rather than adding global test
  logging setup.
