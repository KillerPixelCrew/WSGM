# Engineering scripts

This scope owns repository verification, generated assets, dependency staging, native builds, and
developer deployment. Keep scripts root-relative, non-interactive by default, strict about exit
codes, and safe to rerun.

## Verification contract

- eng/verify.ps1 is the canonical gate. Preserve its checks for optional Prettier formatting, Steam
  asset drift and ownership claims, AGENTS/CLAUDE link integrity, tracked PowerShell syntax,
  live-data path exclusions, controller pins, Steam Input validation, restore, warning-clean Release
  builds, all solution tests, and main-test coverage.
- -SkipPrettier skips only formatting. It must not skip the generated asset build, claims check,
  compilation, or tests.
- -Fix may rewrite formatted files. Never hide unrelated changes in that pass; inspect the diff
  afterward.
- Parse potentially invasive scripts for syntax instead of executing them as part of verification.
- eng/verify.ps1 validates Steam Input but does not build or validate VIIPER. A VIIPER change
  requires `eng/build-viiper.ps1 -Validate` with a source tree that may safely be replaced.

## Build and staging rules

- eng/build-steam-assets.mjs is the sole generator for the embedded Steam UI asset. It composes
  toolkit TypeScript with optional source under src/WSGM/Core/SteamUiAssets/Source, writes
  NativeQamBootstrap.js, and updates its hash in SteamUiAssetCatalog.cs. Commit owning source,
  gitlink changes, and both generated updates together.
- Build Steam Input and VIIPER from source. Treat publish and staging directories as disposable
  output; do not populate them manually.
- eng/build-viiper.ps1 force-checks out, hard-resets, and cleans its SourceRoot, which defaults to
  the sibling `wsgm-viiper` directory. Never point it at a working checkout or any tree containing
  uncommitted work.
- Keep third_party exclusions distinct from external Git submodules. Do not format or rewrite
  dependency source from a main-repository gate.
- Staging must validate package identity, version, architecture, and required files before copying
  anything into the installer tree.
- eng/dev-deploy.ps1 is an attended, machine-specific operation. It checks the supported board,
  stops and restarts live WSGM or Steam processes, and stages a plugin. Never invoke it as a smoke
  test.

For focused work, run the individual script you changed. Follow the root validation policy when
deciding whether to run the full gate:

    .\eng\verify.ps1

Use the root build.ps1 only when the task requires complete publish and installer output.
