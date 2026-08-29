# Controller dependency audit

This directory pins the primary-source inputs reviewed for WSGM controller management. It contains
metadata and notices only. No driver, installer, SDK assembly, or third-party executable is checked
into the repository or copied into an application publish directory.

## Current release decision

Controller management is **not approved yet**. `HidMaestroProductionBackend` implements that
capability-specific failure and never loads HIDMaestro, launches a helper, installs a driver, or
creates a virtual target, so Device Integration, SDL input, and the Steam Input lease stay usable.

### What the rear-control gate actually is, and how it closes

**Source-reviewed 2026-08-29 against the pinned v1.7.0 tree, checked out at `_ref/HIDMaestro`.** The
original gate said the `steam-deck-composite` profile "does not encode the four distinct rear
controls or stick-touch fields". That is accurate but was too pessimistic about what it implies: the
two halves have different answers, and the rear-control half is WSGM's to fix.

The SDK's own canonical state is not the problem. `HMButton` already carries four rear controls —
`LeftPaddle`/`RightPaddle` (upper) and `LeftPaddle2`/`RightPaddle2` (lower). What is short is the
**profile**, which is plain JSON: its `extendedReport` button mask names 64 bit positions and leaves
the two upper paddles as unnamed `_` slots, so `SubmitState` has nowhere to put them.

The missing positions are known. `hhd`'s virtual Steam Deck (`_ref/hhd`,
`src/hhd/controller/virtual/sd/const.py`) is the same implementation HIDMaestro's profile cites for
its attribute values, and it maps all four. Converting its `BM((byte << 3) + bitFromMsb)` form into
the profile's 64-bit little-endian numbering, counting from the mask's base at byte 8:

| Control | `hhd` name | Byte, bit-from-MSB | Mask bit | In HIDMaestro v1.7.0 |
| --- | --- | --- | --- | --- |
| L5 (lower left) | `extra_l2` | 9, 0 | 15 | named `LeftPaddle` |
| R5 (lower right) | `extra_r2` | 10, 7 | 16 | named `RightPaddle` |
| L4 (upper left) | `extra_l1` | 13, 6 | **41** | unnamed `_` |
| R4 (upper right) | `extra_r1` | 13, 5 | **42** | unnamed `_` |

`hhd` treats `extra_l1`/`extra_r1` as the top pair, which its own noob-mode and
`paddles_to_clicks == "top"` handling confirm. The bit arithmetic is cross-checked against three
positions HIDMaestro and `hhd` already agree on: `share`/`Misc1` at 50, `rs`/`RightStick` at 26, and
`ls`/`LeftStick` at 22.

So the rear-control gate closes without any upstream change: WSGM ships its own profile naming all
four, loaded through `LoadProfilesFromDirectory`. WSGM does not need to fork HIDMaestro, and must
not — a profile is data, and shipping data is not shipping a driver.

**Stick touch is a separate matter and does not close this way.** `HMGamepadState` has no capacitive
stick-touch field and `HMButton` has no bit for it, so the SDK cannot express it at all; `hhd` does
not emulate it either, so there is no sourced bit position to name. Whatever WSGM declares for the
Steam Deck target must therefore say so truthfully rather than let `VirtualTargetProfile.Consume`
silently drop a control a plugin published. This is not a blocker for the Claw, which has no
capacitive sticks, but it is a real limit of the target and belongs in its declared capabilities.

### The backend is VIIPER, not HIDMaestro

**Decided 2026-08-29.** VIIPER (`_ref/VIIPER`, corando98's `viiper-controller` branch) creates
virtual USB devices in userspace over USBIP, and it wins on both halves of the gate above.

- **Nothing is missing.** Its `device/steamdeck` carries the whole Neptune frame natively, including
  all four rear controls and capacitive stick touch. The bit map is settled by three independent
  implementations that agree exactly — VIIPER's `device/steamdeck/const.go`, HandheldCompanion's
  `SteamDeckTarget`, and `hhd`'s virtual Deck: L5 at bit 15, R5 at 16, L4 at 41, R4 at 42, pad touch
  at 19/20, and **stick touch at 46 and 47**. No profile authoring and no upstream extension is
  needed to satisfy WSGM's controller contract.
- **The driver problem disappears.** VIIPER rides `usbip-win2`'s signed kernel driver — the exact
  component already pinned and signature-verified in `controller-components.lock.json`, publisher
  thumbprint `9AC56B6C…`. There is no locally built driver, no self-signed certificate to trust, and
  no INF date stamping, so the reproducibility gate that blocked HIDMaestro does not arise. WSGM
  still installs it only through the installer, as an explicit user-approved elevated step, because
  INV-020 forbids the runtime from installing a driver whatever its provenance.

HIDMaestro stays reviewed and pinned as the alternative, and its analysis above stays accurate. It
is not the chosen path.

### The one real cost, and where it comes from

VIIPER driving a virtual Steam Deck in HandheldCompanion measured a **constant 6–8% CPU**. On a
handheld that is a battery cost, not a rounding error, and it was the original reason to prefer
HIDMaestro. It has to be fixed rather than accepted, and the mechanism is now identified rather than
guessed.

VIIPER completes interrupt-IN transfers one of two ways
(`internal/server/usb/server.go`, `startInWorker`). Devices that declare `NaksWhenIdle()` block on
their input gate and go quiet when nothing changes. Everything else takes the keepalive path: a
per-attempt deadline of one `bInterval`, and on expiry the **last report is replayed** so the
endpoint completes on every poll forever. Only the Xbox family declares `NaksWhenIdle`; the Steam
Deck does not, so all three of its streaming endpoints complete continuously:

| Interface | `bInterval` | Carries |
| --- | --- | --- |
| Controller (EP 3) | 6 | The real 64-byte Neptune frame |
| Keyboard (EP 1) | 10 | Nothing — descriptor placeholder |
| Mouse (EP 2) | 10 | Nothing — descriptor placeholder |

Two of the three carried no data at all and still completed roughly 200 transfers per second between
them. That is the first cut, and it is what merged PR #2 removes.

Whether the controller endpoint itself should NAK when idle is a separate question that needs
evidence, not a switch flip. A real Deck appears to stream continuously — its `packetNum` rolls
constantly, and HIDMaestro's own profile sets `alwaysArmed` with a 4 ms idle frame interval for the
same reason — so declaring `NaksWhenIdle` for the Deck would deviate from the hardware Steam thinks
it is talking to. VIIPER already allows forcing it per run (`VIIPER_NAK_IDLE`, `IdleMode`), so the
experiment is cheap; it just has to be measured against Steam actually claiming the device rather
than assumed.

### Applied to the branch

The three fixes merged into `Valkirie/VIIPER` are carried onto corando98's `viiper-controller`
branch, which is well ahead of that fork:

| PR | Fix | State on this branch |
| --- | --- | --- |
| #4 | `ucLength` must be 64 or SDL3 discards every report | already present |
| #3 | Clamp stick Y off `-32768`, which SDL3 negates back to itself | applied |
| #2 | Placeholder mouse/keyboard endpoints must stay pending, not complete with idle input | applied |

PR #2 needed adapting: this branch has replaced the inline `ctx.Done()` waits with
`device.BlockUntilDeadline`, so the merged shape becomes one combined case that blocks and returns
no data. Building VIIPER needs a Go toolchain, which is **not installed on this machine**, so these
two edits are reviewed by inspection and not yet compiled.

## Pinned primary sources

- [HIDMaestro v1.7.0](https://github.com/hifihedgehog/HIDMaestro/releases/tag/v1.7.0), commit
  `46054b862830fcec7bc98d72ccb7c4f0c0179fb1`. Reviewed as the alternative and not chosen, so it is
  no longer a locked component: nothing in a WSGM build downloads, stages or installs it. The
  analysis above stays because the comparison is what justifies the choice.
- [usbip-win2 v.0.9.7.7](https://github.com/vadimgrn/usbip-win2/releases/tag/v.0.9.7.7), commit
  `7c219953101cc5d0ec9a0bcb3eb87259cf72bedd`. WSGM stays on 0.9.7.7 for the same reason HIDMaestro
  does, now checked directly rather than taken second-hand: usbip-win2 issues
  [#180](https://github.com/vadimgrn/usbip-win2/issues/180) and
  [#181](https://github.com/vadimgrn/usbip-win2/issues/181) are still open against 0.9.7.8, and #180
  reports a pool-corruption BSOD on **every** attach on Windows 11 build 26200 — the build the
  reference Claw runs. Neither reproducer is on WSGM's path (#180 needs a vendor-class WinUSB
  device, #181 a USB-audio pin close on a composite DualSense; the `steamdeck` target is HID-class
  with no audio endpoint), so this is caution rather than a known hit — but 0.9.7.8 offers WSGM
  nothing it needs, so there is no reason to take the risk. Revisit when both issues close.
  Verified on 2026-08-29: the 0.9.7.7 asset is an Inno Setup installer whose SHA-256 matches the
  locked digest and whose EV signature matches the locked thumbprint.
- [HidHide v1.5.230.0](https://github.com/nefarius/HidHide/releases/tag/v1.5.230.0), commit
  `722d997ce75db58f5aa36e40ca920f99022c020a`. WSGM's adapter uses the published `\\.\HidHide`
  IOCTL contract directly and preserves the exact external MULTI_SZ entry order.

`eng/acquire-controller-dependencies.ps1` reads this lock file rather than restating it, downloads
the named assets into an explicit artifact directory, and verifies each one's SHA-256 and
Authenticode signer before letting it exist there. It does not execute or install anything.
`eng/checkout-controller-dependency-sources.ps1` checks out the exact reviewed source commits for
independent inspection. It intentionally does not claim release-binary reproduction: publisher
private keys make byte-identical signed output unavailable from a clean public checkout.

## Packaging

There is no ControllerHost process. VIIPER's `libviiper` is a flat C ABI over blittable types, so
the NativeAOT WSGM executable binds it directly and the library ships beside `WSGM.exe` — the same
arrangement as the Rust helpers. The reserved `publish/ControllerHost` staging root is not used.

`build.ps1` builds `libviiper.dll` from the pinned VIIPER revision and stages the verified
usbip-win2 installer into `publish/App`. Both steps are best-effort and skip loudly: a release
machine without a Go toolchain, a C compiler, or a network still produces a good build, and the
result is simply a WSGM whose controller management reports itself unavailable.

Setup installs the driver from one place and one place only — an explicitly ticked task that runs
`Install-UsbipDriver.ps1` while setup is on screen (INV-020). It re-verifies the pinned digest and
signer on the user's disk before running anything, detects an existing install so it never
reinstalls or downgrades one, confirms afterwards that `usbip2_ude` is actually registered rather
than trusting an exit code, and treats every failure as non-fatal. `eng/assert-controller-pin.ps1`
keeps the identity that script carries in step with this lock file.

The USB hub restart is why this may never move into the running shell: installing the driver
re-enumerates every USB 3.0 hub, which on a handheld drops the built-in controller, the touch
digitiser and the keyboard at once.

HidHide is mandatory only while controller management is active. Missing, inactive, inverse-mode,
or unhealthy HidHide makes controller management unavailable without changing global HidHide state.
The production adapter performs exact compare-before-write and exact readback and never toggles the
global active or inverse flags.

## Notices

The reviewed licenses permit redistribution subject to their notice conditions. The exact upstream
license texts are retained under `licenses/` and summarized in `THIRD-PARTY-NOTICES.txt`. That license
review does not override the release gates above or authorize staging the external artifacts.
