# WSGM Device Plugin — Handheld Companion

A [WSGM](https://github.com/KillerPixelCrew/WSGM) device plugin that drives the machine through a
running [Handheld Companion](https://github.com/Valkirie/HandheldCompanion) instead of touching
hardware itself. HC stays the hardware owner; WSGM's overlay and Steam's Quick Access Menu get HC's
power limits, fan behaviour, lighting, charge limit and telemetry as capabilities.

The two talk over a local named pipe. The wire is **HC's own data structures as JSON** with a schema
HC generates at runtime, not a method API, so a new HC version needs no change on the HC side; see
[`docs/ipc-protocol.md`](docs/ipc-protocol.md). The HC side lives on the
[`IPC` branch of KillerPixelCrew/HandheldCompanion](https://github.com/KillerPixelCrew/HandheldCompanion/tree/IPC)
and is meant to be upstreamed.

**Status: design.** The protocol document is the deliverable so far; the plugin sources are not
written yet. The source and test scaffolds now live in WSGM alongside the
[reference plugin](https://github.com/KillerPixelCrew/WSGM/tree/master/src/WSGM.Device.Msi.Claw8A2Vm).

## Building

Run these commands from the WSGM repository root. The SDK is shared source under
`src/WSGM.Device.Sdk`; device projects are built and reviewed together.

```powershell
dotnet build src/WSGM.Device.HandheldCompanion/WSGM.Device.HandheldCompanion.csproj
dotnet test tests/WSGM.Device.HandheldCompanion.Tests/WSGM.Device.HandheldCompanion.Tests.csproj
```

The empty assembly builds, but it is not an installable plugin. The packaging script is retained for
future implementation; it cannot produce a working package until the entry type exists. The scaffold
is not included in the installer.

## Licence

MIT. See `LICENSE`. The plugin links only the MIT SDK and ships no Handheld Companion code: Handheld
Companion is CC BY-NC-SA 4.0 and is only ever talked to over the pipe.
