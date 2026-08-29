# VIIPER, and what WSGM needs from it

WSGM's virtual controller targets are created by [VIIPER](https://github.com/Alia5/VIIPER), a
userspace virtual-USB framework that speaks USBIP. Nothing in this directory is a checkout: it holds
the exact upstream revision WSGM builds against and the patches that revision still needs. The
reasoning for choosing VIIPER over HIDMaestro is in the parent `README.md`.

## Pinned revision

- Repository: `corando98/VIIPER`, branch `viiper-controller`
- Commit: `024aef3a5659fb54d9675929d05f155f47049c4c`

That branch is well ahead of `Valkirie/VIIPER` and carries the performance work this integration
depends on: opt-in NAK-idle interrupt-IN endpoints, hardware-paced completions, a type-agnostic clib
fast path, value-typed input state, and a `GOMAXPROCS` cap.

## Patches WSGM applies

`0001-steamdeck-idle-and-stick-fixes.patch` carries two fixes that are merged in `Valkirie/VIIPER`
but not on this branch. A third, the SDL3 `ucLength` fix, is already present here and needs no
patch.

| Source | Fix | Why it matters to WSGM |
| --- | --- | --- |
| Valkirie/VIIPER#3 | Clamp stick Y off `-32768` | SDL3's Deck driver negates stick Y with a plain unary minus, so `-32768` wraps to itself and a fully-down stick reads as fully up. Real Deck sticks are calibrated and never report it. |
| Valkirie/VIIPER#2 | Placeholder mouse and keyboard endpoints stay pending | They carry no data, yet completed a transfer on every poll. That both wakes the system from standby and burns CPU for nothing. |
| WSGM | Stale quaternion assertion | `9de6355` deliberately dropped the forced identity orientation quaternion, because a frozen identity made Steam ignore raw angular velocity and collapse gyro-to-stick to centre. The test still expected `0x4000` and was left failing, so the package had no green baseline to regress from. |

PR #2 needed adapting rather than applying verbatim: this branch replaced the inline `ctx.Done()`
waits with `device.BlockUntilDeadline`, so the two endpoint cases collapse into one that blocks and
returns no data.

## How WSGM builds and binds it

`eng\build-viiper.ps1` checks the pinned revision out, applies the patches, optionally runs the Deck
device tests, builds `libviiper.dll` with `go build -buildmode=c-shared ./clib`, and stages it with
its header and licences into `src\WSGM\Native\Viiper`. `WSGM.csproj` copies that beside the
executable. The staging directory is generated and is not committed.

Two toolchains are required and the script names them rather than failing obscurely: Go, and a C
compiler for cgo. Without a C compiler Go quietly sets `CGO_ENABLED=0` and then reports "build
constraints exclude all Go files", which says nothing about the real cause.

The library exposes a flat C ABI over blittable types, so WSGM's NativeAOT executable binds it
directly through `LibraryImport` — the same arrangement as the Rust helpers, and the reason no helper
process is needed for a virtual controller.

## Build baseline

Verified with Go 1.27.0 and WinLibs GCC on the reference Claw, 2026-08-29. `go build ./...` succeeds
for the whole tree, `go test ./device/steamdeck/...` passes with the patch applied, and
`eng\build-viiper.ps1 -Validate` runs the whole sequence end to end.

**The binding is verified against the real library, not just compiled.** Driving it through the same
entry points WSGM uses — `viiper_init`, `viiper_bus_create`, `viiper_device_add("steamdeck")`,
`viiper_device_open_fast`, `viiper_device_set_input_fast` with a 64-byte Neptune frame,
`viiper_device_remove`, `viiper_shutdown` — every call returned success. That covers everything short
of `viiper_device_attach`, which needs the usbip-win2 driver installed and is therefore the first
step once the installer work lands.

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

`WSGM.iss` declares a `controller` component; `libviiper.dll` with its notices and header, and the
verified usbip-win2 installer, ship under it. Every one of those entries is
`skipifsourcedoesntexist`, because they exist only when the release machine has a Go toolchain, a C
compiler, and a network — `build.ps1` skips each loudly rather than failing an otherwise good
release.

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

With the driver present, `viiper_device_attach` is the one entry point the binding has not yet been
driven through. That is now testable on this machine, which already carries a usbip-win2 install.
