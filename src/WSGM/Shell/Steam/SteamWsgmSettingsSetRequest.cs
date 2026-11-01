using System.Text.Json;

namespace WSGM.Shell;

/// <summary>One change: the row's key and its new value.</summary>
/// <param name="Key">Published row identity.</param>
/// <param name="Value">Cloned new JSON value; row semantics are validated by the backend.</param>
internal readonly record struct SteamWsgmSettingsSetRequest(string Key, JsonElement Value);
