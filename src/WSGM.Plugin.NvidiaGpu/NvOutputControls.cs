// SPDX-License-Identifier: MIT

using WindowsDeviceControl;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Gpu;

namespace WSGM.Plugin.NvidiaGpu;

internal sealed record NvColorField(
    string Id,
    string Label,
    int Offset,
    bool Byte,
    IReadOnlyList<(uint Value, string Label)> Values)
{
    internal static readonly NvColorField[] All =
    [
        new("format", "Output color format", 8, true,
            [(0, "RGB"), (1, "YCbCr 4:2:2"), (2, "YCbCr 4:4:4"), (3, "YCbCr 4:2:0"), (254, "Default"), (255, "Auto")]),
        new("depth", "Output bit depth", 12, false,
            [(0, "Default"), (1, "6 bpc"), (2, "8 bpc"), (3, "10 bpc"), (4, "12 bpc"), (5, "16 bpc")]),
        new("range", "Output dynamic range", 10, true, [(0, "Full"), (1, "Limited"), (255, "Auto")]),
        new("colorimetry", "Colorimetry", 9, true,
        [
            (0, "RGB"), (1, "BT.601"), (2, "BT.709"), (3, "xvYCC 601"), (4, "xvYCC 709"),
            (5, "sYCC 601"), (6, "Adobe YCC 601"), (7, "Adobe RGB"), (8, "BT.2020 RGB"),
            (9, "BT.2020 YCC"), (10, "BT.2020 constant luminance"), (254, "Default"), (255, "Auto")
        ]),
        new("policy", "Output color selection", 16, false, [(0, "User settings"), (1, "Best quality")]),
        new("desktop-depth", "Desktop color depth", 20, false,
        [
            (0, "Keep current"), (1, "8 bpc"), (2, "10 bpc"), (3, "16-bit float"),
            (4, "16-bit float wide gamut"), (5, "16-bit float HDR")
        ])
    ];

    internal uint Read(byte[] data)
    {
        return Byte ? data[Offset] : NvApi.Number(data, Offset);
    }

    internal byte[] WithValue(byte[] current, uint value)
    {
        var data = (byte[])current.Clone();
        if (Byte)
        {
            data[Offset] = checked((byte)value);
        }
        else
        {
            NvApi.Number(data, Offset, value);
        }

        if (Offset != 16)
        {
            NvApi.Number(data, 16, 0); // An explicit field edit selects USER policy, as ColorControl does.
        }

        if (Offset == 8 && current[Offset] != value)
        {
            data[9] = 255; // The driver selects colorimetry compatible with the new RGB/YCC encoding.
        }

        return data;
    }
}

internal sealed class NvColorControl(
    NvApi api,
    NvOutput output,
    string instance,
    string section,
    NvColorField field,
    IReadOnlyList<(uint Value, string Label)> values)
    : DriverControl(DriverDescriptors.Choice("display.color." + field.Id, instance, field.Label, section,
        CapabilityProfileScope.GlobalOnly, values.Select(value => (NvSettingControl.Encode(value.Value), value.Label))))
{
    internal override CapabilityValue Read()
    {
        return CapabilityValue.Choice(NvSettingControl.Encode(field.Read(api.Color(output.Id, 1))));
    }

    internal override void Write(CapabilityValue value)
    {
        api.RequireOutput(output);
        var data = field.WithValue(api.Color(output.Id, 1), NvSettingControl.Decode(value));
        if (!api.SupportsColor(output.Id, data))
        {
            throw new DriverFailure("This color combination is no longer supported on the current NVIDIA link.");
        }

        api.RequireOutput(output);
        api.Color(output.Id, 2, data);
    }
}

internal sealed class NvDitherControl(
    NvApi api,
    NvOutput output,
    string instance,
    string section,
    int field,
    IReadOnlyList<(uint Value, string Label)> values)
    : DriverControl(DriverDescriptors.Choice("display.dithering." + new[] { "state", "depth", "mode" }[field], instance,
        new[] { "Dithering", "Dithering bit depth", "Dithering mode" }[field], section,
        CapabilityProfileScope.GlobalOnly,
        values.Select(value => (NvSettingControl.Encode(value.Value), value.Label))))
{
    internal override CapabilityValue Read()
    {
        var data = api.Dither(output.Id);
        return CapabilityValue.Choice(
            NvSettingControl.Encode(field switch { 0 => data.State, 1 => data.Bits, _ => data.Mode }));
    }

    internal override void Write(CapabilityValue value)
    {
        api.RequireOutput(output);
        var data = api.Dither(output.Id);
        var requested = NvSettingControl.Decode(value);
        if (field != 0 && data.State != 1)
        {
            throw new DriverFailure("Enable dithering before choosing its bit depth or pattern.");
        }

        if ((field == 1 && (data.BitsCaps & (1u << (int)requested)) == 0)
            || (field == 2 && (data.ModeCaps & (1u << (int)requested)) == 0))
        {
            throw new DriverFailure("This dithering option is no longer supported on the NVIDIA output.");
        }

        api.SetDither(output, field switch
        {
            0 => data with { State = requested },
            1 => data with { Bits = requested },
            _ => data with { Mode = requested }
        });
    }
}

internal sealed class NvOutputModeControl(
    NvApi api,
    NvOutput expected,
    string instance,
    string section,
    IReadOnlyList<(uint Value, string Label)> values)
    : DriverControl(DriverDescriptors.Choice("display.hdr-output-mode", instance, "HDR output mode", section,
        CapabilityProfileScope.GlobalOnly, values.Select(value => (NvSettingControl.Encode(value.Value), value.Label))))
{
    internal override CapabilityValue Read()
    {
        return CapabilityValue.Choice(NvSettingControl.Encode(api.OutputMode(expected.Id)));
    }

    internal override void Write(CapabilityValue value)
    {
        var output = api.RequireOutput(expected);
        var requested = NvSettingControl.Decode(value);
        var capabilities = api.HdrCapabilities(output.Id);
        if (requested == 2 && (capabilities & 0x80) == 0)
        {
            throw new DriverFailure("HDR10+ Gaming is no longer supported by this NVIDIA display.");
        }

        var previous = api.OutputMode(output.Id);
        DisplayTargetIdentity? resetTarget = null;
        if (previous == 2 && requested == 1)
        {
            // ColorControl's HDR10+ -> HDR10 workaround. Capture the exact Windows HDR target first.
            resetTarget = output.Target ?? throw new DriverFailure("The HDR display's Windows identity is ambiguous.");
            if (!DisplayColor.TryReadHdr(resetTarget, out var enabled, out _))
            {
                throw new DriverFailure("The HDR state could not be captured for the HDR10+ transition.");
            }

            if (!enabled)
            {
                resetTarget = null;
            }
        }

        api.RequireOutput(expected);
        api.OutputMode(output.Id, requested);
        if (resetTarget is not null)
        {
            var disabled = DisplayColor.TrySetHdr(resetTarget, false, out var detail);
            // Restore the captured On state even if the Off operation's readback was inconclusive.
            var restored = DisplayColor.TrySetHdr(resetTarget, true, out var restoreDetail);
            if (!disabled || !restored)
            {
                throw new DriverFailure("The HDR10 transition was not confirmed: " + detail + " " + restoreDetail,
                    true);
            }
        }
    }
}

internal sealed class NvGsyncControl(INvProfiles api, string section)
    : DriverControl(DriverDescriptors.Toggle("graphics.gsync", "driver", "G-SYNC enabled", section,
        CapabilityProfileScope.GlobalOnly))
{
    internal override CapabilityValue Read()
    {
        api.Load();
        return CapabilityValue.Boolean(api.Get(api.GlobalProfile(), 0x1094f157).Value == 1);
    }

    internal override void Write(CapabilityValue value)
    {
        api.Load();
        var profile = api.GlobalProfile();
        try
        {
            api.Get(profile, 0x1094f157);
        }
        catch (DriverFailure failure) when (!failure.Lost)
        {
        }

        api.Set(profile, 0x1094f157, value.BooleanValue == true ? 1u : 0u);
        api.Save();
    }
}
