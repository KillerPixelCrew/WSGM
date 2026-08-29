# WSGM.DeviceHost

The sidecar process loads the one installed device plugin package and speaks the semantic protocol
back to WSGM. One host, one package, and one lifecycle live for the WSGM run.

- **JIT by design.** This process exists precisely so a plugin can use `System.Management`/WMI, WinRT
  sensors, and an interactive keyboard hook — none of which the AOT executable may carry. Do not
  apply NativeAOT constraints here, and do not let any assembly from this tree be published beside
  `WSGM.exe` (`eng\check-aot-isolation.ps1` fails the build if one is).
- The installed plugin is explicit administrator-installed hardware code and inherits WSGM's
  required authority. There are no runtime trust tiers or de-elevated package classes. Never add a
  generic broker or load plugin code from a user-writable discovery directory.
- Process separation buys crash and dependency isolation. It is **not** a malware sandbox, and
  comments and diagnostics must not describe it as one: a medium-integrity plugin still has the
  rights of the user account.
- Expose no generic execute, shell, file, WMI, HID, EC, or IOCTL operation over IPC. If a plugin
  needs something, it needs a semantic capability, not a passthrough.
- The host receives no WSGM secrets and no unrelated device handles, uses deterministic DLL search
  paths, and runs under a kill-on-close job so a forced WSGM exit cannot leave it orphaned.
- An unexpected exit is a **fault inside the running device cycle**, never a clean deactivation or a
  handoff to an external manager. Retry once or twice, then fault Device Integration for the run and
  fail open to usable input with an explicit manual retry.
- Device Lab never starts a host to inspect a running plugin; it asks the owner for a bounded,
  read-only diagnostic session. Do not add a "just for diagnostics" activation path.
