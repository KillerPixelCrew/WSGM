namespace WSGM.Shell;

/// <summary>One option of a host choice row, as a feature projects it before Steam draws it.</summary>
/// <param name="Id">What the owner receives when it is picked.</param>
/// <param name="Label">What the user sees.</param>
/// <param name="Selectable">
///     Whether the user can pick it. An unselectable option names a state the user cannot choose, such
///     as values that match none of the presets: it is listed only where it is the current value.
/// </param>
internal sealed record ChoiceRowOption(string Id, string Label, bool Selectable = true);
