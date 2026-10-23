# GPU-scaled source resolutions

WSGM Settings > Display and Steam's native Display Settings share `ScaledResolutionService`. Choose
a connected display to see its GPU and one resolution list. Enter a custom size as width × height
and choose Add, or choose Add 5% steps to add native 100% down to 50% in one action. Each percentage
applies to both dimensions, rounded to the nearest pixel. Existing resolutions are preserved.
Selecting a list entry does not activate it. Remove deletes the selected driver-list entry,
including entries created outside WSGM; the display's reported native size is protected. Adding only
makes sizes available; the existing Overlay and Steam Quick Access pickers select the active mode
once Windows exposes it.

CEF uses Steam's native inline list-builder, with a width × height input and the list actions beside
it. Settings uses the same list and operations, with its custom dimensions in one text field.

Before the first driver write, WSGM saves the original recognized source sizes in
`ScaledResolutionBackups`. Restore original list brings those sizes back and removes later extras,
keeping the native size. The snapshot survives application restarts and is scoped to the adapter or
exact Intel output. Existing driver extensions and timing records remain under the vendor library's
preservation rules. This restores the list captured before editing, rather than claiming to
reconstruct factory defaults from a list that may already contain custom entries.

Only additions accepted from WSGM are remembered. Entries already present are preserved and are not
claimed by WSGM. Missing remembered entries, such as after a driver update, can be reapplied with
Restore missing resolutions action under Driver options. AMD and NVIDIA additions belong to the
driving adapter and appear when editing another display on that adapter. Intel additions belong to
the exact output identity. Disconnected or changed routes must be refreshed before another
operation. The editor lists the driver's source entries. Windows display modes are available through
the separate active-resolution picker.

LibGPUDriverInteract owns support discovery, fresh list reads, writes and activation outcomes on its
existing serialized driver lane. It resolves the selected CCD adapter LUID through
`DISPLAYCONFIG_ADAPTER_NAME` and SetupAPI to a PnP adapter identity and its driver class key. It
does not choose the rendering GPU. WSGM's `BuiltinGpuService` supplies borrowed resident owners;
standalone Settings owns one `GpuDriver` per enabled installed vendor and joins cleanup through
`ApplicationRuntime`.

- Intel uses IGCL `ctlGetSetCustomMode` version 0, with only `SourceX` and `SourceY`. It reads the
  current output list before an explicit edit, supports the driver's 20-entry limit and reports
  accepted edits as immediately available.
- AMD reads all `DALNonStandardModesBCD` numbered partitions. It adds plain source-size records
  without repartitioning existing data and removes only matching plain records. Restricted lists,
  refresh/flag records, unknown values and malformed trailing bytes remain intact. Four-digit BCD
  dimensions are limited to 9999; a driver-restricted size is refused rather than overriding policy.
- NVIDIA reads and edits the `NV_Modes` `REG_MULTI_SZ` source-size groups. Existing qualifiers and
  unknown driver extensions are preserved. Unrecognized registry kinds, including `REG_SZ`, are
  reported unavailable before any write.

AMD and NVIDIA registry changes need a graphics-driver restart. Both editors explain this and expose
an explicit restart action under Driver options. It invokes Windows PnPUtil for the still-matching
adapter instance only, without `/reboot`. Displays on that adapter may blank and games may lose
their device. Windows can refuse the request or require an OS restart; WSGM reports that outcome. It
never restarts a driver merely because a source size was added. These registry operations generally
require administrator permissions. An uncertain write or restart is reported without automatic retry
or rollback.

No path writes EDID overrides, custom refresh rates, signal timings or an active display mode. The
historical Intel experiment in issue #233 describes that driver/panel combination only. The new
paths still require build, UI rendering and attended vendor hardware acceptance; source fixtures are
not runtime proof.

See [the vendor mechanism and ABI references](../external/libgpu-driver-interact/docs/displays.md).
