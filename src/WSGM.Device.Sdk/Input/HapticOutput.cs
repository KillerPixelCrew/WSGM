using System;
using System.Text.Json.Serialization;

namespace WSGM.Device.Sdk.Input;

/// <summary>
/// One output frame travelling from the virtual target back to the physical device.
/// </summary>
/// <remarks>
/// The return channel is separate from input state, and carries its own target generation, because
/// the two travel in opposite directions and a target can be replaced while output is in flight.
/// Applying a frame addressed to a removed target would drive whatever took its slot.
/// </remarks>
public sealed record HapticOutputFrame
{
    /// <summary>Generation of the virtual target that produced this frame.</summary>
    public required long TargetGeneration { get; init; }

    /// <summary>Low-frequency motor intensity, from 0 to 1.</summary>
    public float LowFrequency { get; init; }

    /// <summary>High-frequency motor intensity, from 0 to 1.</summary>
    public float HighFrequency { get; init; }

    /// <summary>Left trigger haptic intensity, from 0 to 1, where the device supports one.</summary>
    public float LeftTrigger { get; init; }

    /// <summary>Right trigger haptic intensity, from 0 to 1, where the device supports one.</summary>
    public float RightTrigger { get; init; }

    /// <summary>When the frame was produced, in UTC.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// A frame that stops all output.
    /// </summary>
    /// <param name="targetGeneration">Generation to stamp on the frame.</param>
    /// <param name="timestamp">When the stop was issued.</param>
    /// <returns>A frame with every channel at zero.</returns>
    /// <remarks>
    /// Rumble always needs an explicit stop path. A motor left running is not a cosmetic bug: it
    /// keeps vibrating after the game closed, the overlay opened, or the plugin was disabled, and
    /// nothing else will turn it off.
    /// </remarks>
    public static HapticOutputFrame Stop(long targetGeneration, DateTimeOffset timestamp) => new()
    {
        TargetGeneration = targetGeneration,
        Timestamp = timestamp,
    };

    /// <summary>Whether this frame commands no output at all.</summary>
    public bool IsSilent =>
        LowFrequency <= 0 && HighFrequency <= 0 && LeftTrigger <= 0 && RightTrigger <= 0;
}

/// <summary>What a device does with an output channel it cannot reproduce.</summary>
/// <remarks>
/// Declared per channel so the plugin's answer is visible rather than implied. A device with two
/// rumble motors and no trigger haptics should say so; silently discarding trigger output looks
/// identical to a broken implementation from the outside.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<OutputChannelSupport>))]
public enum OutputChannelSupport
{
    /// <summary>The device drives this channel directly.</summary>
    Native,

    /// <summary>The channel is not present and its output is discarded.</summary>
    Unsupported,
}

/// <summary>What the physical device can do with output.</summary>
public sealed record HapticCapabilities
{
    /// <summary>Support for the low-frequency motor.</summary>
    public OutputChannelSupport LowFrequency { get; init; } = OutputChannelSupport.Unsupported;

    /// <summary>Support for the high-frequency motor.</summary>
    public OutputChannelSupport HighFrequency { get; init; } = OutputChannelSupport.Unsupported;

    /// <summary>Support for left trigger haptics.</summary>
    public OutputChannelSupport LeftTrigger { get; init; } = OutputChannelSupport.Unsupported;

    /// <summary>Support for right trigger haptics.</summary>
    public OutputChannelSupport RightTrigger { get; init; } = OutputChannelSupport.Unsupported;

    /// <summary>Highest output frame rate the device accepts, in frames per second.</summary>
    public int MaxFramesPerSecond { get; init; } = 60;

    /// <summary>Drops channels the device cannot reproduce, leaving the rest untouched.</summary>
    /// <param name="frame">The frame as produced by the virtual target.</param>
    /// <returns>A frame carrying only channels the device supports.</returns>
    /// <remarks>
    /// Channels are dropped, never redistributed. Folding an unsupported trigger haptic into the
    /// rumble motors would invent an effect the game never asked for, which is the output-side
    /// equivalent of converting gyro into stick movement.
    /// </remarks>
    public HapticOutputFrame Clamp(HapticOutputFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        return frame with
        {
            LowFrequency = LowFrequency is OutputChannelSupport.Native ? frame.LowFrequency : 0,
            HighFrequency = HighFrequency is OutputChannelSupport.Native ? frame.HighFrequency : 0,
            LeftTrigger = LeftTrigger is OutputChannelSupport.Native ? frame.LeftTrigger : 0,
            RightTrigger = RightTrigger is OutputChannelSupport.Native ? frame.RightTrigger : 0,
        };
    }
}
