using System;
using System.Buffers.Binary;
using WSGM.Device.Sdk.Input;

namespace WSGM.Input;

/// <summary>Packs a canonical sample into VIIPER's Xbox 360 input-state wire format.</summary>
internal static class Xbox360Report
{
    /// <summary>Length of one VIIPER Xbox 360 input state.</summary>
    internal const int Length = 20;

    private const uint DPadUp = 0x0001;
    private const uint DPadDown = 0x0002;
    private const uint DPadLeft = 0x0004;
    private const uint DPadRight = 0x0008;
    private const uint Start = 0x0010;
    private const uint Back = 0x0020;
    private const uint LeftThumb = 0x0040;
    private const uint RightThumb = 0x0080;
    private const uint LeftShoulder = 0x0100;
    private const uint RightShoulder = 0x0200;
    private const uint Guide = 0x0400;
    private const uint A = 0x1000;
    private const uint B = 0x2000;
    private const uint X = 0x4000;
    private const uint Y = 0x8000;

    /// <summary>Writes one canonical sample into an Xbox 360 input state.</summary>
    internal static void Write(CanonicalControllerSample sample, Span<byte> destination)
    {
        if (destination.Length != Length)
        {
            throw new ArgumentException(
                $"A VIIPER Xbox 360 input state is exactly {Length} bytes.",
                nameof(destination));
        }

        destination.Clear();
        var buttons = sample.Buttons;
        var wireButtons = ((buttons & CanonicalButtons.DPadUp) != 0 ? DPadUp : 0)
                          | ((buttons & CanonicalButtons.DPadDown) != 0 ? DPadDown : 0)
                          | ((buttons & CanonicalButtons.DPadLeft) != 0 ? DPadLeft : 0)
                          | ((buttons & CanonicalButtons.DPadRight) != 0 ? DPadRight : 0)
                          | ((buttons & CanonicalButtons.Menu) != 0 ? Start : 0)
                          | ((buttons & CanonicalButtons.View) != 0 ? Back : 0)
                          | ((buttons & CanonicalButtons.LeftStick) != 0 ? LeftThumb : 0)
                          | ((buttons & CanonicalButtons.RightStick) != 0 ? RightThumb : 0)
                          | ((buttons & CanonicalButtons.LeftShoulder) != 0 ? LeftShoulder : 0)
                          | ((buttons & CanonicalButtons.RightShoulder) != 0 ? RightShoulder : 0)
                          | ((buttons & CanonicalButtons.Guide) != 0 ? Guide : 0)
                          | ((buttons & CanonicalButtons.A) != 0 ? A : 0)
                          | ((buttons & CanonicalButtons.B) != 0 ? B : 0)
                          | ((buttons & CanonicalButtons.X) != 0 ? X : 0)
                          | ((buttons & CanonicalButtons.Y) != 0 ? Y : 0);

        BinaryPrimitives.WriteUInt32LittleEndian(destination[..4], wireButtons);
        destination[4] = WireScale.Trigger8(sample.LeftTrigger);
        destination[5] = WireScale.Trigger8(sample.RightTrigger);
        BinaryPrimitives.WriteInt16LittleEndian(destination[6..8], WireScale.Axis16(sample.LeftStickX));
        BinaryPrimitives.WriteInt16LittleEndian(destination[8..10], WireScale.Axis16(sample.LeftStickY));
        BinaryPrimitives.WriteInt16LittleEndian(destination[10..12], WireScale.Axis16(sample.RightStickX));
        BinaryPrimitives.WriteInt16LittleEndian(destination[12..14], WireScale.Axis16(sample.RightStickY));
    }



}
