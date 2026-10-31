using System.Text.Json.Serialization;

namespace WSGM.Core;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ImportState))]
[JsonSerializable(typeof(ImportAccountMapping))]
internal sealed partial class ImportStateJsonContext : JsonSerializerContext;
