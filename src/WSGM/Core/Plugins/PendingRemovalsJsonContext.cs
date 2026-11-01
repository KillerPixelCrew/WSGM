using System.Text.Json.Serialization;

namespace WSGM.Core;

/// <summary>Source-generated JSON metadata for the pending removal list.</summary>
[JsonSerializable(typeof(string[]))]
internal sealed partial class PendingRemovalsJsonContext : JsonSerializerContext;
