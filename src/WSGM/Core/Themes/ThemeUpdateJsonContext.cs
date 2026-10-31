using System.Text.Json.Serialization;

namespace WSGM.Core;

[JsonSerializable(typeof(ThemeUpdateJournal))]
internal sealed partial class ThemeUpdateJsonContext : JsonSerializerContext;
