# GPU driver updates

Device > GPU in the Overlay and Settings > Display in Steam share the session's
`GpuDriverUpdateService`. Both show installed version/date, check/availability/failure state,
release notes, download progress, installer outcome, required restart and rollback guidance.
Checking can be enabled daily; downloading/installing always follows an explicit confirmation.
Leaving either surface does not cancel the operation. Cancel stops checking/download/preparation,
and is removed once installation starts.

Installation requires an elevated WSGM, AC power, at least 30% battery when one is present, no
running game and available game detection. Admission and adapter identity are checked again after
download/extraction. The affected vendor's built-in control owner is retired before dispatch and
recreated afterward, so native driver handles do not survive across the replacement. Other vendors
and independent shell recovery remain available. Cached custom-resolution state is invalidated.

Before dispatch, configuration records the adapter and previous/new versions with a pending result.
A crash leaves that pending record visible as unconfirmed; it never triggers another install.
Windows is never rebooted automatically. Rollback guidance uses Device Manager's Roll Back Driver or
reinstalling the previous OEM/vendor package, without promising Windows retained a rollback copy.

LibGPUDriverInteract owns discovery, official catalog selection, download/verification, extraction,
silent commands and vendor result codes. See its
[vendor source/integrity contract](../external/libgpu-driver-interact/docs/driver-updates.md).
LibHandheld owns the OEM restriction. AMD's generic packages exclude handheld devices; the Overlay
and Steam explain that restriction and give the manufacturer's source instead of sideloading an
unsupported package. Intel and NVIDIA checks remain independent of Device Integration.

SteamUiToolkit owns native update notices through its notification store, native template and shared
JSX claim. Stable/Beta literal module IDs are admitted only after source fingerprints and export
shape checks. A notice opens Steam's Display settings, where the shared updater lives. Delivery is
deduplicated and never retried after uncertainty. If the notifier cannot be used, WSGM retains a
notice in its Overlay warning bar directing the user to Device > GPU.
