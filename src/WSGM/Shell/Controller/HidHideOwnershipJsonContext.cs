using System.Text.Json.Serialization;

namespace WSGM.Shell;

[JsonSerializable(typeof(HidHideOwnershipLedger))]
internal sealed partial class HidHideOwnershipJsonContext : JsonSerializerContext;
