# VIIPER, and what WSGM needs from it

WSGM's virtual controller targets are created by [VIIPER](https://github.com/Alia5/VIIPER), a
userspace virtual-USB framework that speaks USBIP. Nothing in this directory is a checkout: it
records which revision WSGM builds against, what the downstream commits on it are for, and how to
move the pin. The reasoning for choosing VIIPER over HIDMaestro is in the parent `README.md`.

## Pinned revision

- Repository: [`KillerPixelCrew/VIIPER`](https://github.com/KillerPixelCrew/VIIPER), branch `wsgm`
- Commit: `fe726ce80bd2995a8b149440d977a561850d9e89`
- Baseline: `corando98/VIIPER@024aef3a5659fb54d9675929d05f155f47049c4c` (`viiper-controller`)

The downstream changes used to live here as `.patch` files applied at build time. They are commits
on the fork now, which is what this repository's contributor guide asks of any dependency it has to
keep changed: the diff is reviewable where it applies, `git log` attributes each change, and a
rebase onto a newer baseline is an ordinary rebase rather than six patches to re-fit by hand. The
build script no longer applies anything — it checks the pinned revision out and builds it.

`viiper-controller` is the baseline because it is well ahead of `Valkirie/VIIPER` on the performance
work this integration depends on: opt-in NAK-idle interrupt-IN endpoints, hardware-paced
completions, a type-agnostic clib fast path, value-typed input state, and a `GOMAXPROCS` cap.

## The fork's branches

| Branch | What it is |
| --- | --- |
| `viiper-controller` | Untouched mirror of the upstream baseline. Never commit to it; fast-forward it from `upstream` and rebase `wsgm` onto it. |
| `wsgm` | The baseline plus the downstream commits below. This is what WSGM pins. |
| `main`, `sd-controller-output`, `gh-pages` | Mirrored from upstream at fork time, unused by WSGM. |

The GitHub fork relationship records `corando98/VIIPER` as parent, so a fresh clone already has the
right `upstream`.

## Downstream commits

Listed oldest first, which is the order they sit on the baseline.

| Commit | Change |
| --- | --- |
| `4c111ae` | `steamdeck`: clamp stick Y, leave placeholder endpoints pending, fix the stale quaternion assertion |
| `f29a4b2` | `windows`: declare both `plugin_hardware` layouts, newest first |
| `8179541` | `clib`: `viiper_device_add` no longer attaches |
| `540bda4` | `clib`: plug the usbip client port out on device remove |
| `29b0393` | `clib`: quiesce feedback before the client detach |
| `1ee755c` | `steamdeck`: report credible attributes so Steam sends trigger rumble |
| `935eacb` | `clib`: `viiper_device_add_ex` no longer attaches either |
| `fe726ce` | `windows`: guard the device-interface size query |

The first commit carries two fixes that are merged in `Valkirie/VIIPER` but not on this branch. A
third, the SDL3 `ucLength` fix, is already present here and needed nothing.

| Source | Fix | Why it matters to WSGM |
| --- | --- | --- |
| Valkirie/VIIPER#3 | Clamp stick Y off `-32768` | SDL3's Deck driver negates stick Y with a plain unary minus, so `-32768` wraps to itself and a fully-down stick reads as fully up. Real Deck sticks are calibrated and never report it. |
| Valkirie/VIIPER#2 | Placeholder mouse and keyboard endpoints stay pending | They carry no data, yet completed a transfer on every poll. That both wakes the system from standby and burns CPU for nothing. |
| WSGM | Stale quaternion assertion | `9de6355` deliberately dropped the forced identity orientation quaternion, because a frozen identity made Steam ignore raw angular velocity and collapse gyro-to-stick to centre. The test still expected `0x4000` and was left failing, so the package had no green baseline to regress from. |

`f29a4b2` is WSGM's own, and without it `viiper_device_attach`
cannot succeed against usbip-win2 0.9.7.8. See the next section — it was found by running the call,
not by reading the code.

`8179541` is WSGM's own and stops `viiper_device_add` attaching. That
matters twice over. Upstream attached in both `add` and `attach`, so the documented pair produced
**two** USB/IP attachments of one device — two ports in `usbip port` pointing at the same bus/dev,
and two identical controllers in Steam's controller list (device-observed 2026-08-29). Attach was
only ever described as a retry there, so following the API literally gives a duplicate rather than a
retry. It also made the intended ordering impossible: a caller cannot present a neutral first frame
before Windows enumerates the device when adding it is what enumerates it. With attach explicit,
WSGM opens the fast handle, submits a neutral frame, and only then lets the host see the controller.

`540bda4` backports the detach change from Handheld
Companion's bundled VIIPER commit `679f7e0` without replacing the pinned
`corando98/VIIPER@024aef3a` `viiper-controller` baseline, its newer performance work, or the Steam
Deck fixes above. usbip-win2 assigns a client port when a device is attached. Removing only
VIIPER's server-side device closes its stream but does not plug that port out of the Windows driver,
so an immediate replacement can collide with the stale attachment. It retains the port
returned by either attach route and issues `IOCTL_PLUGOUT_HARDWARE` before server-side removal, with
the command route as fallback.

`29b0393` tightens the removal ordering that `540bda4`
introduced. That commit performed the blocking driver plugout while holding VIIPER's global C-API
mutex and while its reverse feedback callback was still registered; usbip-win2 may deliver a last
output packet as it cancels the endpoints, so a callback could in principle re-enter WSGM from
VIIPER's Go thread while the caller synchronously waited for removal. Removal now deletes the
registration, drains callbacks that already crossed the registration boundary, and releases the
global mutex before asking the driver to plug the port out.

That patch was written against the wrong diagnosis, and the record matters more than the patch. The
"cannot create a new stack guard page" crash on every live target change was **not** native
re-entry: a procdump first-chance `STATUS_STACK_OVERFLOW` dump (2026-09-01) showed 1,598 frames of
`ViiperControllerBackend.SafeNative` calling itself — a managed overload-resolution bug in WSGM
(see the remark on that method). `29b0393` stays because holding the C-API mutex across a driver
request was wrong on its own terms, but it never fixed, and could not have fixed, that crash.

PR #2 needed adapting rather than applying verbatim: this branch replaced the inline `ctx.Done()`
waits with `device.BlockUntilDeadline`, so the two endpoint cases collapse into one that blocks and
returns no data.

`1ee755c` is WSGM's own. Steam decides controller features from
the `GET_ATTRIBUTES_VALUES` identity block, and with the baseline's answers — board revision 1 and
a BCD-style firmware build time (`0x20260226`, which reads as the year 1987 when taken as the unix
epoch Steam expects) — Steam never sends `ID_TRIGGER_RUMBLE_CMD` (0xEB) to the virtual Deck, while
SDL sends it regardless: rumble worked from SDL applications like RPCS3 but never from Steam Input
(device-observed 2026-09-02, via WSGM's undecoded-feedback log showing Steam probing attributes and
then withholding 0xEB). The patch reports the identity hhd's emulated Deck presents — board
revision `0x2e`, real epoch firmware and bootloader build times, and the trailing attribute set
(`0x0c`..`0x0e`) a current Deck answers — which Steam demonstrably sends rumble to.

`935eacb` finishes what `8179541` started. That change stopped `viiper_device_add` attaching but
left `viiper_device_add_ex` doing it, so both the duplicate attachment and the impossible frame
ordering survived on the extended entry point. WSGM calls `add`, so this was never a live fault
here; it was an inconsistency waiting for the first caller that used the other function.

`fe726ce` is ported from upstream, and is the one change here that fixes a crash rather than a
behaviour. See the audit below.

## Audit of the other VIIPER variants

`Valkirie/VIIPER` and `hbashton/VIIPER` were compared against the baseline commit by commit. Both
are measured against the merge base `904bef3` — the fork point on `main` — because neither shares
`viiper-controller`'s history: Valkirie's tree is Alia5 upstream plus its own work, and corando's
branch is 25 commits of Steam Deck and NAK-idle work on top of the same older `main`. Each fork
therefore has an independent Steam Deck implementation, and neither is a superset of the other.

| Variant | Ahead of merge base | Character |
| --- | --- | --- |
| `Valkirie/VIIPER:main` | 56 | Alia5 upstream past the fork point, plus NS2Pro, DualSense/Edge, Steam Controller and Xbox GIP devices, build/CI tooling, and two attach/CPU changes |
| `hbashton/VIIPER:feature/native-udecx-bus` | 621 | Releases 0.0.7-0.0.9 and a native UDE bus rewrite |
| `hbashton/VIIPER:feature/latency-superiority` | 625 | Superset of the above through 0.1.0 |
| `hbashton/VIIPER:ds4-audio-emulation` | 139 | DS4 audio emulation on the Valkirie tooling base |

### Ported

**`fe726ce`, from Alia5's "Solidify autoattach handling" (`d2af157`), reached through Valkirie.**
`getDeviceInterfacePath` discarded the result of its `SetupDiGetDeviceInterfaceDetailW` size query.
That query is *expected* to fail with `ERROR_INSUFFICIENT_BUFFER` — that is how it reports the size
— but on any other failure `requiredSize` stays zero, and the next two lines allocate a zero-length
slice and index element zero of it. Discovery panics inside the server instead of reporting that
the driver was not found. This is the code path WSGM's attach runs through on every controller
creation, so it is the one variant change that is worth the risk of taking. Rewritten rather than
cherry-picked: the structure and its callers are both renamed on this branch.

### Evaluated and not ported

**Valkirie `209c882`, "Deduplicate Windows device attach flow".** The same duplicate attachment
`8179541` fixes, found independently, answered by making the attach idempotent instead. That keeps
`add` enumerating the device, which is the half that makes a neutral first frame impossible, so it
does not solve WSGM's ordering problem. Its one durable observation — that `add_ex` has the bug too
— is taken, as `935eacb`.

**Valkirie `9254837`, "Fixed abnormal CPU usage".** Replaces the URB loop's non-blocking `default:`
with `case <-time.After(10 * time.Millisecond)` and sleeps a millisecond in the cached-report path.
That adds up to 10 ms to every URB header read, which is fatal for a 1 kHz controller. The baseline
does not need it: NAK-idle endpoints and the data-driven completion port already removed both
busy-loops, and the cached-report branch it sleeps in no longer exists here.

**Alia5 `7e33d2d`, "Improve device emulation efficiency".** Already on the baseline — `server.go`
names it in the comment on the data-driven completion plumbing.

**Alia5/Valkirie build and CI work** (`5b3f7fd` justfile, `9710536` golangci-lint, `717a95e`
Windows CI, `520f842` licence notices in CI artifacts). WSGM does not build VIIPER through its
makefile, justfile or CI: `eng\build-viiper.ps1` runs `go build -buildmode=c-shared ./clib`
directly and stages `LICENSE.txt` and `NOTICE.md` itself, which is the requirement the licence
commit exists to satisfy. Taking them would add lint and tooling churn across the whole tree for no
change to what WSGM ships, and would make every future rebase larger.

**Valkirie's device implementations** (NS2Pro, DualSense/Edge, Steam Controller, Xbox GIP,
`xboxelite2`). WSGM emulates one device type, the Steam Deck, and the baseline has its own. Lifting
a second implementation of devices WSGM does not create is pure drift.

**hbashton `7a24743`, "Bound adaptive virtual controller input scheduling".** The stated goal —
stopping cached reports busy-looping through USB/IP and advertising a 1 kHz ceiling — is the
problem the baseline's NAK-idle endpoints and data-driven completions already solve, by a different
route. The commit touches `dualsense`, `dualshock4`, `ns2pro`, `xbox360`, `keyboard` and `mouse`
plus 106 lines of `server.go` that conflict directly with NAK-idle. There is nothing to take
without re-deriving it against a different scheduling model, and no measured deficit here to
justify that.

**hbashton `ae4b5aa`, "Harden VIIPER first-run startup registration".** Hardens
`scripts/install.ps1` and the GitHub build workflow. WSGM installs nothing through VIIPER's script:
`libviiper.dll` is embedded in the WSGM process and usbip-win2 is installed by WSGM's own
`Install-UsbipDriver.ps1`, which already does more than this commit adds — verifies digest and
signer, skips a newer install, and confirms the service registration afterwards rather than
trusting an exit code.

**hbashton's branches generally.** At 621-625 commits ahead of the merge base they are a different
project rather than a patch set. Anything wanted from them has to be read and reimplemented against
this baseline with attribution, never merged as a branch.

## Keeping the fork current

The fork keeps `viiper-controller` as an untouched mirror and `wsgm` as the patch set, so refreshing
against upstream is one rebase:

```
git clone https://github.com/KillerPixelCrew/VIIPER.git
cd VIIPER
git remote add upstream https://github.com/corando98/VIIPER.git
git fetch upstream
git checkout viiper-controller
git merge --ff-only upstream/viiper-controller
git push origin viiper-controller
git rebase viiper-controller wsgm
```

Then, before the pin moves:

1. `go build ./...` succeeds for the whole tree.
2. `go test ./...` fails in exactly the three places named under "Build baseline" and nowhere else.
3. `eng\build-viiper.ps1 -Validate` runs end to end from the new revision.
4. WSGM's controller tests pass, and a controller is created, attached, driven and removed on real
   hardware. `WSGM.DeviceLab` is the tool for the last part; the pin does not move on a green build
   alone, because every fault the commits above exist for was found by running the thing.
5. Drop any downstream commit whose fix has landed upstream, and say so in this file.

Update the pin in `eng\build-viiper.ps1` and the revision and commit table above in the same change.

## How WSGM builds and binds it

`eng\build-viiper.ps1` checks the pinned revision out, optionally runs the Deck
device tests, builds `libviiper.dll` with `go build -buildmode=c-shared ./clib`, and stages it with
its header and licences into `src\WSGM\Native\Viiper`. `WSGM.csproj` copies that beside the
executable. The staging directory is generated and is not committed.

Two toolchains are required and the script names them rather than failing obscurely: Go, and a C
compiler for cgo. Without a C compiler Go quietly sets `CGO_ENABLED=0` and then reports "build
constraints exclude all Go files", which says nothing about the real cause.

The library exposes a flat C ABI over blittable types, so WSGM binds it directly through
`LibraryImport`. VIIPER owns the virtual USB implementation in-process; no helper process is needed.

## Build baseline

Verified with Go 1.27.0 and WinLibs GCC on the reference Claw, 2026-09-11, at the pinned fork
revision. `go build ./...` succeeds for the whole tree, `go test ./device/steamdeck/...` passes, and
`eng\build-viiper.ps1 -Validate` runs the whole sequence end to end and stages `libviiper.dll`.

`go test ./...` fails in exactly three places, all of them present before any downstream commit and
none of them on WSGM's path: `device/xboxelite2` (paddle bit ordering), `device/xboxgip` (LEB128
fragment length), and `internal/server/api` (its test file has not followed `HandleTransfer`'s
context parameter, so the test binary does not compile). A fourth failure appearing is a regression
worth investigating.

**The binding is verified end to end against the real library and the real driver.** Every entry
point WSGM uses — `viiper_init`, `viiper_bus_create`, `viiper_device_add("steamdeck")`,
`viiper_device_attach`, `viiper_device_open_fast`, `viiper_device_set_input_fast` with a 64-byte
Neptune frame, `viiper_device_remove`, `viiper_shutdown` — returns success, and the attach is real
rather than nominal: while attached, Windows enumerates `USB\VID_28DE&PID_1205` as a composite
device with the expected three interfaces (MI_00 keyboard, MI_01 mouse, MI_02 the vendor-defined
controller), and after teardown no `VID_28DE` device is present. Verified on the reference Claw,
2026-08-29, unelevated.

## The attach ABI break, and why the pin is not optional

`viiper_device_attach` failed on the first real attempt, and the reason is worth recording because
the pinned version is what fixes it.

VIIPER attaches by issuing usbip-win2's `IOCTL_PLUGIN_HARDWARE` against the driver's device
interface, falling back to running `usbip.exe`. Both halves were broken here:

- **usbip-win2 0.9.7.8 changed the IOCTL's structure.** `usbip::vhci::ioctl::plugin_hardware` gained
  a trailing `char serial[SERIAL_BUFSZ]`, taking it from 1100 to 1116 bytes. The driver validates
  the caller's `size` against its own `sizeof` and rejects a mismatch with
  `ERROR_INSUFFICIENT_BUFFER` before doing anything. VIIPER encodes the 0.9.7.7 shape, so on
  0.9.7.8 every attach is rejected on size alone. Confirmed against the installed driver by issuing
  the IOCTL directly at both sizes: 1100 returned `122 ERROR_INSUFFICIENT_BUFFER`, 1116 got through
  to `1225 ERROR_CONNECTION_REFUSED` — the expected answer when nothing is listening on the port.
- **The `usbip.exe` fallback cannot save it.** The usbip-win2 installer does not put
  `%ProgramFiles%\USBip` on `PATH`, on this machine in neither the user nor the machine variable, so
  the fallback fails with `executable file not found in %PATH%`. It is not a fallback WSGM can rely
  on, and the answer is not to start editing `PATH`.

So the 0.9.7.7 pin has a functional reason on top of the open BSOD reports: it is the version
VIIPER's ABI actually matches. `0002-attach-plugin-hardware-layouts.patch` makes the backend work on
both, by declaring the newer structure and trying the two known sizes newest-first. That retry is
safe rather than a repeated attach: a size rejection happens before the driver acts, is reported
with its own specific error code, and any other failure stops immediately instead of being retried
against a layout the driver has already refused.

**Do not "simplify" that loop to a single size, and do not replace it with a version probe.** The
driver's own rejection is the authority on which layout it wants; a version number read from
somewhere else is a second source that can be wrong.

Three packages fail on this branch **before** any WSGM patch and are the accepted baseline:
`device/xboxelite2`, `device/xboxgip`, and `internal/server/api` (build failure). None is touched by
the patch, and none is on WSGM's path — but a fourth failure appearing is a regression worth
investigating.

## What the installer must provide

VIIPER needs three things on Windows, and none of them may be installed by the running shell —
INV-020 keeps driver, service, and certificate installation in the installer, as an explicit,
user-approved, elevated step that verifies the locked component identity first.

1. **usbip-win2**, which supplies the generic signed kernel-mode USB/IP driver and the client device
   VIIPER attaches to. Pinned and signature-verified in `../controller-components.lock.json`
   (`USBip-0.9.7.7-x64.exe`, publisher thumbprint `9AC56B6C…`). This is the one kernel component,
   it is generic, and it never needs to know about specific device types — which is the whole reason
   this approach avoids shipping a driver per controller.
2. **`libviiper`**, the VIIPER server built as a shared library from `clib/`. It runs in userspace,
   embedded in WSGM's controller component rather than as a separate service, and listens on a local
   USBIP port. It is built from the pinned revision above with the patches applied.
3. **HidHide**, already pinned, and already mandatory only while controller management is active.

Licensing is settled and is not a blocker: WSGM is GPL-3.0 and so is the VIIPER server, so shipping
it is straightforward. Retain the upstream notices as for any other shipped component.

The remaining installer requirement is ordinary failure handling: verify each locked component
identity before installing it, and keep a machine where usbip-win2 is absent or declined installing
and running WSGM normally, with controller management simply unavailable — exactly as today.

### State of the installer work

`WSGM.iss` declares a `controller` component; `libviiper.dll` with its header and notices, and the
verified usbip-win2 and HidHide installers, ship under it. They are required release inputs, not
optional ones: `build.ps1` fails when the library was not produced or an installer could not be
acquired and verified, because a release that offers the component must contain all of it. A
release machine therefore needs a Go toolchain, a C compiler, and a network.

The driver step is a separate ticked task, `Install-UsbipDriver.ps1`, run from `[Run]` before setup
restarts anything of WSGM's. It prefers the staged installer and falls back to downloading the same
pinned asset, re-verifies digest and signer on this disk either way, skips an install that is
already present or newer, and confirms `usbip2_ude` is registered afterwards instead of trusting the
exit code. Every failure is non-fatal: a machine without the driver runs WSGM normally with
controller management unavailable.

Two things learned by doing rather than reading, both of which would have produced a broken step:

- The release asset is an **Inno Setup** installer, not NSIS. VIIPER's own `scripts/install.ps1`
  passes `/S`, which Inno Setup does not recognise — that script pops the full interactive installer
  instead of installing silently. The correct switches are `/VERYSILENT /SUPPRESSMSGBOXES
  /NORESTART /NOCANCEL /SP-`.
- **`System32\drivers\usbip2_ude.sys` does not exist even on a working install.** It is a universal
  driver and lives in the driver store; on the reference Claw the real path is
  `DriverStore\FileRepository\usbip2_ude.inf_amd64_…`, reached through the `ImagePath` of the
  `usbip2_ude` service key. A file test — which is what VIIPER's script falls back to — reports "not
  installed" on a machine where it is. `pnputil` is no substitute either: its output is localised,
  and it prints German here.

`viiper_device_attach` has since been driven end to end on the reference Claw; the duplicate
attachment that led to `0003`, the stale port that led to `0004`, and the rumble identity that led
to `0006` were all found by running it (see the patch notes above).
