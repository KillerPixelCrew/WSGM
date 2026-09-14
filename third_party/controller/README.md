# Controller dependency audit

This directory pins the primary sources I reviewed for WSGM controller management:

- `controller-components.lock.json` holds the reviewed usbip-win2 and HidHide releases, with
  digests, signers and install identity. Every script that needs those values reads this file.
- `licenses/` and `THIRD-PARTY-NOTICES.txt` hold the upstream licence texts and their summary.
- `viiper/` holds the exact VIIPER revision WSGM builds `libviiper.dll` from and the six patches
  `eng\build-viiper.ps1` applies on top of it, with its own `README.md` recording why each one
  exists.

No driver, installer, SDK assembly or third-party executable is checked in. The release build
fetches the two installers from their pinned releases and builds the library from its pinned
revision, and nothing here is copied into an application publish directory as-is.

## Where this landed

Controller management uses VIIPER directly in WSGM. The optional installer component carries
`libviiper.dll`, usbip-win2's signed driver and HidHide, and runtime code never installs or repairs
any of them. If a prerequisite is missing, controller management alone is unavailable while SDL
input, the Steam Input lease and the rest of Device Integration keep working.

### Why VIIPER replaced HIDMaestro

Decided 2026-08-29. VIIPER creates virtual USB devices in userspace over USBIP, and it wins on both
halves of the gate. WSGM builds it from the `KillerPixelCrew/VIIPER` fork, whose `wsgm` branch
carries the downstream commits on top of the `corando98/VIIPER` `viiper-controller` baseline;
`viiper/README.md` has the pin, the commit list and the rebase procedure.

**Nothing is missing.** Its `device/steamdeck` carries the whole Neptune frame natively, including
all four rear controls and capacitive stick touch. Three independent implementations agree on the
bit map exactly: VIIPER's `device/steamdeck/const.go`, HandheldCompanion's `SteamDeckTarget`, and
`hhd`'s virtual Deck. L5 at bit 15, R5 at 16, L4 at 41, R4 at 42, pad touch at 19 and 20, and stick
touch at 46 and 47. No profile authoring and no upstream extension is needed to satisfy WSGM's
controller contract.

**The driver problem disappears.** VIIPER rides `usbip-win2`'s signed kernel driver, which is the
exact component already pinned and signature-verified in `controller-components.lock.json`,
publisher thumbprint `9AC56B6C…`. There is no locally built driver, no self-signed certificate to
trust and no INF date stamping, so the reproducibility gate that blocked HIDMaestro never comes up.
WSGM still installs it only through the installer, as an explicit user-approved elevated step,
because INV-020 forbids the runtime from installing a driver whatever its provenance.

HIDMaestro 1.7.0's profile named only two of the four rear controls and its managed state had no
capacitive stick-touch fields, and its generated driver package was locally signed and stamped from
the build date. Those are the concrete reasons it is not kept as a second backend or source
checkout. Git history has the full comparison.

### The one real cost, and where it comes from

VIIPER driving a virtual Steam Deck in HandheldCompanion measured a constant 6 to 8% CPU. On a
handheld that is a battery cost rather than a rounding error, and it was the original reason to
prefer HIDMaestro. It needs fixing rather than accepting, and I now know where it comes from.

Measured 2026-08-29 on the reference Claw (Core Ultra 7 258V, 8 cores). An attached, idle virtual
Steam Deck, sampled over interleaved 20 second runs of the same probe against two builds of the
library, three rounds each, alternating, so drift and thermal state hit both:

| Build | Of one core | Of the machine |
| --- | --- | --- |
| Both WSGM patches | 7.06 / 6.13 / 6.51%, mean **6.6%** | **0.82%** |
| Without PR #2 (placeholder endpoints) | 8.90 / 6.92 / 9.15%, mean **8.3%** | 1.04% |

Two things come out of that, and the second is the one that matters.

**PR #2 is worth carrying.** The patched build wins every paired round, by roughly a fifth of the
cost, about 1.7 points of a core. A single sample would not have shown it: the run-to-run spread
overlaps between the two builds, and the first pair I measured looked like noise. It only shows up
because the runs are paired and repeated.

**The famous 6 to 8% is per core, not per machine.** It lands almost exactly on the patched build's
own per-core figure, so the number that nearly sent WSGM to a different backend was measuring the
same thing this does. On eight cores that is under 1% of the CPU. It is still worth reducing on a
battery-powered device, and the open question below is still worth answering, but it was never the
disqualifying cost it got treated as and it does not justify a different backend.

Submissions are not the cost. Driving the device with input frames instead of leaving it idle moved
the figure by about a tenth of a point. That run reached about 64 Hz rather than the intended 250 Hz
because `Thread.Sleep` granularity dominates a 4 ms wait, so it shows submissions are cheap rather
than anything specific about 250 Hz.

What the CPU actually goes on is the keepalive replay. VIIPER completes interrupt-IN transfers one
of two ways (`internal/server/usb/server.go`, `startInWorker`). Devices that declare
`NaksWhenIdle()` block on their input gate and go quiet when nothing changes. Everything else takes
the keepalive path: a per-attempt deadline of one `bInterval`, and on expiry the last report is
replayed, so the endpoint completes on every poll forever. Only the Xbox family declares
`NaksWhenIdle`, and the Steam Deck does not, so all three of its streaming endpoints complete
continuously:

| Interface | `bInterval` | Carries |
| --- | --- | --- |
| Controller (EP 3) | 6 | the real 64-byte Neptune frame |
| Keyboard (EP 1) | 10 | nothing, descriptor placeholder |
| Mouse (EP 2) | 10 | nothing, descriptor placeholder |

Two of the three carried no data at all and still completed roughly 200 transfers per second between
them. That is the first cut, and it is what merged PR #2 removes.

Whether the controller endpoint itself should NAK when idle is a separate question that needs
evidence rather than a switch flip. A real Deck appears to stream continuously, its `packetNum`
rolls constantly, and HIDMaestro's own profile sets `alwaysArmed` with a 4 ms idle frame interval
for the same reason, so declaring `NaksWhenIdle` for the Deck would deviate from the hardware Steam
thinks it is talking to. VIIPER already allows forcing it per run (`VIIPER_NAK_IDLE`, `IdleMode`), so
the experiment is cheap, but it has to be measured with Steam actually claiming the device rather
than assumed.

### What is on the branch

`viiper/README.md` is the record of what WSGM carries on top of the pinned revision, and it must
stay the only one. In short: the two `Valkirie/VIIPER` fixes that branch lacks (#3 stick clamp, #2
placeholder endpoints kept pending) travel in `0001`, along with a stale quaternion test assertion;
the SDL3 `ucLength` fix (#4) is already upstream; and `0002` through `0006` are ours, being the
usbip-win2 attach layouts, add-without-attach, port plug-out on remove, feedback quiescence before
detach, and the credible Deck identity that makes Steam send rumble. `eng\build-viiper.ps1
-Validate` applies the whole set and runs the Deck device tests before every release build.

## Pinned primary sources

**[usbip-win2 v.0.9.7.7](https://github.com/vadimgrn/usbip-win2/releases/tag/v.0.9.7.7)**, commit
`7c219953101cc5d0ec9a0bcb3eb87259cf72bedd`. WSGM stays on 0.9.7.7 for the same reason HIDMaestro
does, which I checked directly rather than taking second-hand: usbip-win2 issues
[#180](https://github.com/vadimgrn/usbip-win2/issues/180) and
[#181](https://github.com/vadimgrn/usbip-win2/issues/181) are still open against 0.9.7.8, and #180
reports a pool-corruption BSOD on every attach on Windows 11 build 26200, which is the build the
reference Claw runs. Neither reproducer is on WSGM's path (#180 needs a vendor-class WinUSB device,
#181 a USB-audio pin close on a composite DualSense, while the `steamdeck` target is HID-class with
no audio endpoint), so this is caution rather than a known hit. But 0.9.7.8 offers WSGM nothing it
needs, so there is no reason to take the risk. Revisit when both issues close. Checked on
2026-08-29: the 0.9.7.7 asset is an Inno Setup installer whose SHA-256 matches the locked digest and
whose EV signature matches the locked thumbprint.

**[HidHide v1.5.230.0](https://github.com/nefarius/HidHide/releases/tag/v1.5.230.0)**, commit
`722d997ce75db58f5aa36e40ca920f99022c020a`. WSGM's adapter uses the published `\\.\HidHide` IOCTL
contract directly and preserves the exact external MULTI_SZ entry order.

`eng/acquire-controller-dependencies.ps1` reads the lock file rather than restating it, downloads
the named assets into an explicit artifact directory, and verifies each one's SHA-256 and
Authenticode signer before letting it exist there. It does not execute or install anything.
`eng/checkout-controller-dependency-sources.ps1` checks out the exact reviewed source commits for
independent inspection. It deliberately does not claim to reproduce the release binaries, because
publisher private keys make byte-identical signed output impossible from a clean public checkout.

## Packaging

VIIPER's `libviiper` is a flat C ABI over blittable types. WSGM binds it directly and ships the
library beside `WSGM.exe`.

`build.ps1` builds `libviiper.dll` from the pinned VIIPER revision and stages the verified
usbip-win2 and HidHide installers into `publish/App`. These are required release inputs: a runtime
machine may leave out the optional controller component, but a release artifact has to contain the
complete component it offers.

Setup installs the drivers from exactly one place, the explicitly ticked `usbipdriver` task, which
runs `Install-UsbipDriver.ps1` and then HidHide's own silent installer while setup is on screen
(INV-020). The script re-verifies the pinned digest and signer on the user's disk before running
anything, detects an existing install so it never reinstalls or downgrades one, confirms afterwards
that `usbip2_ude` is actually registered rather than trusting an exit code, treats every failure as
non-fatal, and writes a bounded status marker under `%ProgramData%\WSGM` that setup reads instead of
the exit code. `eng/assert-controller-pin.ps1` keeps the identity that script carries in step with
the lock file.

The USB hub restart is why this may never move into the running shell: installing the driver
re-enumerates every USB 3.0 hub, which on a handheld drops the built-in controller, the touch
digitiser and the keyboard all at once.

## HidHide

HidHide is mandatory only while controller management is active. Missing, inactive, inverse-mode or
unhealthy HidHide makes controller management unavailable without changing global HidHide state. The
production adapter does an exact compare before writing and an exact readback afterwards, and never
touches the global active or inverse flags.

**WSGM is not the only thing that uses HidHide, and it used to assume it was.** I found this on the
reference Claw on 2026-08-29. `HidHideOwnedDeltaManager` adds WSGM to HidHide's application
allowlist, but it did that only as the first step of WSGM's own hiding transaction, meaning only
once controller management was already activating. That ordering cannot survive a machine where
something else hid the controller first.

On this unit HandheldCompanion had done exactly that. Its leftover configuration blocked
`HID\VID_0DB0&PID_1901&IG_00\…` and `HID\VID_0DB0&PID_1902&MI_00&COL01\…`, the pad in both modes,
with an allowlist naming only HandheldCompanion and HidHide's own tools. The effect on WSGM was
total and silent: SDL reported no gamepad at all, the plugin's HID enumeration could not see the
DirectInput pad it had just switched the device into, controller acquisition failed with
`PrerequisiteMissing`, and nothing anywhere said the word HidHide. Adding WSGM to the allowlist made
the pad visible again immediately. The in-process runtime needs only WSGM's executable identity.

The pad itself was never the problem, and this is worth writing down so nobody re-diagnoses it: in
DirectInput mode it enumerates as `1902/0001:0005 in64 out32`, it streams while idle at roughly
125 Hz, and its first report arrives about a millisecond after opening. Measured with the exact
`GENERIC_READ | GENERIC_WRITE` overlapped open the plugin uses.

Both fixes have landed. `HidHideOwnedDeltaManager.EnsureReadableAsync` allowlists WSGM's own image
before anything needs to read a device, with `DeviceCoordinator` calling it ahead of the plugin's
cycle start and again before controller management is re-enabled. It adds nothing to the hidden set
and leaves another owner's entries alone, so the later hiding transaction finds it present and
records no delta. And when the allowance cannot be granted, the failure text names HidHide, so "no
exact interface identity was available" no longer sends the diagnosis in the wrong direction. The
device evidence, including the NT-path notation finding, is in `docs\device-security.md`.

## Notices

The reviewed licences permit redistribution subject to their notice conditions. The exact upstream
licence texts are under `licenses/` and summarized in `THIRD-PARTY-NOTICES.txt`. That licence review
does not override the release gates above and does not authorize staging the external artifacts.
