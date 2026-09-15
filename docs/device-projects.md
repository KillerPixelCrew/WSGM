# Device projects in WSGM

The SDK, Device Lab, Claw plugin, Handheld Companion scaffold and ROG Ally X scaffold are maintained
in this repository. Their source lives under `src`, their tests under `tests`, and `WSGM.slnx`
includes them all. Every consumer references `src/WSGM.Device.Sdk/WSGM.Device.Sdk.csproj`, so a
contract change and its consumers build and go through review together.

| Project            | Source and documentation                                                        | Status                                                         |
| ------------------ | ------------------------------------------------------------------------------- | -------------------------------------------------------------- |
| SDK                | [WSGM.Device.Sdk](../src/WSGM.Device.Sdk/README.md)                             | Public MIT contract and NuGet package support                  |
| Device Lab         | [WSGM.DeviceLab](../src/WSGM.DeviceLab/README.md)                               | Separate GUI/CLI executable, optional installer component      |
| MSI Claw           | [WSGM.Device.Msi.Claw8A2Vm](../src/WSGM.Device.Msi.Claw8A2Vm/README.md)         | Built-in reference plugin, loaded dynamically                  |
| ASUS ROG Ally X    | [WSGM.Device.Asus.RogAllyX](../src/WSGM.Device.Asus.RogAllyX/README.md)         | Passive scaffold, exact detection and hardware support pending |
| Handheld Companion | [WSGM.Device.HandheldCompanion](../src/WSGM.Device.HandheldCompanion/README.md) | Design scaffold and IPC proposal, no working plugin yet        |

WSGM still references only the SDK at compile time. Device Lab and plugins remain separate
assemblies with their existing lifecycle and package boundaries. The installer continues to ship the
Claw package and optional Device Lab tool; it does not ship the HC or Ally X scaffolds.

Run from the repository root:

```powershell
dotnet build WSGM.slnx --configuration Release
dotnet test WSGM.slnx --configuration Release --no-build
./eng/verify.ps1
```

For a focused SDK change, run
`dotnet test tests/WSGM.Device.Sdk.Tests/WSGM.Device.Sdk.Tests.csproj`. Use the corresponding test
project for Device Lab or Claw work. Hardware validation remains a separate, explicitly attended
operation.

When a package or publish artifact is needed:

```powershell
dotnet pack src/WSGM.Device.Sdk/WSGM.Device.Sdk.csproj --configuration Release --output publish/sdk
./eng/publish-device-lab.ps1
./eng/pack-device.ps1 -Source src/WSGM.Device.Msi.Claw8A2Vm -RequireGlyphs
```

`eng/stage-device-components.ps1` builds installer components from these same sources.
`eng/pack-device.ps1 -Source <project directory>` packs any device project, so the HC and Ally X
scaffolds stay packable; add `-RequireGlyphs` for a package that ships physical glyphs. It uses
`eng/device-package-output.ps1` to replace an existing archive atomically or publish a new one
without overwriting a competing file. A failed replacement preserves the previous archive.

The four imported source trees and their matching test trees retain their original MIT licenses.
Each has a `LICENSE` file. The new Ally X scaffold is also MIT-licensed. The imported packaging
scripts (`eng/publish-device-lab.ps1` and `eng/pack-device.ps1`, which merges the former Claw and HC
packers) retain the MIT license of their respective source projects. Their shared
`eng/device-package-output.ps1` and `eng/device-lab-publish.ps1` helpers are also MIT-licensed. WSGM's main application remains
GPL-3.0-or-later.

The Generic PC repository contained only a design scaffold, with no implemented behavior to move. It
is retired. Windows-wide features belong in Core; device-specific integrations still belong in
plugins. See the ownership decision in the implementation tracker.

## Import revisions

The consolidation on 2026-09-05 imported these merged revisions. Existing history remains in the
original repositories; these identifiers record the exact source baseline for the move.

| Former repository             | Revision                                   |
| ----------------------------- | ------------------------------------------ |
| WSGM.Device.Sdk               | `0d874c72966309d77d36b7c1e965ec21ef8edf57` |
| WSGM.DeviceLab                | `3ea7aaf59f0e9d066afe45ab4f0fe58bb6c799bb` |
| WSGM.Device.Msi.Claw8A2Vm     | `e7092811840c835b43e98a4eeb1c75c9cc6b435a` |
| WSGM.Device.HandheldCompanion | `ea52f2332fe5d69ac6f39e81b553a22332f26c13` |

Only `external/steam-ui-toolkit`, `external/windows-device-control`, and `native/SteamInput` remain
Git submodules. There are no nested SDK pins to advance.

## Portable Ally X tester

[Ally X Lab](../tools/AllyXLab/README.md) is a separate developer tool for an attended remote
tester. Its self-contained EXE is committed under `tools/AllyXLab/Downloads` at the maintainer's
request. It and `tests/WSGM.AllyXLab.Tests` build independently of `WSGM.slnx`; neither is an
installer component. The source comparison and outstanding hardware validation live beside the Ally
X plugin.
