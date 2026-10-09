# Shared source migration

All 64 tracked Device SDK C# files moved into this assembly without changing their source
namespaces. No declarations were excluded. The source project, its guides and its standalone
assembly are retired. The shared contract reference preserves member semantics; API 5 requires
rebuilt plugin consumers.

Each path below is relative to the former src/WSGM.Device.Sdk and now lives at Shared/<path>.

- `Capabilities/CapabilityCommand.cs`
- `Capabilities/CapabilityDescriptor.cs`
- `Capabilities/CapabilityDisplay.cs`
- `Capabilities/CapabilityIds.cs`
- `Capabilities/CapabilityLayout.cs`
- `Capabilities/CapabilityProfileScope.cs`
- `Capabilities/CapabilityReason.cs`
- `Capabilities/CapabilityRole.cs`
- `Capabilities/CapabilitySection.cs`
- `Capabilities/CapabilityState.cs`
- `Capabilities/CapabilityValueValidation.cs`
- `Capabilities/CommandResults.cs`
- `Capabilities/DevicePowerPair.cs`
- `Capabilities/DevicePowerPreset.cs`
- `Capabilities/DeviceSections.cs`
- `Capabilities/PlainText.cs`
- `DeviceApi.cs`
- `Glyphs/GlyphAssetValidation.cs`
- `Glyphs/GlyphPackageImporter.cs`
- `Glyphs/GlyphPackageLayout.cs`
- `Glyphs/GlyphProfile.cs`
- `Glyphs/ImmutableGlyphPackageDirectorySource.cs`
- `Identity/DeviceIdentitySnapshot.cs`
- `Identity/HardwareMatchRule.cs`
- `Identity/IdentityText.cs`
- `Input/CanonicalControllerState.cs`
- `Input/HapticOutput.cs`
- `Input/MotionFilters.cs`
- `Input/MotionSampleBuilder.cs`
- `Input/OemButtonLatch.cs`
- `Input/OemControls.cs`
- `Input/PhysicalDeviceIdentity.cs`
- `Lifecycle/ActiveClock.cs`
- `Lifecycle/ControllerHandoff.cs`
- `Lifecycle/Deadline.cs`
- `Lifecycle/DeviceLifecycle.cs`
- `Lifecycle/DeviceWriteBudget.cs`
- `Packaging/ManifestLimits.cs`
- `Packaging/ManifestRules.cs`
- `Packaging/ManifestValidation.cs`
- `Packaging/PluginManifest.cs`
- `Packaging/PluginManifestReader.cs`
- `Packaging/PluginManifestValidator.cs`
- `Packaging/PluginPackageLayout.cs`
- `Plugin/DiagnosticText.cs`
- `Plugin/PluginContracts.cs`
- `Plugin/PluginHostAdapter.cs`
- `Plugin/PluginTrace.cs`
- `Serialization/DeviceJsonContext.cs`
- `Services/DeviceCommandSerializer.cs`
- `Services/DeviceRecoveryJournal.cs`
- `Services/DeviceService.cs`
- `Services/DeviceServiceLifecycle.cs`
- `Settings/PluginSettingSection.cs`
- `Settings/PluginSettingsManifest.cs`
- `Testing/TestPluginHostAdapter.cs`
- `Windows/DeviceReconnect.cs`
- `Windows/HidDevices.Inspect.cs`
- `Windows/HidDevices.cs`
- `Windows/LegacyMotionSensors.Events.cs`
- `Windows/LegacyMotionSensors.cs`
- `Windows/LegacyMotionStream.cs`
- `Windows/LowLevelKeyboardHook.cs`
- `Windows/PrecisionTicker.cs`
