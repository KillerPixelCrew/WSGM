# catalog

The known-implementation catalog: reviewed transport, protocol, layout, policy, and capability
modules, their compatibility probes, and the bounded mutation trials. Data and reviewed probe/trial
sources — no runtime WSGM component reads this directory. Normal device detection does not consult
it; only Device Lab does.

- **Identity similarity nominates a candidate. It never authorizes a protocol or a write.** Keep
  reuse rank, evidence grade, and write eligibility as three independent values.
- Reusing a transport or protocol must not import another model's ranges, offsets, tables,
  persistence assumptions, firmware policy, or recovery behavior. Each catalog entry states
  explicitly which values must **not** be inherited.
- Hard constraints belong in the entry, not in the scorer: exact firmware versions, report lengths,
  descriptor hashes, required WMI methods, CPU family, and endpoint roles reject a module outright.
- Every entry records its provenance and license class. A protocol fact learned from another project
  is free to use; its code and structured register tables are not.

## Trials (`catalog/trials/`)

A mutation trial is a **repository artifact, not installable content** (`P0-007`). There is no
mechanism for a user or third party to add one to a shipped Device Lab, and there must never be one.

- A trial is added by source change and review against the `P2.7` checklist. Its hash is computed at
  build time and pinned in the catalog manifest; `probe run` refuses any mismatch.
- One trial acquires one resource and exercises one capability. Combining power, fan, rumble, RGB, or
  controller mode in a single trial is refused in review.
- Every trial declares exact identity and firmware gates, maximum writes, rate, retries, timeout,
  cooldown, an **independent** observation or readback, rollback, and an emergency action — and
  verifies restoration.
- Permanently out of scope, until a separately reviewed pathway exists: EEPROM/ROM/UEFI writes,
  firmware flashing, provider or registry repair, driver install/restart, charge persistence, blind
  bus scans, unknown IOCTL/HID/ACPI/MMIO/MSR/raw-port access, physical memory, test certificates, and
  test-signing.
