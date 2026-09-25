# ASUS ROG Ally X device plugin

This is an MIT-licensed Device API 6 scaffold, not working hardware support. The public entry type
implements `IDevicePlugin` and references the shared Device SDK. Detection always returns no match
with an explicit reason until an exact identity predicate can be backed by device evidence. Direct
start and resume calls remain passive, commands are rejected, and haptic frames are dropped. The
scaffold publishes no capabilities, settings, controller samples, physical devices, or OEM controls.
It opens no hardware handles and changes no machine state.

The project also holds unfinished building blocks that nothing calls yet: reference-derived HID
report encoders in `AllyXProtocol.cs` and a read-only ATKACPI status reader in
`AsusReadProtocol.cs`, `AsusReadings.cs` and `WindowsAsusReader.cs`. They carry no claim of live
support and stay unwired until the bring-up work below validates each transport.

## Build

From the repository root:

```powershell
dotnet build src/WSGM.Device.Asus.RogAllyX/WSGM.Device.Asus.RogAllyX.csproj --configuration Release --runtime win-x64
```

The output includes `plugin.wsgm.json`, the entry assembly, this README and `LICENSE.txt`. The
project belongs to `WSGM.slnx`; WSGM does not reference it at compile time and the installer does
not ship it. Building does not install or activate the package.

## Bring-up work

1. The maintainer has a remote tester with the device. Use the
   [portable Ally X Lab](../../tools/AllyXLab/README.md) for attended capture of normalized system
   and board identity, SKU, BIOS, firmware and exact endpoints. No such capture is included.
2. Implement side-effect-free exact detection with positive and negative fixtures, including other
   Ally models and unsupported firmware. Do not substitute a marketing-name match.
3. Establish each transport and its ownership before declaring capabilities. Investigate power,
   fans, charging, lighting, telemetry, OEM buttons, controller input, motion and haptics
   separately; this list makes no claim that any of them is supported.
4. Add identity and generation revalidation, serialized commands, independent readback, rollback,
   original-state restoration and ordered controller release before enabling writes or acquisition.
5. Add hardware-free lifecycle and command tests with the SDK TestKit. Follow the repository's
   manual-testing-first policy before running suites, then perform the relevant validation gate.
   Live hardware actions and installation require explicit maintainer direction.

Use [the SDK reference](../WSGM.Device.Sdk/docs/reference.md) and
[device authoring guide](../../docs/device-plugin-authoring.md) for the contract. For implementation
comparisons, use HHD (Handheld Daemon) as the primary reference, especially for buttons. The
maintainer reports buggy button handling in Handheld Companion; do not use HC button behavior as the
implementation baseline. This device-specific direction takes precedence over the repository's usual
HC-first comparison rule. HC may supply secondary context only.

HHD is an implementation reference, not a Windows transport or proof of behavior on this device.
Record the exact HHD revision and relevant source paths when implementing each behavior, review
licensing before copying code, and distinguish reference-derived behavior from attended validation.
Both reference repositories are now cloned under `_ref`. See [the source comparison](REFERENCE.md)
for pinned revisions, button differences, Windows transport leads and remaining validation. No
reference implementation has been imported into the plugin or validated on hardware.

## Licence

MIT, see `LICENSE`. The package links only the MIT Device SDK.

## Portable tester

[Ally X Lab](../../tools/AllyXLab/README.md) is a separate, self-contained EXE for the remote
tester. It covers guided button/motion capture, rumble calibration, RGB and power/profile/fan
readback. Its experimental hardware workflows do not enable this production plugin. No attended
result has been recorded yet.
