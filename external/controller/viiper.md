# VIIPER, and what WSGM needs from it

WSGM's virtual controller targets are created by [VIIPER](https://github.com/Alia5/VIIPER), a
userspace virtual-USB framework that speaks USBIP. The source is the `external\viiper` submodule.
This file records which revision WSGM builds against, what the downstream commits on it are for, and
how to move the pin. Why VIIPER rather than HIDMaestro is in `README.md`.

## Pinned revision

- Repository: [`KillerPixelCrew/VIIPER`](https://github.com/KillerPixelCrew/VIIPER), branch `wsgm`
- Commit: the `external\viiper` gitlink
- Upstream: [`Alia5/VIIPER`](https://github.com/Alia5/VIIPER) `main`, merged at `41c66b1`

The downstream changes used to live here as `.patch` files applied at build time. They are commits
on the fork now, which is what this repository's contributor guide asks of any dependency we have to
keep changed: the diff is reviewable where it applies, `git log` attributes each change, and taking
upstream is an ordinary merge rather than patches to re-fit by hand. The build script no longer
applies anything, it builds the submodule as checked out.

Until 2026-09 the fork was based on `corando98/VIIPER`'s `viiper-controller`, which was well ahead on
the performance work this integration depends on. corando98's repository is itself a fork of
Alia5's, and most VIIPER work lands in Alia5's, so the fork now follows Alia5 directly (WSGM#164).
corando98's fork and Handheld Companion's `Valkirie/VIIPER` are cherry-pick sources only: a commit
from either is taken on its own, with attribution, when WSGM needs it, and neither is merged as a
baseline.

## The fork's branches

| Branch | What it is |
| --- | --- |
| `main` | Untouched mirror of `Alia5/VIIPER` `main`. Never commit to it; fast-forward it from `upstream`. |
| `wsgm` | Upstream plus the downstream changes. The fork's default branch, and what WSGM pins. |
| `viiper-controller` | The former corando98 baseline, frozen. Kept for history; nothing merges into it. |
| `sd-controller-output`, `gh-pages` | Mirrored at fork time, unused by WSGM. |

GitHub still records `corando98/VIIPER` as the fork's parent, and that cannot be changed, so `gh pr
create` defaults to the wrong repository. Always pass `--repo KillerPixelCrew/VIIPER --base wsgm`.

## What the fork carries

`NOTICE.md` on the branch lists the changes against upstream; in short:

- `clib`, the C library WSGM binds: add and attach as separate calls, port plug-out on remove,
  per-type input fast paths, raw feedback callbacks drained before removal, panic recovery at the
  cgo boundary, a `GOMAXPROCS` cap, and the device-type aliases.
- `internal/server/usb`: persistent per-endpoint interrupt-IN workers, hardware-paced completions and
  per-device NAK-idle endpoints, in place of upstream's per-URB completion goroutines.
- Windows attach: a cancellable overlapped `plugin_hardware` IOCTL that negotiates three driver
  layouts, and does not retry an attach whose outcome is uncertain.
- `device/steamdeck`, which upstream does not have, and the input gate that `xbox360`, `keyboard`,
  `mouse` and `dualshock4` use so updates do not allocate.
- Devices WSGM never creates: `steamcontroller`, `switchpro`, `xboxelite2`, `xboxgip` and
  `cmd/gip_probe`. They are candidates for removal.

### What would let the fork go away

WSGM builds `clib`, not upstream's own C library in `lib/viiper`. That library lacks add without
attach, a returned attach port and plug-out on remove, a raw-bytes input path, raw feedback
callbacks (the Deck needs them) and callback draining. Upstream has no Steam Deck device and none of
the server idle or pacing work. Until those exist upstream, moving to patch files would mean
re-fitting the `server.go` and attach rewrites on every sync, which is the churn the commits
replaced. The route that shrinks the fork is upstreaming instead:

- Bug fixes that stand alone: the attach layout negotiation (41c66b1 matches English error text, has
  no 0.9.7.8 layout and misplaces the new fields), plug-out on remove, the cancellable IOCTL, the
  `xbox360` secondary-endpoint resubmit loop, accept backoff and panic recovery.
- The Steam Deck device, once it uses upstream's input pattern instead of the fork's input gate.
- The idle and pacing work, proposed as an `IdleMode` option with measured CPU numbers.
- The `lib/viiper` features above, which would let WSGM bind upstream's library.

## Downstream commits

The history of the commits WSGM added on the corando98 baseline, oldest first. They stay on `wsgm`
through the Alia5 merge; where upstream has since landed the same fix, the merge took upstream's.

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
| `e9da7e2` | `usb`: stop recurring timeout attempts on unused Steam Deck keyboard/mouse endpoints |
| `4d2bd52` | Trim the downstream README ending |

### `4c111ae`, three fixes in one

Two of them are merged in `Valkirie/VIIPER` but missing from this branch. A third, the SDL3
`ucLength` fix, is already here and needed nothing.

| Source | Fix | Why it matters to WSGM |
| --- | --- | --- |
| Valkirie/VIIPER#3 | Clamp stick Y off `-32768` | SDL3's Deck driver negates stick Y with a plain unary minus, so `-32768` wraps to itself and a fully-down stick reads as fully up. Real Deck sticks are calibrated and never report it. |
| Valkirie/VIIPER#2 | Placeholder mouse and keyboard endpoints stay pending | They carry no data, yet completed a transfer on every poll. That both wakes the system from standby and burns CPU for nothing. |
| WSGM | Stale quaternion assertion | `9de6355` deliberately dropped the forced identity orientation quaternion, because a frozen identity made Steam ignore raw angular velocity and collapse gyro-to-stick to centre. The test still expected `0x4000` and was left failing, so the package had no green baseline to regress from. |

PR #2 needed adapting rather than applying verbatim: this branch replaced the inline `ctx.Done()`
waits with `device.BlockUntilDeadline`, so the two endpoint cases collapse into one that blocks and
returns no data.

### `f29a4b2`, the attach layouts

Ours, and without it `viiper_device_attach` cannot succeed against usbip-win2 0.9.7.8. The Alia5
merge extended it to the 0.9.8.0 layout. The whole story is under "The attach ABI break" below. It
was found by running the call, not by reading the code.

### `8179541` and `935eacb`, add no longer attaches

Upstream attached in both `add` and `attach`, so the documented pair produced two USB/IP attachments
of one device: two ports in `usbip port` pointing at the same bus and dev, and two identical
controllers in Steam's controller list. I saw that on the device on 2026-08-29. Attach was only ever
described as a retry there, so following the API literally gives you a duplicate rather than a
retry.

It also made the intended ordering impossible. A caller cannot present a neutral first frame before
Windows enumerates the device when adding it is what enumerates it. With attach explicit, WSGM opens
the fast handle, submits a neutral frame, and only then lets the host see the controller.

`935eacb` finishes the job: `8179541` stopped `viiper_device_add` attaching but left
`viiper_device_add_ex` doing it, so both the duplicate attachment and the impossible frame ordering
survived on the extended entry point. WSGM calls `add`, so this was never a live fault here, just an
inconsistency waiting for the first caller that used the other function.

### `540bda4`, plug the port out

Backported from Handheld Companion's bundled VIIPER commit `679f7e0`, without replacing the pinned
baseline, its newer performance work or the Steam Deck fixes above.

usbip-win2 assigns a client port when a device is attached. Removing only VIIPER's server-side
device closes its stream but does not plug that port out of the Windows driver, so an immediate
replacement can collide with the stale attachment. The commit retains the port returned by either
attach route and issues `IOCTL_PLUGOUT_HARDWARE` before server-side removal, with the command route
as fallback.

### `29b0393`, and a diagnosis that was wrong

This tightens the removal ordering `540bda4` introduced. That commit performed the blocking driver
plugout while holding VIIPER's global C-API mutex and while its reverse feedback callback was still
registered. usbip-win2 may deliver a last output packet as it cancels the endpoints, so a callback
could in principle re-enter WSGM from VIIPER's Go thread while the caller synchronously waited for
removal. Removal now deletes the registration, drains callbacks that already crossed the
registration boundary, and releases the global mutex before asking the driver to plug the port out.

I wrote that patch against the wrong diagnosis, and the record matters more than the patch. The
"cannot create a new stack guard page" crash on every live target change was not native re-entry. A
procdump first-chance `STATUS_STACK_OVERFLOW` dump on 2026-09-01 showed 1,598 frames of
`ViiperControllerBackend.SafeNative` calling itself, which is a managed overload-resolution bug in
WSGM (see the remark on that method). `29b0393` stays because holding the C-API mutex across a
driver request was wrong on its own terms, but it never fixed, and could not have fixed, that crash.

### `1ee755c`, making Steam send rumble

Ours. Steam decides controller features from the `GET_ATTRIBUTES_VALUES` identity block, and with
the baseline's answers, board revision 1 and a BCD-style firmware build time (`0x20260226`, which
reads as the year 1987 when taken as the unix epoch Steam expects), Steam never sends
`ID_TRIGGER_RUMBLE_CMD` (0xEB) to the virtual Deck. SDL sends it regardless, so rumble worked from
SDL applications like RPCS3 but never from Steam Input. Found on the device on 2026-09-02, through
WSGM's undecoded-feedback log showing Steam probing attributes and then withholding 0xEB.

The patch reports the identity hhd's emulated Deck presents: board revision `0x2e`, real epoch
firmware and bootloader build times, and the trailing attribute set (`0x0c` to `0x0e`) a current
Deck answers. Steam demonstrably sends rumble to that.

### `fe726ce`, the one crash fix from another fork

Ported from upstream. Details in the audit below.

### `e9da7e2`, the unused endpoints

Keeps the placeholder endpoints pending without repeatedly creating keepalive deadlines. Automatic
idle mode now accepts a per-endpoint declaration, so the real controller endpoint keeps its
continuous reports, and explicit idle-mode overrides still win. The DLL and regression-test binaries
compile; execution and live CPU and controller validation still need a manual check.

## Audit of the other VIIPER variants

Written against the corando98 baseline, before the fork moved to Alia5. The Alia5 entries below are
merged now; the Valkirie and hbashton entries still describe cherry-pick candidates.

I compared `Valkirie/VIIPER` and `hbashton/VIIPER` against the baseline commit by commit. Both are
measured against the merge base `904bef3`, the fork point on `main`, because neither shares
`viiper-controller`'s history: Valkirie's tree is Alia5 upstream plus its own work, and corando's
branch is 25 commits of Steam Deck and NAK-idle work on top of the same older `main`. So each fork
has an independent Steam Deck implementation, and neither is a superset of the other.

| Variant | Ahead of merge base | What it is |
| --- | --- | --- |
| `Valkirie/VIIPER:main` | 56 | Alia5 upstream past the fork point, plus NS2Pro, DualSense/Edge, Steam Controller and Xbox GIP devices, build and CI tooling, and two attach/CPU changes |
| `hbashton/VIIPER:feature/native-udecx-bus` | 621 | Releases 0.0.7 to 0.0.9 and a native UDE bus rewrite |
| `hbashton/VIIPER:feature/latency-superiority` | 625 | Superset of the above through 0.1.0 |
| `hbashton/VIIPER:ds4-audio-emulation` | 139 | DS4 audio emulation on the Valkirie tooling base |

### Ported

**`fe726ce`, from Alia5's "Solidify autoattach handling" (`d2af157`), reached through Valkirie.**
`getDeviceInterfacePath` discarded the result of its `SetupDiGetDeviceInterfaceDetailW` size query.
That query is *expected* to fail with `ERROR_INSUFFICIENT_BUFFER`, since that is how it reports the
size, but on any other failure `requiredSize` stays zero and the next two lines allocate a
zero-length slice and index element zero of it. Discovery panics inside the server instead of
reporting that the driver was not found. This is the code path WSGM's attach runs through on every
controller creation, so it is the one variant change worth the risk of taking. Rewritten rather than
cherry-picked, because the structure and its callers are both renamed on this branch.

### Evaluated and not ported

**Valkirie `209c882`, "Deduplicate Windows device attach flow".** The same duplicate attachment
`8179541` fixes, found independently, answered by making the attach idempotent instead. That keeps
`add` enumerating the device, which is the half that makes a neutral first frame impossible, so it
does not solve our ordering problem. Its one durable observation, that `add_ex` has the bug too, is
taken as `935eacb`.

**Valkirie `9254837`, "Fixed abnormal CPU usage".** Replaces the URB loop's non-blocking `default:`
with `case <-time.After(10 * time.Millisecond)` and sleeps a millisecond in the cached-report path.
That adds up to 10 ms to every URB header read, which is fatal for a 1 kHz controller. The baseline
does not need it: NAK-idle endpoints and the data-driven completion port already removed both
busy-loops, and the cached-report branch it sleeps in no longer exists here.

**Alia5 `7e33d2d`, "Improve device emulation efficiency".** Already on the baseline. `server.go`
names it in the comment on the data-driven completion plumbing.

**Alia5 and Valkirie build and CI work** (`5b3f7fd` justfile, `9710536` golangci-lint, `717a95e`
Windows CI, `520f842` licence notices in CI artifacts). WSGM does not build VIIPER through its
makefile, justfile or CI. `eng\build-viiper.ps1` runs `go build -buildmode=c-shared ./clib` directly
and stages `LICENSE.txt` and `NOTICE.md` itself, which is the requirement the licence commit exists
to satisfy. Taking them would add lint and tooling churn across the whole tree for no change to what
WSGM ships, and would make every future rebase bigger.

**Valkirie's device implementations** (NS2Pro, DualSense/Edge, Steam Controller, Xbox GIP,
`xboxelite2`). WSGM emulates one device type, the Steam Deck, and the baseline has its own. Lifting
a second implementation of devices WSGM does not create is pure drift.

**hbashton `7a24743`, "Bound adaptive virtual controller input scheduling".** The stated goal,
stopping cached reports busy-looping through USB/IP and advertising a 1 kHz ceiling, is the problem
the baseline's NAK-idle endpoints and data-driven completions already solve by a different route.
The commit touches `dualsense`, `dualshock4`, `ns2pro`, `xbox360`, `keyboard` and `mouse` plus 106
lines of `server.go` that conflict directly with NAK-idle. There is nothing to take without
re-deriving it against a different scheduling model, and no measured deficit here to justify that.

**hbashton `ae4b5aa`, "Harden VIIPER first-run startup registration".** Hardens
`scripts/install.ps1` and the GitHub build workflow. WSGM installs nothing through VIIPER's script:
`libviiper.dll` is embedded in the WSGM process and usbip-win2 is installed by our own
`Install-UsbipDriver.ps1`, which already does more than this commit adds. It verifies the digest and
signer, skips a newer install, and confirms the service registration afterwards rather than trusting
an exit code.

**hbashton's branches generally.** At 621 to 625 commits ahead of the merge base they are a
different project, not a patch set. Anything we want from them has to be read and reimplemented
against this baseline with attribution, never merged as a branch.

## Keeping the fork current

`main` mirrors Alia5, and `wsgm` takes it by merge. `wsgm` is published and pinned, so it is never
rebased.

```
cd external/viiper
git remote add upstream https://github.com/Alia5/VIIPER.git
git fetch upstream
git push origin upstream/main:main
git switch -c sync/<topic> origin/wsgm
git merge origin/main
```

Resolve the conflicts, follow any upstream API change through `clib` and the fork-only devices, then
open a PR with `gh pr create --repo KillerPixelCrew/VIIPER --base wsgm`. Before the pin moves:

1. `eng\build-viiper.ps1 -Validate` runs end to end from the new revision, including the check that
   `libviiper.h` matches the library's exports.
2. `go test ./...` fails only in the places named under "Build baseline".
3. WSGM's controller tests pass, and a controller is created, attached, driven and removed on real
   hardware. The pin does not move on a green build alone, because every fault the commits above
   exist for was found by running the thing.
4. Record here any downstream change upstream has made unnecessary.

Merge the fork PR first, then advance the `external\viiper` gitlink in the same WSGM change that
updates this file.

## How WSGM builds and binds it

`eng\build-viiper.ps1` builds the `external\viiper` submodule, optionally runs the Deck device tests, builds
`libviiper.dll` with `go build -buildmode=c-shared ./clib`, and stages it with its header and
licences into `src\WSGM\Native\Viiper`. `WSGM.csproj` copies that beside the executable. The staging
directory is generated and is not committed.

Two toolchains are required and the script names them rather than failing obscurely: Go, and a C
compiler for cgo. Without a C compiler, Go quietly sets `CGO_ENABLED=0` and then reports "build
constraints exclude all Go files", which tells you nothing about the real cause.

The library exposes a flat C ABI over blittable types, so WSGM binds it directly through
`LibraryImport`. VIIPER owns the virtual USB implementation in-process, and no helper process is
needed.

## Build baseline

Checked with Go 1.27.0 and WinLibs GCC 16.1 on 2026-09-24, after the Alia5 merge.
`eng\build-viiper.ps1 -Validate` runs `go vet ./...`, the Deck and `clib` tests and the build, and
stages `libviiper.dll` with the same 19 exports as before the merge.

`go test ./...` fails in exactly two places, both present before the merge and neither on WSGM's path:
`device/xboxelite2` (paddle bit ordering and profile button layouts) and `device/xboxgip` (LEB128
fragment length). The `internal/server/api` test build that used to fail is fixed by the merge. A
new failure is a regression worth investigating.

**The binding is verified end to end against the real library and the real driver.** Every entry
point WSGM uses returns success: `viiper_init`, `viiper_bus_create`,
`viiper_device_add("steamdeck")`, `viiper_device_attach`, `viiper_device_open_fast`,
`viiper_device_set_input_fast` with a 64-byte Neptune frame, `viiper_device_remove` and
`viiper_shutdown`. The attach is real rather than nominal: while attached, Windows enumerates
`USB\VID_28DE&PID_1205` as a composite device with the expected three interfaces (MI_00 keyboard,
MI_01 mouse, MI_02 the vendor-defined controller), and after teardown no `VID_28DE` device is
present. Checked on the reference Claw on 2026-08-29, unelevated.

## The attach ABI break, and why the pin is not optional

`viiper_device_attach` failed on the first real attempt, and it is worth recording why, because the
pinned version is what fixes it.

VIIPER attaches by issuing usbip-win2's `IOCTL_PLUGIN_HARDWARE` against the driver's device
interface, falling back to running `usbip.exe`. Both halves were broken here.

**usbip-win2 0.9.7.8 changed the IOCTL's structure.** `usbip::vhci::ioctl::plugin_hardware` gained a
trailing `char serial[SERIAL_BUFSZ]`, taking it from 1100 to 1116 bytes. The driver validates the
caller's `size` against its own `sizeof` and rejects a mismatch with `ERROR_INSUFFICIENT_BUFFER`
before doing anything. VIIPER encodes the 0.9.7.7 shape, so on 0.9.7.8 every attach is rejected on
size alone. I confirmed that against the installed driver by issuing the IOCTL directly at both
sizes: 1100 returned `122 ERROR_INSUFFICIENT_BUFFER`, and 1116 got through to
`1225 ERROR_CONNECTION_REFUSED`, which is the expected answer when nothing is listening on the port.

**The `usbip.exe` fallback cannot save it.** The usbip-win2 installer does not put
`%ProgramFiles%\USBip` on `PATH`, in neither the user nor the machine variable on this machine, so
the fallback fails with `executable file not found in %PATH%`. It is not a fallback WSGM can rely
on, and the answer is not to start editing `PATH`.

So the 0.9.7.7 pin had a functional reason on top of the then-open BSOD reports: it was the
version VIIPER's ABI actually matched. WSGM now pins 0.9.8.0. `f29a4b2` makes the backend work on both, by declaring the newer
structure and trying the two known sizes newest-first. That retry is safe rather than a repeated
attach: a size rejection happens before the driver acts, is reported with its own specific error
code, and any other failure stops immediately instead of being retried against a layout the driver
has already refused.

**usbip-win2 0.9.8.0 changed it again.** The structure gained `bool wsk_events`, which selects a
receive path built on WSK event callbacks that usbip-win2 recommends for small, frequent reports. In
C++ it is `struct plugin_hardware : base, imported_device_location`, so the location base is padded
to 1096 bytes on its own before the serial: the serial sits at offset 1100, `wsk_events` at 1116,
and the whole structure is 1120 bytes. Alia5's `41c66b1` flattens those fields and puts both three
bytes early, which only works because it leaves them zero. The fork lays the structure out with
explicit padding, pins the offsets at compile time, and sets `wsk_events`. 0.9.8.0 rejects a size
mismatch with `STATUS_INVALID_BUFFER_SIZE`, which arrives as `ERROR_INVALID_USER_BUFFER` (1784)
rather than 0.9.7.x's `ERROR_INSUFFICIENT_BUFFER`, still before acting, so the loop now tries 1120,
1116 and 1100 and treats either error as a layout rejection.

**Do not simplify that loop to a single size, and do not replace it with a version probe.** The
driver's own rejection is the authority on which layout it wants. A version number read from
somewhere else is a second source that can be wrong.

## What the installer must provide

VIIPER needs three things on Windows, and none of them may be installed by the running shell.
INV-020 keeps driver, service and certificate installation in the installer, as an explicit,
user-approved, elevated step that verifies the locked component identity first.

1. **usbip-win2**, which supplies the generic signed kernel-mode USB/IP driver and the client device
   VIIPER attaches to. Pinned and signature-verified in `controller-components.lock.json`
   (`USBip-0.9.8.0-x64.exe`, publisher thumbprint `9AC56B6C…`). This is the one kernel component, it
   is generic, and it never needs to know about specific device types, which is the whole reason
   this approach avoids shipping a driver per controller.
2. **`libviiper`**, the VIIPER server built as a shared library from `clib/`. It runs in userspace,
   embedded in WSGM's controller component rather than as a separate service, and listens on a local
   USBIP port. It is built from the pinned revision above.
3. **HidHide**, already pinned, and already mandatory only while controller management is active.

Licensing is settled and is not a blocker: WSGM is GPL-3.0 and so is the VIIPER server, so shipping
it is straightforward. Retain the upstream notices as for any other shipped component.

The remaining installer requirement is ordinary failure handling: verify each locked component
identity before installing it, and keep a machine where usbip-win2 is absent or declined installing
and running WSGM normally, with controller management simply unavailable, exactly as today.

### Where the installer work stands

`WSGM.iss` declares a `controller` component, and `libviiper.dll` with its header and notices, plus
the verified usbip-win2 and HidHide installers, ship under it. They are required release inputs
rather than optional ones: `build.ps1` fails when the library was not produced or an installer could
not be acquired and verified, because a release that offers the component has to contain all of it.
A release machine therefore needs a Go toolchain, a C compiler and a network.

The driver step is a separate ticked task, `Install-UsbipDriver.ps1`, run from `[Run]` before setup
restarts anything of WSGM's. It prefers the staged installer and falls back to downloading the same
pinned asset, re-verifies the digest and signer on this disk either way, skips an install that is
already present or newer, and confirms `usbip2_ude` is registered afterwards instead of trusting the
exit code. Every failure is non-fatal: a machine without the driver runs WSGM normally with
controller management unavailable.

Two things I only learned by doing it, either of which would have produced a broken step:

- The release asset is an **Inno Setup** installer, not NSIS. VIIPER's own `scripts/install.ps1`
  passes `/S`, which Inno Setup does not recognise, so that script pops the full interactive
  installer instead of installing silently. The correct switches are
  `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL /SP-`.
- **`System32\drivers\usbip2_ude.sys` does not exist even on a working install.** It is a universal
  driver and lives in the driver store. On the reference Claw the real path is
  `DriverStore\FileRepository\usbip2_ude.inf_amd64_…`, reached through the `ImagePath` of the
  `usbip2_ude` service key. A file test, which is what VIIPER's script falls back to, reports "not
  installed" on a machine where it is. `pnputil` is no substitute either: its output is localised,
  and it prints German here.

`viiper_device_attach` has since been driven end to end on the reference Claw. The duplicate
attachment behind `8179541`, the stale port behind `540bda4` and the rumble identity behind
`1ee755c` were all found by running it.
