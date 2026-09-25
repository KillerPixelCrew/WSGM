# ASUS ROG Ally family plugin contributor instructions

## Scope and sources of truth

These instructions apply to `src/WSGM.Device.Asus.RogAlly/**`.

This is the MIT-licensed device plugin for the ROG Ally (RC71L), Ally X (RC72LA/RC72L), Xbox Ally
(RC73YA) and Xbox Ally X (RC73XA). It was built blind from Handheld Companion 1.3.1.6 (the decompiled
Patreon build in `_ref/HandheldCompanion`) and HHD (`_ref/hhd`); nothing in it has a recorded hardware
pass. Read `README.md`, `PROVENANCE.md` and the tests before changing behavior.

- HC 1.3.1.6 is the primary Windows reference, including default button behavior, ATKACPI, fans,
  power, Aura, rumble and motion. HHD is a secondary cross-check and supplies behavior HC does not
  implement. A Device Lab observation beats both.
- Every per-model fact lives in `AllyModels.cs`. Correct a model by changing its row, never by adding
  a model check elsewhere.
- Every device fact carries a citation, in `PROVENANCE.md` or beside the value. When a lab report
  changes a value, update the citation and the "must confirm" list in the same change.

## Device boundary

Detection matches baseboard manufacturer `ASUSTeK COMPUTER INC.` and the exact baseboard products in
the model table, and stays side-effect free. `StartAsync` rereads SMBIOS and refuses a changed model.
Every command revalidates identity, service state, generations, deadline and range before any write.

## Mutation invariants

- ATKACPI calls go through `WindowsAsusAcpi`, which admits only the `AsusAcpiId` list and validated
  curves. Do not add INIT, WDOG or arbitrary IDs.
- Power: SPL <= SPPT <= FPPT after every write; mode before limits on restore. Refuse a write unless
  the original limits and mode can be read and journalled first.
- Fans: eight points, 20-110 °C, non-falling duties, clamped to 99. Refuse a write unless every
  present channel's original curve can be read and journalled. Keep an unverified restore in the
  recovery record; do not retry it automatically.
- Charge limit and Aura are persistent user choices: never journalled, never reverted on stop.
- Controller tables are written as 64-byte 0x5A feature reports only while the controller is managed,
  journalled first, and replaced by the factory tables on release. They cannot be read back, so a
  release that wrote them stays reported as unverified.
- An uncertain write is never retried. HHD's timed re-send of the controller tables stays out.

## Input invariants

- Vendor codes follow HC's Windows event semantics: 0x93 is a separate Library control and
  0xA7/0xA8 are M2 press/release. `AllyModels.cs` maps the front controls to each model's physical
  layout; keep the HHD disagreement in PROVENANCE.md.
- The keyboard hook claims only the watched F-keys, never injected input, and stays allocation-light
  with no I/O in the callback. Rear keys are watched only while the controller tables are applied.
- Apply each model's axis maps exactly once, before the gyro-offset correction.

## Validation

CI is software-only. Tests under `tests/WSGM.Device.Asus.RogAlly.Tests` use fakes for every
transport. Any claim that a transport, code or axis works on hardware needs a Device Lab report
(`wsgm-device report <file.wsgmlab>`) and a PROVENANCE.md update.
