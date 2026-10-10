using System.Text.Json;
using System.Text.Json.Serialization;

namespace WSGM.Core;

/// <summary>Source-generated JSON metadata for the persisted <see cref="AppConfig" /> contract.</summary>
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(GpuDriverConfig))]
[JsonSerializable(typeof(GameModeLaunchRecovery))]
[JsonSerializable(typeof(DisplayGpuPreference))]
[JsonSerializable(typeof(DisplayGpuCapability))]
[JsonSerializable(typeof(CefConfig))]
[JsonSerializable(typeof(SplashConfig))]
[JsonSerializable(typeof(LaunchWrapperConfig))]
[JsonSerializable(typeof(CardLibraryConfig))]
[JsonSerializable(typeof(CustomTabConfig))]
[JsonSerializable(typeof(NativeTabConfig))]
[JsonSerializable(typeof(DeviceIntegrationConfig))]
[JsonSerializable(typeof(DeviceProfileScope))]
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
internal partial class ConfigJsonContext : JsonSerializerContext
{
    /// <summary>Shared JSON metadata using enum repair sentinels; callers must not mutate its serializer options.</summary>
    internal static ConfigJsonContext Tolerant { get; } = CreateTolerant();

    private static ConfigJsonContext CreateTolerant()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new TolerantEnumConverterFactory());
        return new ConfigJsonContext(options);
    }
}
