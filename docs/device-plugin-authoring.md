# Device plugin authoring

WSGM loads one administrator-installed device plugin through the public `WSGM.Device.Sdk` assembly.
A plugin owns exact device detection, hardware transports, semantic capabilities, input and output,
diagnostics, and restoration. It supplies no UI code and cannot use WSGM internals.

## 1. Create and implement

Use Device Lab's Plugin Developer flow, or scaffold from a confirmed capture:

```powershell
wsgm-device scaffold --from <capture.wsgmcap> --out-dir <new-plugin-directory>
```

The generated project contains a minimal `IDevicePlugin`, a six-field `plugin.wsgm.json`, an
explicit x64 target, the GPL-3.0-or-later source header and license, and its package layout. The
generated project keeps `LICENSE.txt` beside both build and publish output. Inside a WSGM checkout
it references the SDK project; installed Device Lab instead writes an explicit reference to the
exact `WSGM.Device.Sdk.dll` shipped beside the tool. That path is validated before any scaffold file
is written, so no undefined MSBuild property is emitted. Keep the reference on that exact API if the
scaffold is moved to another machine. Implement exact detection first, then add direct device-owned
services. Publish only semantic descriptors, state, input, and diagnostics through
`IPluginHostAdapter`; vendor addresses, packets, handles, and recovery state stay inside the plugin.

Every hardware write must recheck current identity and bounds, serialize its real transport, read
back when the hardware supports it, and restore the captured original state on failure or stop.
Unknown identity or ranges fail closed. A partial device is valid: publish the working capabilities
and a specific unavailable reason for the others.

## 2. Build and run safely

Build the plugin for 64-bit Windows and place the entry assembly plus package-local dependencies
beside the manifest:

```powershell
dotnet build <plugin.csproj> -c Release -r win-x64
wsgm-device validate <package-directory>
wsgm-device test sample
wsgm-device test plugin <package-directory> --from <inventory.json>
```

`validate` is offline and does not load plugin code. It rejects a missing, malformed, or non-x64
entry assembly and enforces the same entry, file, per-file, and aggregate-byte package budgets used
by protected staging. `test plugin` loads the package and runs exact detection only. Use a temporary
state directory for the attended hardware path:

```powershell
wsgm-device test hardware <package-directory> --from <inventory.json> --state-dir <new-directory> --action haptic
```

Use `--action controller` for the bounded controller-management check. A semantic capability write
uses `--action capability --capability <id> --value <semantic-value>` plus optional
`--instance <id>`. Each run accepts exactly one explicit action.

The hardware command refuses redirected input or output, CI, `--yes`, a nonmatching device, an
active WSGM Device Integration owner, a process without elevation, or a reused state directory. It
requires a local confirmation immediately before activation. The state-path, owner, elevation,
attendance, CI, and confirmation checks complete before Device Lab loads the plugin assembly or runs
its constructor. Exact detection runs only after those checks and must match before activation.
Device Lab gives startup and cleanup 15-second cancellation budgets; the plugin must honor
cancellation so the in-process developer run can return. Never automate this command.

## 3. Test and diagnose

Use `WSGM.Device.Sdk.Testing.TestPluginHostAdapter` for deterministic lifecycle,
partial-availability, publication, cancellation, and cleanup tests without touching hardware. Keep
transport parsing and decision logic behind fakes; reserve real WMI/HID/controller checks for the
attended Device Lab path. The supporting read-only commands require their explicit inputs:

```powershell
wsgm-device doctor --out-dir <diagnostics-directory>
wsgm-device inventory --out-dir <inventory-directory> --shareable
wsgm-device inspect <capture.wsgmcap>
wsgm-device compare <first.wsgmcap> <second.wsgmcap>
wsgm-device correlate <capture.wsgmcap> --action <id> --sources <id,id>
```

They collect or inspect machine and capture evidence without granting mutation authority.

A plugin should leave enough bounded diagnostics to explain detection, service availability,
readback, restoration, and dependency failures. Do not log personal identifiers, raw secrets, or
unbounded device payloads.

## 4. Pack

Create the deterministic distribution archive only after offline validation passes:

```powershell
wsgm-device pack <package-directory> --out <plugin.wsgmpkg>
```

The archive contains only the validated package files in deterministic path and timestamp order.
Device Lab pins the source tree and regular-file handles before validation, then writes the archive
from those same handles so a link or file replacement cannot substitute different bytes after a
clean report. License and attribution notices required by shipped code or glyph assets remain
package files.

## 5. Install or replace the one slot

Close the WSGM shell and DeviceHost. A package installed through this command becomes trusted
hardware code and may later inherit WSGM's elevation, so inspect and validate the exact directory
you intend to install. Expand the `.wsgmpkg` into a fresh directory, then ask the installed WSGM
binary to replace the protected slot:

```powershell
$expanded = '<new-expanded-directory>'
if (Test-Path -LiteralPath $expanded) { throw 'The expansion directory must be new.' }
New-Item -ItemType Directory -Path $expanded | Out-Null
tar -xf <plugin.wsgmpkg> -C $expanded
if ($LASTEXITCODE -ne 0) { throw 'Package extraction failed.' }
& "$env:LOCALAPPDATA\WSGM\bin\WSGM.exe" --install-device-plugin $expanded
if ($LASTEXITCODE -ne 0) { throw 'WSGM rejected the plugin installation; inspect wsgm.log.' }
```

The maintenance command requests elevation, copies into the fixed nondiscoverable `.staging`
sibling, revalidates its bounded paths, manifest/API version, and x64 entry point, atomically
reserves the machine-wide WSGM/Device Lab hardware owner, refuses any running DeviceHost process,
and replaces `C:\Program Files\WSGM\DevicePlugins\installed`. It repairs an ambiguous old slot by
replacing the whole slot and never leaves a release and developer plugin side by side. The source
directory must not overlap the installed slot, `.staging`, `.previous`, `.installed.previous`, or an
abandoned `.installed.staging-*` namespace in either direction and must not traverse a link/reparse
point; these checks run before recovery reconciliation. Enable Device Integration in WSGM Settings
only after the install succeeds. Runtime discovery/host creation and maintenance use the same
machine-wide package-slot gate; the owner reservation is held from the host recheck through every
filesystem operation, closing the startup race without loading plugin code in maintenance. To return
to core-only WSGM, run the maintenance removal; it also requests elevation and applies the same gate
and owner/DeviceHost refusal:

```powershell
& "$env:LOCALAPPDATA\WSGM\bin\WSGM.exe" --remove-device-plugin
if ($LASTEXITCODE -ne 0) { throw 'WSGM rejected the plugin removal; inspect wsgm.log.' }
```
