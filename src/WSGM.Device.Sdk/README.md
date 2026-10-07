# WSGM.Device.Sdk

The contract a **WSGM Device Plugin** links against. WSGM rebuilds SteamOS Game Mode on Windows 11
handhelds, and a device plugin is what teaches it a specific machine: its power limits, fans,
lighting, controller, motion sensors and OEM buttons.

```
dotnet add package WSGM.Device.Sdk
```

It targets `net10.0-windows` and is a deliberate zero-dependency leaf. This assembly is the
type-identity boundary between the host and your plugin, so anything it referenced, your plugin
would inherit.

The common identity and lifecycle contracts shared with non-device plugins live in a separate MIT
assembly at [WSGM.Plugin.Sdk](../WSGM.Plugin.Sdk/README.md). WSGM's device coordinator owns the
device runtime directly; the common host admits independent integrations and refuses the Device
category. The current exact compatibility level is **Device API 12** (`DeviceApi.Version`), separate
from the NuGet package version. Its history is in the
[reference](docs/reference.md#version-history). Current contracts include active-time deadlines,
paired power limits, profile scope and apply timing, shared service/recovery helpers, and
allocation-free controller, motion and haptic frames.

## What a plugin is

One class implementing `IDevicePlugin`, packaged with a `plugin.wsgm.json` manifest. WSGM loads
exactly one installed package into a collectible `AssemblyLoadContext` and drives it through a
single lifecycle:

```csharp
using WSGM.Device.Sdk.Plugin;

public sealed class MyHandheldPlugin : IDevicePlugin
{
    public string PackageId => "com.example.myhandheld";

    // Identify the exact machine without acquiring anything mutable.
    public ValueTask<PluginDetectionResult> DetectAsync(
        PluginDetectionContext context, CancellationToken cancellationToken) => ...;

    // One device cycle begins. Acquire transports here; unwind them if cancelled.
    public ValueTask<PluginStartResult> StartAsync(
        PluginStartContext context, CancellationToken cancellationToken) => ...;

    // Apply one semantic command, after revalidating identity and current state yourself.
    public ValueTask<CapabilityCommandResult> ExecuteCommandAsync(
        CapabilityCommand command, CancellationToken cancellationToken) => ...;
}
```

You publish capabilities, things like a TDP limit, a fan curve or a lighting zone, and WSGM renders
them, routes user intent back as a `CapabilityCommand`, and shows whatever you report. It never
talks to your hardware.

Descriptors may declare `CapabilityProminence` (`Normal`, `Primary`, `Compact`) and an optional
`LayoutPair` companion in the same section and category. These are presentation hints: WSGM owns
layout, focus and editor choice. Validate the complete set with `CapabilityLayout.TryValidate` and
advance descriptor generation when hints change. Pairing for presentation never requests a hardware
write. See the [reference](docs/reference.md#capabilitydescriptor).

A sustained-power descriptor can declare `PowerPresets`: named shortcuts combining its watt limit,
the device's slow watt limit, a `DevicePowerMode` and optional AC and battery `ScenarioMode`
choices. WSGM applies the scenario first and requires every write to finish successfully, verified
or unverified, before continuing. WSGM owns application and Windows access; the plugin supplies the
device-specific numbers. Validate the whole descriptor set with `DevicePowerPreset.TryValidate`.
Presets do not enforce values after selection.

For paired power control at runtime, a sustained descriptor may name `PairedPowerLimitId`. The host
then decides both limits: every write to either one carries the other in
`CapabilityCommand.PairedPowerLimitWatts`, with the sustained limit never above the boost limit. The
plugin checks the pair with `DevicePowerPair.TryResolve` and writes both values as given. Validate
declarations with `DevicePowerPair.TryValidate`.

`DeviceSections` provides shared Power, RGB, Controller and Info pages. Reference their IDs from
capabilities, optionally add categories with record copies, and declare your own sections for other
subjects. WSGM can contribute its own controls to the shared pages.

## What is in here

| Namespace       | What it carries                                                                |
| --------------- | ------------------------------------------------------------------------------ |
| `Plugin`        | `IDevicePlugin`, the host adapter, and `PluginTrace` logging                   |
| `Lifecycle`     | cycle start and stop, controller handoff, deadlines                            |
| `Capabilities`  | capability descriptors, commands, states and refusal reasons                   |
| `Input`         | canonical controller state, haptic output, OEM controls, device identity       |
| `Identity`      | the device identity snapshot a plugin matches against                          |
| `Glyphs`        | glyph packages: profiles, layout, import and asset validation                  |
| `Settings`      | plugin-declared settings sections that WSGM renders and validates              |
| `Services`      | service states and walks, the command gate and the recovery journal            |
| `Packaging`     | `plugin.wsgm.json` reading, validation and limits                              |
| `Serialization` | the source-generated JSON context for all of the above                         |
| `Testing`       | `TestPluginHostAdapter`, to record publications without the production router  |
| `Windows`       | HID discovery, Sensor API motion, keyboard hooks, reconnect and timing helpers |

## The rules this contract enforces

These are not style preferences. They are why the surface looks the way it does.

**Your plugin runs with WSGM's authority.** It is an assembly the host loads in-process, so the SDK
does not pretend to sandbox you. Asset handling checks integrity (identifiers, confined paths,
bounds, well-formedness) and passes your bytes through unchanged. Do not mistake validation for
isolation.

**Report the truth, including uncertainty.** A transport-accepted write is successful even without
matching readback: return `AppliedUnverified`, publish the written value as `Observed`, and leave
`ReadbackValue` absent. An independent matching read upgrades it to `AppliedVerified`. A timeout or
interruption whose hardware effect is unknown is `TimedOut` or `Indeterminate`; never retry that
write automatically. Missing readback alone is not an uncertain write.

**Revalidate on every command:** identity, firmware, range and current state, every time. The
helpers are shaped to make that the easy path, and a capability that cannot revalidate is awkward to
express on purpose.

**Keep prerequisite policy outside the SDK.** No SDK path installs drivers or companion software. A
missing prerequisite must produce a truthful unavailable reason. Any device-specific recovery
belongs to the package; for example, the Claw package can repair its OEM event provider only when
MSI's own installed support files are present.

**Controllers, Steam and HidHide are not yours.** Canonical input goes out, canonical output comes
back. Plugins never call the virtual-controller backend, never own WSGM's Steam Input lease and
never touch HidHide.

[`docs/reference.md`](docs/reference.md) describes every one of those types, the rules the host
applies to what a plugin publishes, and every limit, in the order a plugin runs into them.

## Authoring, packaging and testing

[**Device Lab**](https://github.com/KillerPixelCrew/WSGM/tree/master/src/WSGM.DeviceLab) is the
companion tool. It inventories the machine you are targeting, scaffolds a plugin from a captured
device, validates and packs the package, and runs attended hardware tests. `TestPluginHostAdapter`
in this repository covers the unattended half: lifecycle, capability and manifest behaviour with no
hardware attached.

## Versioning

Pre-1.0. It is published so plugin authors can pin an exact version, not because the contract is
frozen. Breaking changes move the minor version and get called out in the release notes. Every
public member is documented and the build fails on one that is not, so IntelliSense is the
reference.

## Licence

MIT, see `LICENSE`.

WSGM itself is GPL-3.0-or-later. This contract is deliberately permissive so a plugin can carry
whatever licence its author wants, including a closed-source vendor or OEM plugin.
