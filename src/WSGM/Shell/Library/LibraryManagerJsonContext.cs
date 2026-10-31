using System.Text.Json.Serialization;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Serializes the two manager states for Steam and reads a custom tab sent back from its editor.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SteamCardManagerState))]
[JsonSerializable(typeof(SteamLibraryTabsState))]
[JsonSerializable(typeof(CustomTabConfig))]
internal sealed partial class LibraryManagerJsonContext : JsonSerializerContext;
