# Building and navigating the source

[`WSGM.slnx`](../WSGM.slnx) is the current managed-project topology. The application runs on Windows
x64 with .NET 10 and Avalonia; it publishes a self-contained CoreCLR application. The root
[`build.ps1`](../build.ps1) assembles the native libraries, managed executables, plugin packages,
component installers and setup payload. A solution build alone does not produce that installer.

Start with [the documentation index](README.md) for subsystem behavior. The process/startup flow is
in [boot and shell](boot-and-shell.md), installation in [setup](setup.md), ordinary game wrapping in
[elevation](elevation.md), and imported/followed games in
[packaged-game launcher](packaged-game-launcher.md). The contributor and validation policy lives in
[`AGENTS.md`](../AGENTS.md); nearer guides add rules for their subtree.

## Solution and ownership

| Project or source group                                                                                                                                                                       | Owns                                                                                                                            | Read next                                                                                         |
| --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------- |
| [`src/WSGM`](../src/WSGM)                                                                                                                                                                     | Process startup, user configuration, Avalonia UI, session policy and integration composition                                    | [Boot/session source map](boot-and-shell.md#startup-and-lifetime-source-map)                      |
| [`WSGM.Install`](../src/WSGM.Install)                                                                                                                                                         | Shared install paths, bundle models, hardware offers, account identity and component selection                                  | [Setup source ownership](setup.md#source-ownership)                                               |
| [`WSGM.Setup`](../src/WSGM.Setup)                                                                                                                                                             | Elevated install/update/repair/uninstall and embedded payload                                                                   | [Setup](setup.md)                                                                                 |
| [`WSGM.LogonService`](../src/WSGM.LogonService)                                                                                                                                               | Minimal SYSTEM service, per-session user-token launch and Explorer watchdog                                                     | [Logon service](boot-and-shell.md#logon-service-and-boot-flow)                                    |
| [`WSGM.Launch`](../src/WSGM.Launch)                                                                                                                                                           | Console wrapper for de-elevation and process-tree Steam Input leases                                                            | [Elevation](elevation.md)                                                                         |
| [`WSGM.PackagedLaunch`](../src/WSGM.PackagedLaunch)                                                                                                                                           | Package activation, optional overlay routes, lifetime recovery and external-launcher following                                  | [Packaged launcher](packaged-game-launcher.md)                                                    |
| [`WSGM.Plugin.Sdk`](../src/WSGM.Plugin.Sdk)                                                                                                                                                   | Common plugin contracts and extension vocabulary                                                                                | [Plugin system](plugin-system.md)                                                                 |
| [`WSGM.Device.Sdk`](../src/WSGM.Device.Sdk)                                                                                                                                                   | Device contracts, lifecycle, capabilities, controller data and package layout                                                   | [Device plugin system](device-plugin-system.md)                                                   |
| [`WSGM.Device.Msi.Claw`](../src/WSGM.Device.Msi.Claw), [`WSGM.Device.Asus.RogAlly`](../src/WSGM.Device.Asus.RogAlly), [`WSGM.Device.HandheldCompanion`](../src/WSGM.Device.HandheldCompanion) | Machine-specific behavior packaged separately from host policy; each README records implementation and hardware-evidence status | [Device authoring](device-plugin-authoring.md)                                                    |
| [`WSGM.Plugin.IntelGpu`](../src/WSGM.Plugin.IntelGpu), [`WSGM.Plugin.NvidiaGpu`](../src/WSGM.Plugin.NvidiaGpu), [`WSGM.Plugin.AmdGpu`](../src/WSGM.Plugin.AmdGpu)                             | Vendor graphics-driver capability providers                                                                                     | [Plugin system](plugin-system.md) and package READMEs                                             |
| [`WSGM.Plugin.Ir`](../src/WSGM.Plugin.Ir)                                                                                                                                                     | IR session-action plugin and its firmware/protocol                                                                              | [IR README](../src/WSGM.Plugin.Ir/README.md)                                                      |
| [`WSGM.DeviceLab`](../src/WSGM.DeviceLab)                                                                                                                                                     | Package validation, authoring support and attended hardware diagnostics                                                         | [Device Lab README](../src/WSGM.DeviceLab/README.md)                                              |
| [`Avalonia.LiveBackdrop`](../src/Avalonia.LiveBackdrop)                                                                                                                                       | Reusable live-backdrop rendering implementation                                                                                 | [LiveBackdrop README](../src/Avalonia.LiveBackdrop/README.md)                                     |
| [`external/steam-ui-toolkit`](../external/steam-ui-toolkit)                                                                                                                                   | Reusable CEF/CDP transport, Steam UI ownership and generated browser-side foundation                                            | [Toolkit README](../external/steam-ui-toolkit/README.md), [WSGM integration](steam-cef-system.md) |
| [`external/windows-device-control`](../external/windows-device-control)                                                                                                                       | Reusable Windows audio, radio, display, device and power mechanisms                                                             | [Library README](../external/windows-device-control/README.md)                                    |
| [`src/Shared`](../src/Shared)                                                                                                                                                                 | Source-linked contracts/primitives used by multiple executables or plugin assemblies                                            | Shared boundaries below                                                                           |
| [`tests`](../tests) and external library test projects                                                                                                                                        | Isolated policy, protocol, UI and library verification                                                                          | Validation policy below                                                                           |

Inside WSGM, `Program.cs` owns entry ordering and `App.axaml.cs` composes the selected lifetime.
`Core` holds configuration, persistence and application policy; `Shell` owns long-lived services and
session orchestration; `Overlay` and `Settings` project those owners into their respective UI;
`Input` translates controller and hotkey activity; `Interop` contains native declarations;
`Controls` and `Themes` contain presentation primitives. Device-specific writes belong to the
selected device package, graphics-driver writes to their plugin, and reusable Windows mechanisms to
WindowsDeviceControl. The SDK assemblies define contracts rather than host policy.

The SDKs are MIT-licensed so external packages can implement them. The product has its own GPL
license; licenses and notices for vendored code remain with their respective components. See each
project's license before reusing its source.

## Shared boundaries and generated files

`src/Shared` is not a project or a separate runtime service. Consumers explicitly link the same
files in their project files. A change therefore recompiles every consumer, which can have distinct
runtime copies of a type.

| Shared source                                                       | Consumers and contract                                                                                               |
| ------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------- |
| `Boot/BootManifest.cs`, `Boot/AtomicFile.cs`                        | WSGM and logon service agree on the per-user sign-in manifest without a service reference to the desktop application |
| `Install/InstallLayout.cs`                                          | Install assembly and logon service agree on machine roots and pending-setup detection                                |
| `Process/SessionProtocolNames.cs`                                   | WSGM and setup agree on cross-version shutdown, shell-owner and anchor-event names                                   |
| `Process/ScheduledTaskXml.cs`, `Process/WindowsCommandLine.cs`      | WSGM and ordinary launcher use the same task identity and argument quoting                                           |
| `Process/ParentProcessStart.cs`, `Process/Win32Common.cs`           | Process-start primitives used by WSGM and the packaged launcher; service also links the common native declarations   |
| `Process/SteamControllerExclusion.cs`, `Process/RotatingFileLog.cs` | Launcher environment sanitization and rotating diagnostics; service shares the rotating logger                       |
| `Launch/PackagedLaunchCommand.cs`                                   | Library shortcut composer and packaged launcher share parse/compose vocabulary                                       |
| `Interop` and `Gpu`                                                 | Explicitly linked native and graphics implementation primitives; `.csproj` includes define their consumers           |

The Steam browser payload is also composed from one set of owning sources.
[`eng/build-steam-assets.mjs`](../eng/build-steam-assets.mjs) combines the toolkit fragment list
with [`Core/SteamUiAssets/Source`](../src/WSGM/Core/SteamUiAssets/Source) and emits
`NativeQamBootstrap.js`. Edit the owning TypeScript fragments, regenerate, and include the generated
asset with the change. The runtime hashes the embedded bytes; it does not require a manually edited
C# asset hash.

```powershell
npm run steam-assets:build
npm run steam-assets:check
```

The second command rebuilds in memory and checks drift. `npm run steam-assets:claims` executes the
toolkit's browser-asset ownership checks against the composed WSGM payload and WSGM's
module-discovery checks. It is a test-bearing command and follows the manual-first policy below.

## Checkout and prerequisites

Use a full checkout with recursive submodules. The two managed libraries are projects in the
solution; Steam Input Lease and VIIPER are source-built native dependencies. Their revisions come
from the parent repository's gitlinks. `external/viiper` tracks the fork's `wsgm` branch, not its
default branch. `git submodule update --init --recursive` restores recorded revisions; adding
`--remote` would intentionally move beyond them.

```powershell
git submodule update --init --recursive
npm ci --ignore-scripts --no-audit --no-fund
dotnet tool restore
```

| Tool                                                              | Needed for                                                                                                                              |
| ----------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------- |
| .NET 10 SDK                                                       | Managed restore/build; `global.json` requests a stable SDK with `latestMinor` roll-forward                                              |
| Node.js 24 and npm                                                | Locked Prettier/TypeScript tools and Steam asset composition; matches CI                                                                |
| PowerShell 7 (`pwsh`)                                             | Repository engineering/build scripts; Windows PowerShell also runs the installed USB/IP step                                            |
| Git with submodule and symlink support                            | Dependency checkout and linked vendored/guidance files; enable Windows Developer Mode or appropriate symlink privileges before checkout |
| Rust MSVC toolchain and Cargo                                     | Steam Input Lease DLLs; validation additionally uses Clippy                                                                             |
| Go version required by `external/viiper/go.mod` and MinGW-w64 GCC | VIIPER C ABI via cgo; a Go installation alone is insufficient                                                                           |
| Visual Studio 2022 or 2026 C++ tools, Windows SDK and CMake       | x64 packaged-game bridge, MinHook and native export inspection                                                                          |

The standard output RID is `win-x64`. Most projects target `net10.0-windows`; consumers of WinRT or
WindowsDeviceControl use `net10.0-windows10.0.19041.0`. That suffix is the Windows API target, not a
claim that every product scenario has been tested on every OS with that API level.

## Managed compilation without native staging

For source analysis or managed compilation on a clean checkout, use the explicit compile-only escape
hatch:

```powershell
dotnet restore WSGM.slnx -p:SkipNativeArtifacts=true -m:1
dotnet build WSGM.slnx -c Release --no-restore --warnaserror -p:SkipNativeArtifacts=true -m:1
```

`Directory.Build.props` enables Windows targeting for restore/analysis on non-Windows hosts. Windows
desktop execution, native MSVC builds, service behavior and hardware verification still require
Windows. `SkipNativeArtifacts` omits staged native content; it does not produce a complete runtime
or validate a plugin/controller/installer scenario. Both managed-library submodules and the linked
Steam Input binding sources must still be present.

Compilation includes project XML documentation according to the shared/project properties. Public
API documentation belongs beside declarations and must explain contracts, results and ownership.
Generated `bin`/`obj` files are output, not source. Repository Markdown provides the architecture,
workflow and failure semantics that an individual member's XML comment cannot show.

## Build the native components and application

On a Windows development machine with the prerequisites installed, the following builds stage native
components without executing their automated test suites:

```powershell
.\eng\build-steam-input-lease.ps1
.\eng\build-viiper.ps1
.\eng\build-uwp-bridge.ps1
dotnet restore WSGM.slnx -m:1
dotnet build WSGM.slnx -c Release --no-restore --warnaserror -m:1
```

| Build script                  | Generated staging/output                                                                                                                           |
| ----------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------- |
| `build-steam-input-lease.ps1` | `src/WSGM/Native/SteamInputLease`: gate, FFI DLL and notices; canonical managed bindings remain linked from the submodule                          |
| `build-viiper.ps1`            | `src/WSGM/Native/Viiper`: `libviiper.dll`, checked C header, source-revision stamp and notices                                                     |
| `build-uwp-bridge.ps1`        | `src/WSGM.PackagedLaunch/Native/UwpBridge`: x64 `WsgmUwpBridge.dll` and MinHook license; intermediate CMake build under `publish/uwp-bridge-build` |

Do not populate these directories manually or infer a dependency's version from leftover output.
`-Validate` on Steam Input runs Clippy, Rust tests and export checks; on VIIPER it runs `go vet` and
the relevant Go tests. The bridge's `-Validate` checks required exports. A VIIPER change requires
its own validation: the main verification gate does not build or validate that library.

Opening Settings and `--overlay-test` are the allowed UI inspection modes. Shell/boot takeover,
service install, plugin install/removal, Device Lab hardware actions and
[`eng/dev-deploy.ps1`](../eng/dev-deploy.ps1) alter the live machine and require explicit direction.
The `tools/WsgmLibTest` scripts connect to live Steam and are attended tools, not compilation
checks. Before connecting any live CEF tool, inspect Steam's file logs and confirm that Steam and
Big Picture have fully started. A listening debug port, process, window or target list does not
establish that. Early attachment can hang Steam's entire UI and require force-closing Steam; use the
Steam CEF debugging skill's readiness procedure before opening a connection.

## Setup and plugin outputs

After the required manual validation and when a complete setup artifact is wanted:

```powershell
.\build.ps1
```

This script restores locked Node tools, checks the generated Steam asset, validates/builds Steam
Input and VIIPER, restores for `win-x64`, publishes WSGM and both launch wrappers plus the logon
service, builds the native UWP bridge, stages pinned controller installers, builds the curated
plugin bundle, stages the pinned WebView2 installer, validates component contents, embeds the ZIP
and publishes setup. It clears the root `publish` contents before rebuilding that output.

The deliverables are `publish/WSGM-Setup-<version>.exe` and `publish/bundle.json`. `publish/App`,
`Payload`, `Packages`, `SetupOut` and `payload.zip` are staging products. The setup's `App` includes
a second copy of the main executable named `WSGM.ShellAnchor.exe`, so ending `WSGM.exe` cannot also
end its independent Explorer recovery owner. `Controller` carries VIIPER and controller installers;
setup installs that component only for an applicable device role. `MediaRuntime` carries WebView2.

`build.ps1 -BundleFrom <directory>` consumes prebuilt `Packages` and `bundle.json`. Prepare that
directory outside the root `publish` tree, which this script clears. The release workflow builds
community plugins in a separate job without secrets, then passes its bundle into the final build.
[`plugins/curated`](../plugins/curated) is the source of package origin and pinned community
commits; [`eng/build-bundle.ps1`](../eng/build-bundle.ps1) is the producer of `.wsgmpkg` files and
the bundle manifest. An optional local bundle build is:

```powershell
.\eng\build-bundle.ps1 -OutputRoot artifacts/local-bundle -SkipCommunity -SkipTools
```

`-SkipTools` omits Device Lab from the output; it still builds Device Lab temporarily to validate
device packages. `-SkipCommunity` omits community source builds. Package assembly and inspection do
not activate hardware. Community builds execute third-party MSBuild code and belong in an
environment without secrets, as required by the engineering guide.

Controller and WebView2 payloads are acquired and checked at build time against their lock files,
including digest/signature requirements. PawnIO acquisition belongs to Device Lab publishing; a
portable tester build requires its bundled installer, while the ordinary installer-tree path can
warn and report it unavailable offline. RTSS is different: setup downloads its pinned build only
when the selected answers enable RTSS and no installation is registered.

`src/WSGM/WSGM.csproj` supplies the release `Version`; setup reads it. The shared
[`eng/wsgm-revision.targets`](../eng/wsgm-revision.targets) stamps the application's/setup's fourth
assembly/file version component from the reachable Git commit count, with 0 when unavailable or an
explicit `-p:WsgmRevision=N` override. Full Git history is needed for a comparable count. Test
builds do not consume a new release version. A local request to publish a release means building
this setup; GitHub tags/releases are a separate action.

## Verification and tools

The repository requires manual testing first: compile, deploy only when directed, and let the
maintainer exercise the change. Automated tests, coverage, `steam-assets:claims` and test-bearing
gates wait until the maintainer reports manual testing, unless requested sooner. Formatting,
compilation, generated-asset drift and guidance checks may run before that report.

```powershell
npm run format
npm run steam-assets:check
.\eng\check-agent-guidance.ps1
```

After manual testing, run the narrow test for the changed area and then the canonical gate for an
initial implementation:

```powershell
dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Area"
.\eng\verify.ps1
```

Replace `Area` with the actual test class or namespace. The gate checks formatting, asset drift and
ownership, guidance links, tracked PowerShell syntax, forbidden live-data paths, versions/pins,
Steam Input, Rider cleanup/style/analyzers, warning-clean Release compilation, all solution test
projects, main-suite coverage, and compilation of tracked tool projects. It does not execute
deployment or hardware scripts as smoke tests. `-SkipPrettier` skips only Prettier; `-Fix` can
rewrite source. Inspect any formatter diff. Rider's Full Cleanup and `WSGM.slnx.DotSettings` are the
C# layout authority; Prettier does not own `external` or C# source.

Tool projects intentionally stay outside the solution. Their own guides explain supported modes:

- [`tools/OverlayPreview`](../tools/OverlayPreview/README.md): render/inspect overlay UI fixtures.
- [`tools/PerfLab`](../tools/PerfLab/README.md): performance capture support; retained evidence is
  in [`docs/perf`](perf/README.md).
- [`tools/LiveBackdropSample`](../tools/LiveBackdropSample/README.md): standalone backdrop sample.
- [`tools/HcDeviceExtract`](../tools/HcDeviceExtract): reference-data extraction used by
  `eng/extract-hc-devices.ps1`.

The `examples/SteamCefPlugin` example belongs to the plugin authoring workflow. `_ref` contains
local reference material and `_plan` contains working decisions/history; neither defines build
inputs or the active project list. Dated hardware evidence remains a record of that particular run,
not a substitute for testing the current build.
