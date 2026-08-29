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

## Build baseline

Verified with Go 1.27.0 on the reference Claw, 2026-08-29. `go build ./...` succeeds for the whole
tree, and `go test ./device/steamdeck/...` passes with the patch applied.

Three packages fail on this branch **before** any WSGM patch and are the accepted baseline:
`device/xboxelite2`, `device/xboxgip`, and `internal/server/api` (build failure). None is touched by
the patch, and none is on WSGM's path — but a fourth failure appearing is a regression worth
investigating.

## What the installer must provide

VIIPER needs three things on Windows, and none of them may be installed by the running shell —
INV-020 keeps driver, service, and certificate installation in the installer, as an explicit,
user-approved, elevated step that verifies the locked component identity first.

1. **usbip-win2**, which supplies the generic signed kernel-mode USBIP driver and the VHCI device
   VIIPER attaches to. Already pinned and signature-verified in `../controller-components.lock.json`
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
