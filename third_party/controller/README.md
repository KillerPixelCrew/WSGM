# Controller dependency audit

This directory pins the primary-source inputs reviewed for WSGM controller management. It contains
metadata and notices only. No driver, installer, SDK assembly, or third-party executable is checked
into the repository or copied into an application publish directory.

## Current release decision

Controller management is **not approved**. Under `_plan/2.0-decisions.md` P0-020, WSGM must leave
controller management unavailable while Device Integration, SDL input, and the Steam Input lease
remain usable. `HidMaestroProductionBackend` implements that capability-specific failure and never
loads HIDMaestro, launches a helper, installs a driver, or creates a virtual target.

The reviewed HIDMaestro `steam-deck-composite` profile does not encode the four distinct rear
controls or stick-touch fields required by P6-016. Its driver build also stamps INF versions from the
current date and creates local signing material, so a clean checkout cannot reproduce the exact
signed driver artifacts. These are mandatory release gates, not best-effort diagnostics.

## Pinned primary sources

- [HIDMaestro v1.7.0](https://github.com/hifihedgehog/HIDMaestro/releases/tag/v1.7.0), commit
  `46054b862830fcec7bc98d72ccb7c4f0c0179fb1`. The release archive and
  `HIDMaestro.Core.dll` hashes are locked in `controller-components.lock.json`.
- [usbip-win2 v.0.9.7.7](https://github.com/vadimgrn/usbip-win2/releases/tag/v.0.9.7.7), commit
  `7c219953101cc5d0ec9a0bcb3eb87259cf72bedd`. HIDMaestro deliberately uses 0.9.7.7 instead of
  0.9.7.8 because its upstream README records pool-corruption reports against 0.9.7.8.
- [HidHide v1.5.230.0](https://github.com/nefarius/HidHide/releases/tag/v1.5.230.0), commit
  `722d997ce75db58f5aa36e40ca920f99022c020a`. WSGM's adapter uses the published `\\.\HidHide`
  IOCTL contract directly and preserves the exact external MULTI_SZ entry order.

`eng/acquire-controller-dependencies.ps1` downloads those exact assets into an explicit artifact
directory, verifies every locked hash, verifies the two signed installers, and extracts the pinned
HIDMaestro SDK and its notices. It does not execute or install anything.
`eng/checkout-controller-dependency-sources.ps1` checks out the exact reviewed source commits for
independent inspection. It intentionally does not claim release-binary reproduction: publisher
private keys and HIDMaestro's time-dependent INF stamping make byte-identical signed output
unavailable from a clean public checkout.

## Architecture and future packaging

HIDMaestro is a managed JIT/WinRT SDK and must never be referenced by or staged beside WSGM's
NativeAOT application. If all gates are later closed, the conditional component is reserved for
`publish/ControllerHost` and installed root `ControllerHost/`; it remains separate from both `App`
and the untrusted plugin `DeviceHost`. The installer must verify the locked component identity and
signatures before any explicit, user-approved install or repair operation.

HidHide is mandatory only while controller management is active. Missing, inactive, inverse-mode,
or unhealthy HidHide makes controller management unavailable without changing global HidHide state.
The production adapter performs exact compare-before-write and exact readback and never toggles the
global active or inverse flags.

## Notices

The reviewed licenses permit redistribution subject to their notice conditions. The exact upstream
license texts are retained under `licenses/` and summarized in `THIRD-PARTY-NOTICES.txt`. That license
review does not override the release gates above or authorize staging the external artifacts.
