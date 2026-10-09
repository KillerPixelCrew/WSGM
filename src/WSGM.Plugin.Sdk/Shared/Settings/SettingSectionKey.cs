using System.Text.Json.Serialization;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Device.Sdk.Settings;

/// <summary>
///     How a settings section is titled on WSGM's own surfaces.
/// </summary>
/// <remarks>
///     The same ownership split as <see cref="DisplayKey" />, one level up: a plugin selects a key WSGM
///     localizes, or supplies plain text through <see cref="Custom" />. Sections use the same
///     text contract as the labels inside them.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<SettingSectionKey>))]
public enum SettingSectionKey
{
    /// <summary>Use the capability section's custom title as plugin text.</summary>
    Custom,

    /// <summary>"General".</summary>
    General,

    /// <summary>"Power".</summary>
    Power,

    /// <summary>"Fans".</summary>
    Fans,

    /// <summary>"Lighting".</summary>
    Lighting,

    /// <summary>"Controller".</summary>
    Controller,

    /// <summary>"Display".</summary>
    Display,

    /// <summary>"Advanced".</summary>
    Advanced,

    /// <summary>"Diagnostics".</summary>
    Diagnostics
}
