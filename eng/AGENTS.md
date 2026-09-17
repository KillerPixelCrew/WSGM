# Engineering scripts

This scope owns repository verification, generated assets, dependency staging, native builds, and
developer deployment. Keep scripts root-relative, non-interactive by default, strict about exit
codes, and safe to rerun.

## Verification contract

- eng/verify.ps1 is the canonical gate. Preserve its checks for optional Prettier formatting, Steam
  asset drift and ownership claims, AGENTS/CLAUDE link integrity, tracked PowerShell syntax,
  live-data path exclusions, version-copy agreement, the Ally X Lab download manifest, controller
  pins, Steam Input validation, restore, warning-clean Release builds, all solution tests, and
  main-test coverage.
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
  toolkit TypeScript with optional source under src/WSGM/Core/SteamUiAssets/Source, writes
  NativeQamBootstrap.js, and updates its hash in SteamUiAssetCatalog.cs. Commit owning source,
  gitlink changes, and both generated updates together.
- Build Steam Input and VIIPER from source. Treat publish and staging directories as disposable
  output; do not populate them manually.
- eng/qodana-bootstrap.ps1 is the Qodana for .NET bootstrap named in qodana.yaml. It builds the
  native libraries and the solution, runs WSGM.Tests with coverage, and copies the Cobertura report
  to .qodana/code-coverage. The `qodana-dotnet` job in `.github/workflows/ci.yml` sets
  `WSGM_QODANA_USE_STAGED_NATIVE` and `WSGM_QODANA_COVERAGE_FROM` so it reuses the libraries and
  coverage from the `verify` and `viiper` jobs instead. It is a CI step, not a local gate.
- .qodana/dotnet-baseline.sarif.json is the Qodana for .NET baseline the `qodana-dotnet` job passes
  with `--baseline`. It holds the reviewed findings that stay on purpose, each with its reason in
  _plan/qodana-review-2.0.md; only findings new against it fail the check. Regenerate it from a scan
  of the current tree when a reviewed finding is deliberately added, never to hide a new one.
- eng/build-viiper.ps1 builds the external/viiper submodule as checked out. build.ps1 passes
  `-RequirePinned`, which refuses a dirty submodule or one that is not at the gitlink HEAD records,
  so a release library always matches a pinned commit. Move the VIIPER pin by pushing to the fork's
  `wsgm` branch and advancing the gitlink, as for any other submodule. The fork's default branch is
  `viiper-controller`, so `.gitmodules` records `branch = wsgm` for `git submodule update --remote`.
- external/ holds submodules, vendored upstream source, and dependency pins. Do not format or
  rewrite it from a main-repository gate.
- Device packers share `device-package-output.ps1` for archive publication. Keep staging on the
  destination volume, replace owned archives atomically, and use create-new semantics otherwise.
  Never delete the previous archive before its replacement commits.
- `publish-device-lab.ps1` and `stage-device-components.ps1` publish Device Lab through
  `device-lab-publish.ps1`, which copies the exact restored runtime notices and the licence. The
  unsafe package id and version refusal lives in `pack-device.ps1` only; staging keeps its built-in
  identity, entry assembly, glyph and extracted-tree checks.
- Staging must validate package identity, version, architecture, and required files before copying
  anything into the installer tree.
- `new-plugin.ps1` and `package-plugin.ps1` take the common API version and manifest validation from
  the Plugin SDK through `plugin-manifest.cs`. Do not restate identity patterns or API ranges there.
- eng/dev-deploy.ps1 is an attended, machine-specific operation. It checks the supported board,
  stops and restarts live WSGM or Steam processes, and stages a plugin. Never invoke it as a smoke
  test.

For focused work, run the individual script you changed. Follow the root validation policy when
deciding whether to run the full gate:

    .\eng\verify.ps1

Use the root build.ps1 only when the task requires complete publish and installer output.
