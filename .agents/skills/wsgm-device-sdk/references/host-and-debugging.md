# Handheld host integration and diagnosis

WSGM owns a selected LibHandheld instance, its profiles, virtual target, input leases and both UI
surfaces. Common plugins use `WSGM.Plugin.Sdk` and their installed package host; they are
independent of handheld integration. Built-in GPU control uses LibGPUDriverInteract independently
too.

Use one captured `LibHandheld.Contracts.DeviceIdentitySnapshot` for pure exact detection and
creation. The selected definition carries actual roles, controller availability, hardware-access
requirements and hardware-verification evidence. Unsupported hardware remains passive. Diagnostics
must name the exact identity and detection reason, selected definition, version and state path.

Turn integration off by releasing controller ownership and stopping the handheld lifetime. Dispose
only frees handles. Bounded restoration may remain incomplete: preserve its journal and report the
reason. Keep one command serialization owner; do not add duplicate generations, admission gates,
semaphores, trace statics or process-global recovery graphs around the library.

Follow one observable command from user intent through validation, dispatch and typed outcome to
Steam and the overlay. A cancelled wait has not touched hardware and is rejected. A failed submitted
write is uncertain and is never automatically retried. Readback does not gate support or writes.
Glyph loading failure drops glyphs and records the failure; it does not tear down a sound cycle.

For diagnosis, begin with current `wsgm.log`, the matching family source and provenance, and current
Device Lab evidence. Remote testers are ordinary users: do not ask them to run probes. Hardware or
attended CEF actions require the maintainer's explicit scope and their respective preflight.
