# Retained shared contracts

WSGM.Plugin.Sdk is the common-plugin SDK. Hardware detection, device lifecycle, controller and
motion production, OEM input, haptics and firmware protocols belong to LibHandheld. The SDK contains
no `IDevicePlugin`, device host adapter, recovery journal, motion/filter service, keyboard hook or
device settings-manifest pipeline. Device Lab uses public LibHandheld contracts and owns its lab HID
helpers.

The retained `Shared` source has concrete consumers:

| Source                                                 | Current consumer                                                                    |
| ------------------------------------------------------ | ----------------------------------------------------------------------------------- |
| `Capabilities/`                                        | Common capability plugins, IR payloads and WSGM's descriptor controls/profiles.     |
| `Glyphs/`, `Serialization/GlyphJsonContext.cs`         | Hash-pinned physical glyph import, package validation and WSGM/Steam presentation.  |
| `Packaging/PluginPackageLayout.cs`                     | Common archive safety and WSGM's in-memory package loader.                          |
| `Packaging/ManifestRules.cs`, `ManifestLimits.cs`      | Pure common manifest identity, entry-point, version and document validation.        |
| `Settings/SettingSectionKey.cs`                        | Titles of capability sections; it declares no preference schema or values.          |
| `Plugin/DeviceTraceLevel.cs`                           | Public common-plugin host diagnostic severity; no ambient sink exists.              |
| `Lifecycle/Deadline.cs`, `ActiveClock.cs`              | Bounded common-plugin operations and WSGM service waits.                            |
| `Lifecycle/DeviceLifecycle.cs`, `ControllerHandoff.cs` | Current WSGM lifecycle/controller state enums pending their host-owned replacement. |

These types retain their current namespaces where real callers still need their assembly identity.
There is no separate Device SDK assembly or compatibility-forwarding layer. Read the source for each
member contract and [the common reference](reference.md) for plugin lifecycle, settings and actions.
Hardware-free tests cover these retained boundaries. Hardware and target-client acceptance remain
separate from source review and compilation.
