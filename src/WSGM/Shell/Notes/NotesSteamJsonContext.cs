using System.Text.Json.Serialization;

namespace WSGM.Shell;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(NotesSteamState))]
[JsonSerializable(typeof(NotesSteamRequest))]
[JsonSerializable(typeof(NotesSteamImage))]
[JsonSerializable(typeof(NotesSteamCreated))]
internal sealed partial class NotesSteamJsonContext : JsonSerializerContext;
