# Engineering scripts

This scope owns repository verification, generated assets, dependency staging, native builds, and
developer deployment. Keep scripts root-relative, non-interactive by default, strict about exit
codes, and safe to rerun.

## Verification contract

- eng/verify.ps1 is the canonical gate. Preserve its checks for optional Prettier formatting, Steam
  asset drift and ownership claims, AGENTS/CLAUDE link integrity, tracked PowerShell syntax,
  live-data path exclusions, version-copy agreement, controller, PawnIO and KX pins, Steam Input
  validation, restore, warning-clean Release builds, all solution tests, and main-test coverage.
- Analyzer rules kept for a whole scope are severity-scoped in .editorconfig, with the reason on
  each block.
- -SkipPrettier skips only formatting. It must not skip the generated asset build, claims check,
  compilation, or tests.
- -Fix may rewrite formatted files. Never hide unrelated changes in that pass; inspect the diff
  afterward.
- Parse potentially invasive scripts for syntax instead of executing them as part of verification.
- eng/verify.ps1 validates Steam Input but does not build or validate VIIPER. A VIIPER change
  requires `eng/build-viiper.ps1 -Validate`. CI runs that separately from the main `verify` job (see
  `.github/workflows/ci.yml`), so a broken VIIPER pin is caught before a release tag rather than
  inside the tag-triggered release job.

## Build and staging rules

- eng/build-steam-assets.mjs is the sole generator for the embedded Steam UI asset. It composes
  the toolkit's fragment list (`external/steam-ui-toolkit/eng/steam-ui-fragments.mjs`) with the
  source under src/WSGM/Core/SteamUiAssets/Source and writes NativeQamBootstrap.js. The runtime
  hashes the embedded bytes, so no C# is rewritten. Commit owning source, gitlink changes, and the
  generated file together. `npm run steam-assets:claims` runs every toolkit emitted-asset check
  against that file.
- Build Steam Input and VIIPER from source. Treat publish and staging directories as disposable
  output; do not populate them manually.
- eng/build-viiper.ps1 builds the external/viiper submodule as checked out. build.ps1 passes
  `-RequirePinned`, which refuses a dirty submodule or one that is not at the gitlink HEAD records,
  so a release library always matches a pinned commit. Move the VIIPER pin by pushing to the fork's
  `wsgm` branch and advancing the gitlink, as for any other submodule. The fork's default branch is
  `viiper-controller`, so `.gitmodules` records `branch = wsgm` for `git submodule update --remote`.
- external/ holds submodules, vendored upstream source, and dependency pins. Do not format or
  rewrite it from a main-repository gate.
- The plugin and device packers share `plugin-package-common.ps1` for archive publication. Keep
  staging on the destination volume, replace owned archives atomically, and use create-new semantics
  otherwise. Never delete the previous archive before its replacement commits.
- `publish-device-lab.ps1` and `build-bundle.ps1` publish Device Lab through
  `device-lab-publish.ps1`, which copies the exact restored runtime notices and the licence. The
  unsafe package id and version refusal lives in `pack-device.ps1` only; staging keeps its built-in
  identity, entry assembly, glyph and extracted-tree checks.
- `build-bundle.ps1` is the only producer of `Packages\*.wsgmpkg` and `bundle.json`. It reads
  `plugins/curated`, packs first-party plugins from this checkout and community plugins from their
  pinned commit against a local SDK feed. It records a community build failure as outdated instead
  of failing. Run its community step only where no secret is available.
- Both packers stamp `wsgmVersion` from `src/WSGM/WSGM.csproj` and strip the assemblies WSGM
  supplies itself (`Remove-HostProvidedFiles`); never ship a package without either step.
- `device-lab-publish.ps1` runs `acquire-pawnio.ps1` first, which downloads the installer pinned in
  `external/pawnio/pawnio.lock.json` into `artifacts/pawnio` and refuses a wrong digest or signer.
  Device Lab embeds it when present. Offline, the installer tree only warns and that build reports
  PawnIO as unavailable; `publish-device-lab.ps1 -Portable`, the one-file tester build, fails instead.
  `assert-pawnio-pin.ps1` (run by eng/verify.ps1) checks the lock's shape and, when present, the
  acquired file; it never downloads.
- Staging must validate package identity, version, architecture, and required files before copying
  anything into the installer tree.
- `new-plugin.ps1` and `package-plugin.ps1` take the common API version and manifest validation from
  the Plugin SDK through `plugin-manifest.cs`. Do not restate identity patterns or API ranges there.
- eng/dev-deploy.ps1 is an attended, machine-specific operation. It checks the supported board,
  stops and restarts live WSGM or Steam processes, and stages a plugin. It stops WSGM through
  `Local\WSGM.ExitForUpdate`, as setup does, so the exit cleanup runs; a force stop is only the
  announced fallback. Never invoke it as a smoke test.
- eng/build-uwp-bridge.ps1 compiles the vendored MinHook in external/minhook into the packaged-game
  bridge. It never reads another component's restored dependencies.

For focused work, run the individual script you changed. Follow the root validation policy when
deciding whether to run the full gate:

    .\eng\verify.ps1

Use the root build.ps1 only when the task requires complete publish and installer output.
