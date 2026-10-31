using System.Text.Json.Serialization;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Serializes the Game Library's state, its editors' documents and its command answers.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GameLibraryState))]
[JsonSerializable(typeof(RomSourceConfig))]
[JsonSerializable(typeof(ManualShortcutConfig))]
[JsonSerializable(typeof(EmulatorSnapshot))]
[JsonSerializable(typeof(EmulatorPageState))]
[JsonSerializable(typeof(EmulatorProgressState))]
[JsonSerializable(typeof(GameLibraryDetails))]
[JsonSerializable(typeof(GameLibraryRouteAnswer))]
[JsonSerializable(typeof(GameLibraryAcknowledgeAnswer))]
[JsonSerializable(typeof(GameLibraryOptionsAnswer))]
[JsonSerializable(typeof(GameLibraryMatchesAnswer))]
internal sealed partial class GameLibraryJsonContext : JsonSerializerContext;
