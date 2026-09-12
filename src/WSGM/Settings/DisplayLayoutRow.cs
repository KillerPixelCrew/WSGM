using System.Globalization;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Settings;

/// <summary>One display in a saved layout, rendered as text.
///
/// Read-only on purpose at this stage: the values come from a Snapshot of a real desktop, and the
/// per-field editor that lets a user change them without a monitor attached is its own piece of
/// work. Showing them is what makes the saved layout auditable in the meantime.</summary>
/// <param name="Output">The saved output.</param>
public sealed record DisplayLayoutRow(DisplayLayoutOutput Output)
{
    /// <summary>Whether this row names a display Windows can still resolve.
    ///
    /// A layout migrated from the retired per-monitor profiles carries no usable identity, because
    /// the old shape recorded a GDI name and a registry key rather than a display identity. Those
    /// rows have to be pointed at a real display before Game Mode will apply the layout.</summary>
    public bool NeedsConfirmation => Output.Target.DevicePath.Length == 0
        && Output.Target.EdidManufacturerId is null;

    /// <summary>The display's name, or a prompt when it could not be resolved.</summary>
    public string DisplayName => Output.Target.FriendlyName.Length > 0
        ? Output.Target.FriendlyName
        : "Unnamed display";

    /// <summary>Resolution, refresh rate, position and any scaling or HDR choice.</summary>
    public string Summary
    {
        get
        {
            string mode = string.Create(CultureInfo.InvariantCulture,
                $"{Output.Width}x{Output.Height}");
            string refresh = Output.Refresh.Hertz > 0
                ? string.Create(CultureInfo.InvariantCulture, $" @ {Output.Refresh.Hertz:0.##} Hz")
                : "";
            string position = Output.IsPrimary
                ? ", primary"
                : string.Create(CultureInfo.InvariantCulture, $", at {Output.X},{Output.Y}");
            string dpi = Output.DpiPercent is { } percent
                ? string.Create(CultureInfo.InvariantCulture, $", {percent}% scaling")
                : "";
            string hdr = Output.Hdr is { } enabled ? (enabled ? ", HDR on" : ", HDR off") : "";
            return mode + refresh + position + dpi + hdr;
        }
    }

    /// <summary>The warning shown for a row whose display is not identified.</summary>
    public string ConfirmationText => NeedsConfirmation
        ? "Confirm which display this is. Game Mode will not apply a layout that names it."
        : "";
}

/// <summary>One configured plugin action, rendered as text.</summary>
/// <param name="Step">The saved step.</param>
/// <param name="Available">Whether the named plugin instance is currently running.</param>
public sealed record PluginActionStepRow(PluginActionStep Step, bool Available)
{
    /// <summary>Which plugin instance and action this step names.</summary>
    public string Title => $"{Step.Plugin?.PluginId} / {Step.Plugin?.InstanceId}: {Step.ActionId}";

    /// <summary>The arguments and deadline, plus a note when the plugin is not loaded.</summary>
    public string Summary
    {
        get
        {
            string arguments = Step.Arguments.Count == 0
                ? "no arguments"
                : string.Join(", ", System.Linq.Enumerable.Select(Step.Arguments,
                    pair => $"{pair.Key}={Describe(pair.Value)}"));
            string deadline = string.Create(CultureInfo.InvariantCulture,
                $"{Step.TimeoutSeconds} s");
            return Available
                ? $"{arguments}; {deadline}"
                : $"{arguments}; {deadline}; this plugin is not running";
        }
    }

    private static string Describe(WSGM.Plugin.Sdk.PluginValue value) =>
        value.Text is { Length: > 0 } text ? text
        : value.Number is { } number ? number.ToString(CultureInfo.InvariantCulture)
        : value.Boolean is { } flag ? (flag ? "on" : "off")
        : "";
}
