using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using WSGM.Device.Sdk.Settings;
using WSGM.Themes;

namespace WSGM.Core;

/// <summary>Source-generated JSON metadata for the persisted <see cref="AppConfig" /> contract.</summary>
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(CefConfig))]
[JsonSerializable(typeof(SplashConfig))]
[JsonSerializable(typeof(LaunchWrapperConfig))]
[JsonSerializable(typeof(CardLibraryConfig))]
[JsonSerializable(typeof(CustomTabConfig))]
[JsonSerializable(typeof(NativeTabConfig))]
[JsonSerializable(typeof(DeviceIntegrationConfig))]
[JsonSerializable(typeof(PluginSettingsScope))]
// The SDK's own manifest types, so the cached declaration keeps one shape owned by the SDK rather
// than a WSGM-side copy that would have to be kept in step with it.
[JsonSerializable(typeof(PluginSettingsManifest))]
[JsonSerializable(typeof(PluginSettingValue))]
[JsonSerializable(typeof(DeviceAuthoredProfile))]
[JsonSerializable(typeof(AuthoredCurvePoint))]
[JsonSerializable(typeof(PerformanceConfig))]
[JsonSerializable(typeof(ArtworkConfig))]
[JsonSerializable(typeof(GameLibraryConfig))]
[JsonSerializable(typeof(ProfileConfig))]
[JsonSerializable(typeof(GameProfile))]
[JsonSerializable(typeof(ProfileValues))]
[JsonSerializable(typeof(ProfileDeviceValue))]
[JsonSerializable(typeof(FilterNode))]
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
internal partial class ConfigJsonContext : JsonSerializerContext;
