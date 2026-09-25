using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;

namespace WSGM.DeviceLab.Wizard;

/// <summary>Motor strengths for one write, each 0 to 100 percent.</summary>
/// <param name="Left">Left (low-frequency, strong) motor channel.</param>
/// <param name="Right">Right (high-frequency, weak) motor channel.</param>
internal sealed record LabRumbleFrame(int Left, int Right)
{
    /// <summary>Both motors off.</summary>
    public static LabRumbleFrame Zero { get; } = new(0, 0);

    /// <summary>Whether both motors are off.</summary>
    [JsonIgnore]
    public bool IsZero => Left == 0 && Right == 0;

    /// <summary>Throws unless both strengths are within 0 to 100 percent.</summary>
    /// <returns>This frame.</returns>
    public LabRumbleFrame Checked()
    {
        if (Left is < 0 or > 100 || Right is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(Left), this, "Motor strength must be 0-100 percent.");
        }

        return this;
    }
}

/// <summary>
///     The exact output report a curated knowledge record gives for rumble, for example
///     <c>0D 0F 00 00 &lt;left%&gt; &lt;right%&gt; FF 00 EB</c>.
/// </summary>
/// <remarks>
///     The first byte is the report ID. Placeholders are <c>&lt;left%&gt;</c> and <c>&lt;right%&gt;</c>
///     (0-100) and <c>&lt;strong&gt;</c> and <c>&lt;weak&gt;</c> (0-255, strong being the left
///     low-frequency motor). Anything else makes the layout unusable, so an unknown format is never
///     guessed at.
/// </remarks>
internal sealed class LabRumbleHidLayout
{
    private const int LeftPercent = -1;
    private const int RightPercent = -2;
    private const int Strong = -3;
    private const int Weak = -4;

    private readonly int[] _slots;

    private LabRumbleHidLayout(int[] slots, string text)
    {
        _slots = slots;
        Text = text;
    }

    /// <summary>The layout as the record gives it.</summary>
    public string Text { get; }

    /// <summary>Report length in bytes, including the report ID.</summary>
    public int Length => _slots.Length;

    /// <summary>Parses a record's report layout.</summary>
    /// <param name="text">The layout text.</param>
    /// <param name="problem">Why it cannot be used, when it cannot.</param>
    /// <returns>The layout, or null.</returns>
    public static LabRumbleHidLayout? TryParse(string? text, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            problem = "The device record gives no rumble report.";
            return null;
        }

        List<int> slots = [];
        var hasLeft = false;
        var hasRight = false;
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int slot;
            switch (token.ToLowerInvariant())
            {
                case "<left%>":
                    slot = LeftPercent;
                    hasLeft = true;
                    break;
                case "<right%>":
                    slot = RightPercent;
                    hasRight = true;
                    break;
                case "<strong>":
                    slot = Strong;
                    hasLeft = true;
                    break;
                case "<weak>":
                    slot = Weak;
                    hasRight = true;
                    break;
                default:
                    if (token.Length != 2
                        || !byte.TryParse(token, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
                            out var value))
                    {
                        problem = $"The rumble report has a part this tool does not understand: {token}.";
                        return null;
                    }

                    slot = value;
                    break;
            }

            slots.Add(slot);
        }

        if (slots.Count < 2 || slots[0] < 0 || !hasLeft || !hasRight)
        {
            problem = "The rumble report must start with a report ID and place both motors.";
            return null;
        }

        return new LabRumbleHidLayout([.. slots], text);
    }

    /// <summary>Builds the report for one frame, padded with zeros to the collection's output length.</summary>
    /// <param name="frame">Motor strengths.</param>
    /// <param name="outputLength">The collection's output report length.</param>
    /// <returns>The report bytes.</returns>
    public byte[] Encode(LabRumbleFrame frame, int outputLength)
    {
        frame.Checked();
        if (outputLength < _slots.Length || outputLength > 1024)
        {
            throw new InvalidOperationException("The collection's output report does not fit the recorded layout.");
        }

        var report = new byte[outputLength];
        for (var i = 0; i < _slots.Length; i++)
        {
            report[i] = _slots[i] switch
            {
                LeftPercent => (byte)frame.Left,
                RightPercent => (byte)frame.Right,
                Strong => Scale(frame.Left),
                Weak => Scale(frame.Right),
                var value => (byte)value
            };
        }

        return report;
    }

    private static byte Scale(int percent)
    {
        return (byte)((percent * 255 + 50) / 100);
    }
}
