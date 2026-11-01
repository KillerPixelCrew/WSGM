namespace WSGM.Core;

/// <summary>What the UI shows of a component.</summary>
/// <param name="Name">Its label.</param>
/// <param name="Type"><c>color-picker</c> or <c>image-picker</c>.</param>
/// <param name="On">The option it belongs to.</param>
/// <param name="Value">Its current value.</param>
public sealed record ThemeComponentSnapshot(string Name, string Type, string On, string Value);
