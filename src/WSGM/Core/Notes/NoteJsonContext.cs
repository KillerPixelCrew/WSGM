using System.Text.Json.Serialization;

namespace WSGM.Core;

/// <summary>Source-generated metadata for every Notes file.</summary>
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NotePage))]
[JsonSerializable(typeof(NoteSettings))]
internal sealed partial class NoteJsonContext : JsonSerializerContext;
