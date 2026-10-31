using System.Text.Json.Serialization;

namespace WSGM.Core;

/// <summary>Host-only card serialization; never source-link this parser into the launch helper.</summary>
[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(StorageLibraryManifest))]
internal sealed partial class StorageLibraryJsonContext : JsonSerializerContext;
