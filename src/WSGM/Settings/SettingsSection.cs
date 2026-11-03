using Avalonia.Controls;

namespace WSGM.Settings;

/// <summary>One section of the Settings window.</summary>
/// <param name="Title">The rail label and page heading.</param>
/// <param name="Caption">One line under the heading saying what the section is for; may be empty.</param>
/// <param name="Page">The section's always-alive page.</param>
/// <param name="Parent">The rail entry a nested level belongs to, or null for a rail entry.</param>
internal sealed record SettingsSection(string Title, string Caption, Control Page, int? Parent = null);
