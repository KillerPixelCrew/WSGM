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
assembly at [ `../WSGM.Plugin.Sdk`](../WSGM.Plugin.Sdk/README.md). The resident common host admits
this Device runtime through a compatibility adapter. Device API 5 adds presentation hints and
retains value types for high-rate controller and motion samples so publishing a frame does not
allocate contract objects.

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
choices. WSGM applies the scenario first and confirms every target. WSGM owns application and
Windows access; the plugin supplies the device-specific numbers. Validate the whole descriptor set
with `DevicePowerPreset.TryValidate`. Presets do not enforce values after selection.

For paired power control at runtime, a sustained descriptor may name `PairedPowerLimitId`.
`CapabilityCommand.ApplyPowerPair` then asks the plugin to coordinate and verify both limits. The
plugin defines the relationship between them and may use different ranges and steps; the primary
range defines the valid targets, and the host never assumes sustained and boost are equal. Validate
declarations with `DevicePowerPair.TryValidate`. Ordinary commands still address one limit.

`DeviceSections` provides shared Power, RGB, Controller and Info pages. Reference their IDs from
capabilities, optionally add categories with record copies, and declare your own sections for other
subjects. WSGM can contribute its own controls to the shared pages.

## What is in here

| Namespace       | What it carries                                                          |
| --------------- | ------------------------------------------------------------------------ |
| `Plugin`        | `IDevicePlugin`, the host adapter, and `PluginTrace` logging             |
| `Lifecycle`     | cycle start and stop, controller handoff, diagnostics snapshots          |
| `Capabilities`  | capability descriptors, commands, states and refusal reasons             |
| `Input`         | canonical controller state, haptic output, OEM controls, device identity |
| `Identity`      | the device identity snapshot a plugin matches against                    |
| `Glyphs`        | glyph packages: profiles, layout, import and asset validation            |
| `Settings`      | plugin-declared settings sections that WSGM renders and validates        |
| `Packaging`     | `plugin.wsgm.json` reading, validation and limits                        |
| `Serialization` | the source-generated JSON context for all of the above                   |
| `Testing`       | `PluginTestKit`, to drive your plugin's lifecycle without WSGM           |

## The rules this contract enforces

These are not style preferences. They are why the surface looks the way it does.

**Your plugin runs with WSGM's authority.** It is an assembly the host loads in-process, so the SDK
does not pretend to sandbox you. Asset handling checks integrity (identifiers, confined paths,
bounds, well-formedness) and passes your bytes through unchanged. Do not mistake validation for
isolation.

**Report the truth, including uncertainty.** A capability command returns what actually happened. A
write whose result you could not confirm is reported as uncertain, never retried silently, and never
reported as success. WSGM shows the user exactly what you tell it.

**Revalidate on every command:** identity, firmware, range and current state, every time. The
helpers are shaped to make that the easy path, and a capability that cannot revalidate is awkward to
express on purpose.

**Declare dependencies, never install or repair them.** No SDK path copies a driver, edits a
registry key, restarts a device or runs an installer. A missing prerequisite makes one feature
unavailable and says so.

**Controllers, Steam and HidHide are not yours.** Canonical input goes out, canonical output comes
back. Plugins never call the virtual-controller backend, never own WSGM's Steam Input lease and
never touch HidHide.

[`docs/reference.md`](docs/reference.md) describes every one of those types, the rules the host
applies to what a plugin publishes, and every limit, in the order a plugin runs into them.

## Authoring, packaging and testing

[**Device Lab**](https://github.com/KillerPixelCrew/WSGM/tree/master/src/WSGM.DeviceLab) is the
companion tool. It inventories the machine you are targeting, scaffolds a plugin from a captured
device, validates and packs the package, and runs attended hardware tests. `PluginTestKit` in this
repository covers the unattended half: lifecycle, capability and manifest behaviour with no hardware
attached.

## Versioning

Pre-1.0. It is published so plugin authors can pin an exact version, not because the contract is
frozen. Breaking changes move the minor version and get called out in the release notes. Every
public member is documented and the build fails on one that is not, so IntelliSense is the
reference.

## Licence

MIT, see `LICENSE`.

WSGM itself is GPL-3.0-or-later. This contract is deliberately permissive so a plugin can carry
whatever licence its author wants, including a closed-source vendor or OEM plugin.
