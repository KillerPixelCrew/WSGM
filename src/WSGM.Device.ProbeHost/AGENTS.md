# WSGM.Device.ProbeHost

A disposable process that runs exactly one reviewed compatibility probe and exits.

- **Never activate a plugin's normal lifecycle to test compatibility.** Activation may contain
  writes. A probe reaches only the candidate module's dedicated probe entry point.
- Probe interfaces are typed and profile-scoped, and they exist only in this tree. Nothing here may
  be shared with, referenced by, or reachable through production `WSGM.DeviceHost` IPC — the two are
  separate assemblies precisely so that cannot happen by accident.
- Only WSGM-reviewed, locally installed, hash-pinned probe code runs automatically, and only when it
  matches the exact family and endpoint. Signed-external, sideloaded, and developer probes require an
  explicit Developer Mode action even when their metadata claims read-only.
- **A getter is not safe merely because it reads.** Every probe stays rate-limited, deadline-bounded,
  and scoped to its profile.
- Validate the response, do not accept it: type, length, status, range, timing, repetitions, and an
  independent cross-check. A nonempty reply proves nothing.
- The process is expected to be killed. Hold no state that matters across its lifetime and leave
  nothing behind that a crash would strand.
