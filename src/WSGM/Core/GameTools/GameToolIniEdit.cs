namespace WSGM.Core;

/// <summary>One requested INI edit.</summary>
/// <param name="Section">The section, matched case-insensitively ignoring whitespace and quotes.</param>
/// <param name="Key">The key inside that section.</param>
/// <param name="Value">The raw value to write.</param>
/// <param name="CreateMissing">Creates only a descriptor-declared native key when requested.</param>
internal sealed record GameToolIniEdit(string Section, string Key, string Value, bool CreateMissing = false);
