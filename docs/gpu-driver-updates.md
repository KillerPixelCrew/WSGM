# GPU driver updates

Device > GPU in the Overlay and Settings > Display > GPU driver updates in Steam share the session's
`GpuDriverUpdateService`. Both show installed version/date, check/availability/failure state,
release notes, download progress, installer outcome, required restart and rollback guidance. Both
pages put **Include non-WHQL (beta) drivers** beside the automatic-check preference. It defaults to
off, persists in AppConfig, and changing it starts a fresh catalog check even when automatic checks
are disabled. A change during another operation queues that check and suppresses results from the
previous channel. Every offered package identifies its WHQL certification; enabling beta never
relaxes the official-source, checksum, signer or explicit-confirmation requirements.

Checking can be enabled daily. The last attempt is persisted, so a restart does not repeat a check
made within 24 hours; startup checks wait for the initial session settling delay. Adapter queries
run concurrently. Downloading/installing always follows an explicit confirmation bound to the
adapter, version, official URL, size and checksum that were shown, and a changed package is refused.
Leaving either surface does not cancel the operation. Cancel stops checking/download/preparation,
and is removed once installation starts.

Installation requires an elevated WSGM, AC power, at least 30% battery when one is present, no
running game and available game detection. The UI reuses a short-lived admission snapshot and the
process's elevation fact; admission and adapter identity are freshly checked again after
download/extraction. The affected vendor's built-in control owner is retired before dispatch and
recreated afterward, so native driver handles do not survive across the replacement. Other vendors
and independent shell recovery remain available. Successful native reattachment raises the event
that invalidates dependent custom-resolution state.

The service owns the phase enum, stage text, cancellation and progress availability. Download
progress is capped at four updates per second and updates the Overlay's existing progress element in
place. Progress has its own signal and does not queue a whole Steam settings publication; Steam's
normal periodic projection samples the current driver rows. Structural state changes still publish
immediately. Discovery and catalog operations share one admission lane, and a failed rescan clears
the former package offer.

Before dispatch, configuration records the adapter and previous/new versions with a pending result.
A crash leaves that pending record visible as unconfirmed; it never triggers another install.
Windows is never rebooted automatically. Rollback guidance uses Device Manager's Roll Back Driver or
reinstalling the previous OEM/vendor package, without promising Windows retained a rollback copy.

LibGPUDriverInteract owns discovery, official catalog selection, download/verification, extraction,
silent commands and vendor result codes. See its
[vendor source/integrity contract](../external/libgpu-driver-interact/docs/driver-updates.md).
LibHandheld owns nullable, explicitly vendor-scoped OEM facts on exact models. AMD handheld facts
cite AMD's published generic-driver exclusion; Intel models and the Steam Machine do not inherit an
AMD-worded default. The Overlay and Steam apply the fact's own PCI applicability and give the OEM
source. The updater reuses the session's detected definition instead of detecting hardware again.
When no definition is known, including before Device Integration is enabled, it does not guess that
an integrated AMD GPU is a desktop product or substitute another APU's name: only an unambiguous
exact official selector match can yield a package. Otherwise the source declines and directs the
user to their OEM. Ordinary AMD desktop adapters remain eligible through that exact selector match.
Intel and NVIDIA checks remain independent of Device Integration.

SteamUiToolkit owns native update notices through its notification store, native template and shared
JSX claim. Discovery uses source fingerprints and export shape checks. A notice names and opens
Steam's Display settings, where the shared updater lives. Delivery is deduplicated and never retried
after uncertainty. If the notifier cannot be used, WSGM retains a notice in the update service for
its Overlay warning bar directing the user to Device > GPU. The Overlay consumes that notice once;
the session keeps no second copy.
