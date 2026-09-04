# WSGM.Launch

This project is a console launcher for de-elevation and process-tree input leases. Keep it small,
dependency-light, and usable before the desktop application starts.

- Preserve OutputType Exe so diagnostics and exit codes remain observable.
- Diagnostic modes such as --status, --rescan, and --help do not require a target. A non-diagnostic
  launch requires at least one behavior flag and a target after --.
- --input-lease and --input-lease-inject are mutually exclusive. Preserve target argument
  boundaries, quoting, working directory, and environment.
- Job containment covers the complete launched process tree. Cancellation, launcher failure, and
  normal exit must restore the lease exactly once.
- Use the canonical controller bindings supplied by the pinned dependency; do not maintain a
  divergent local copy. SteamInterop sources are linked from
  native/SteamInput/bindings/SteamInterop.Net.
- The scheduled-task fallback uses UTF-16 XML, InteractiveToken, and the established principal. Do
  not add /NoUACCheck or weaken task identity checks.
- Named-pipe access grants the intended user SID explicitly. Do not substitute CurrentUserOnly when
  the elevated and interactive endpoints need to meet.
- Input-lease setup fails open to launching the target after recording the failure. De-elevation
  failure launches at the current integrity only with the exact fail-open marker and when no linked
  limited token was found; that case includes standard users, the built-in Administrator, and an
  unqueryable parent token, not only disabled UAC. Otherwise return a clear failure instead of
  launching at the wrong integrity level.
- --input-lease connects only to the resident shim and never enables injection. --input-lease-inject
  is the explicit injection route used when shim management is off.
- For an elevated launch, acquire the input lease in the parent before handing the target to the
  medium-integrity child. The child prepends `__COMPAT_LAYER=RunAsInvoker` so admin-manifest and
  RUNASADMIN targets do not fail with error 740 after de-elevation.
- Clean up tasks, pipes, handles, jobs, and temporary files on every exit path.

Test parsing, quoting, token decisions, task XML, pipe ACLs, job ownership, and one-time restoration
without launching real games or changing live input state.
