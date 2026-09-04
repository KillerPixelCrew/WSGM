# WSGM.LogonService

This project is the minimal LocalSystem logon service. Keep it independent of Avalonia, COM
automation, ServiceBase, and the main application's configuration stack.

- Use the raw service-control dispatcher and preserve bounded, truthful service status transitions.
- Read the shared boot manifest as untrusted input. Validate schema, paths, session identity, and
  file existence before launching anything.
- React to WTS_SESSION_LOGON and perform the startup sweep so sessions already present when the
  service starts are not missed.
- Request a linked token only for the WSGM launch that requires it. Explorer recovery uses the
  unlinked interactive token.
- The watchdog waits for the launched WSGM process handle; there is no health handshake. After a
  dirty exit, give the independent shell anchor five seconds to restore Explorer, then perform the
  one-shot Explorer fallback if needed. Never relaunch WSGM.
- Write diagnostics under ProgramData with bounded failure handling. Do not depend on a user profile
  or interactive UI.
- Service install, start, stop, and live session tests are attended operations. Do not run them as
  ordinary validation.

Cover manifest rejection, token selection, session deduplication, watchdog races, and one-shot
Explorer recovery with seams in WSGM.Tests.
