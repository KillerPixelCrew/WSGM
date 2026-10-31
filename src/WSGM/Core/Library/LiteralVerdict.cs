namespace WSGM.Core;

/// <summary>The literal-path verdict for one path of one shortcut.</summary>
/// <param name="State">The published state.</param>
/// <param name="Location">The card or disk to name.</param>
/// <param name="Detail">What to do about it.</param>
/// <param name="Binding">The binding to keep for this path, or null when it has none yet.</param>
public sealed record LiteralVerdict(ContentAvailability State, string Location, string Detail, ShortcutBinding? Binding);
