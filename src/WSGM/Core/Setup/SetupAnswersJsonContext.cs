using System.Text.Json.Serialization;

namespace WSGM.Core;

/// <summary>Source-generated JSON metadata for <see cref="SetupAnswers" />.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(SetupAnswers))]
internal sealed partial class SetupAnswersJsonContext : JsonSerializerContext;
