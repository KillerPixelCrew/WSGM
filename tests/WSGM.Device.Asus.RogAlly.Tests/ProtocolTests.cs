// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Numerics;
using WSGM.Device.Sdk.Input;

namespace WSGM.Device.Asus.RogAlly.Tests;

public sealed class ProtocolTests
{
    [Theory]
    [InlineData(new byte[] { 0x5A, 0xA6, 0, 0 }, true, 0xA6)]
    [InlineData(new byte[] { 0x5A, 0x38 }, true, 0x38)]
    [InlineData(new byte[] { 0x5D, 0xA6 }, false, 0)]
    [InlineData(new byte[] { 0x5A, 0x00 }, false, 0)]
    [InlineData(new byte[] { 0x5A }, false, 0)]
    public void VendorReportsDecodeOnlyReport5A(byte[] report, bool expected, byte code)
    {
        Assert.Equal(expected, AllyProtocol.TryReadVendorEvent(report, out var decoded));
        Assert.Equal(code, decoded);
    }

    [Fact]
    public void ClassicLayoutFollowsHhdButtonCodes()
    {
        var ally = AllyModels.ById("rc72la")!;

        Assert.Equal(new AllyVendorAction(OemControlIds.CommandCenter, OemPressKind.Short, CanonicalButtons.Guide),
            AllyModels.VendorAction(ally, 0xA6));
        Assert.Equal(new AllyVendorAction(OemControlIds.ArmouryCrate, OemPressKind.Short, CanonicalButtons.QuickAccess),
            AllyModels.VendorAction(ally, 0x38));
        // HHD merges 0x93 into the QAM; HC's separate Library button is not followed.
        Assert.Equal(AllyModels.VendorAction(ally, 0x38), AllyModels.VendorAction(ally, 0x93));
        Assert.Equal(new AllyVendorAction(OemControlIds.ArmouryCrate, OemPressKind.Long, CanonicalButtons.None),
            AllyModels.VendorAction(ally, 0xA7));
        Assert.Null(AllyModels.VendorAction(ally, 0xA8));
        Assert.Null(AllyModels.VendorAction(ally, 0xA5));
    }

    [Fact]
    public void XboxLayoutNamesItsOwnButtons()
    {
        var xbox = AllyModels.ById("rc73xa")!;

        Assert.Equal(OemControlIds.ArmouryCrate, AllyModels.VendorAction(xbox, 0xA6)!.Value.ControlId);
        Assert.Equal(OemControlIds.Library, AllyModels.VendorAction(xbox, 0x93)!.Value.ControlId);
        Assert.Equal(CanonicalButtons.QuickAccess, xbox.XInputGuide);
        Assert.Equal([AllyModels.VkF21, AllyModels.VkF22], xbox.FrontKeyboardControls.Select(item => item.VirtualKey));
        Assert.Empty(AllyModels.ById("rc71l")!.FrontKeyboardControls);
    }

    [Fact]
    public void ControllerTablesAreSixtyFourByteFeatureReports()
    {
        Assert.All(AllyProtocol.GameModeConfiguration, report =>
        {
            Assert.Equal(64, report.Length);
            Assert.Equal(0x5A, report[0]);
            Assert.Equal(0xD1, report[1]);
        });
        Assert.Equal(AllyProtocol.GameModeConfiguration.Count, AllyProtocol.DefaultConfiguration.Count);
        // Only the M1/M2 table differs between taking and releasing the controller.
        var differing = Enumerable.Range(0, AllyProtocol.GameModeConfiguration.Count)
            .Where(index => !AllyProtocol.GameModeConfiguration[index].SequenceEqual(AllyProtocol.DefaultConfiguration[index]))
            .ToArray();
        Assert.Equal([8], differing);
        Assert.Equal([0x5A, 0xD1, 0x01, 0x01, 0x01], AllyProtocol.GameModeConfiguration[0][..5]);
    }

    [Fact]
    public void RearTablesCarryHhdKeyCodes()
    {
        var keyboard = AllyProtocol.RearKeyboardMapping;
        Assert.Equal([0x5A, 0xD1, 0x02, 0x08, 0x2C], keyboard[..5]);
        Assert.Equal(0x28, keyboard[7]);
        Assert.Equal(0x30, keyboard[29]);

        var factory = AllyProtocol.RearDefaultMapping;
        Assert.Equal(0x8E, factory[7]);
        Assert.Equal(0x8E, factory[18]);
        Assert.Equal(0x8F, factory[29]);
        // HC's copy stops before this fourth entry; HHD's complete block is the one sent.
        Assert.Equal(0x8F, factory[40]);
    }

    [Fact]
    public void CommitFollowsHcValues()
    {
        var commit = AllyProtocol.GameModeConfiguration.Skip(10).ToArray();
        Assert.Equal([0x5A, 0xD1, 0x0F, 0x20], commit[0][..4]);
        Assert.Equal([0x5A, 0xD1, 0x06, 0x02, 0x64, 0x64], commit[1][..6]);
        Assert.Equal([0x5A, 0xD1, 0x04, 0x04, 0x00, 0x64, 0x00, 0x64], commit[2][..8]);
        Assert.Equal([0x5A, 0xD1, 0x05, 0x04, 0x00, 0x64, 0x00, 0x64], commit[3][..8]);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(16, 0)]
    [InlineData(17, 1)]
    [InlineData(50, 2)]
    [InlineData(100, 3)]
    public void BrightnessScalesLikeHc(int percent, byte level)
    {
        Assert.Equal([0x5D, 0xBA, 0xC5, 0xC4, level], AllyProtocol.Brightness(percent));
    }

    [Fact]
    public void ColorMessageMatchesHcAuraMessage()
    {
        var report = AllyProtocol.Color(AuraEffect.Breathing, AuraZone.All, 0x112233, 0x445566, AllyProtocol.SpeedFast);

        Assert.Equal(17, report.Length);
        Assert.Equal(
            [0x5D, 0xB3, 0x00, 0x01, 0x11, 0x22, 0x33, 0xE1, 0x00, 0x01, 0x44, 0x55, 0x66, 0, 0, 0, 0],
            report);
        Assert.Equal(0, AllyProtocol.Color(AuraEffect.Solid, AuraZone.RightStickRight, 0, 0, 0)[9]);
        Assert.Equal(4, AllyProtocol.Color(AuraEffect.Solid, AuraZone.RightStickRight, 0, 0, 0)[2]);
    }

    [Theory]
    [InlineData(0, 0xEB)]
    [InlineData(33, 0xEB)]
    [InlineData(34, 0xF5)]
    [InlineData(66, 0xF5)]
    [InlineData(67, 0xE1)]
    public void SpeedUsesHcBands(int percent, byte expected)
    {
        Assert.Equal(expected, AllyProtocol.Speed(percent));
    }

    [Fact]
    public void SolidLightingWritesEachRingZone()
    {
        var reports = LightingService.Encode(new AllyLightingState(100, AuraEffect.Solid, 50, 0xFF0000, 0x0000FF));

        Assert.True(reports[0].Feature);
        Assert.Equal(0xBA, reports[0].Bytes[1]);
        Assert.Equal([1, 2, 3, 4], reports.Skip(1).Take(4).Select(report => (int)report.Bytes[2]));
        Assert.Equal(0xFF, reports[1].Bytes[4]);
        Assert.Equal(0xFF, reports[4].Bytes[6]);
        Assert.Equal(0xB4, reports[^2].Bytes[1]);
        Assert.Equal(0xB5, reports[^1].Bytes[1]);
        Assert.All(reports.Skip(1), report => Assert.False(report.Feature));
    }

    [Fact]
    public void AcpiRequestsFollowHcCallMethod()
    {
        var status = AsusAcpiProtocol.EncodeStatus(AsusAcpiId.SustainedPower, 0);
        Assert.Equal(16, status.Length);
        Assert.Equal(0x53545344u, BinaryPrimitives.ReadUInt32LittleEndian(status));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(4)));
        Assert.Equal(0x001200A3u, BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(8)));

        var set = AsusAcpiProtocol.EncodeSet(AsusAcpiId.ChargeLimit, 80);
        Assert.Equal(0x53564544u, BinaryPrimitives.ReadUInt32LittleEndian(set));
        Assert.Equal(80u, BinaryPrimitives.ReadUInt32LittleEndian(set.AsSpan(12)));

        var curve = AsusAcpiProtocol.Encode(AsusAcpiProtocol.DeviceSet, AsusAcpiId.CpuFanCurve, new byte[16]);
        Assert.Equal(28, curve.Length);
        Assert.Equal(20u, BinaryPrimitives.ReadUInt32LittleEndian(curve.AsSpan(4)));
    }

    [Theory]
    [InlineData(0x0001000Fu, true, 15)]
    [InlineData(0x00010000u, true, 0)]
    [InlineData(0x0000000Fu, false, 0)]
    [InlineData(0xFFFFFFFEu, false, 0)]
    [InlineData(0x0101000Fu, false, 0)]
    public void ScalarsRequireThePresenceBit(uint raw, bool expected, int value)
    {
        Assert.Equal(expected, AsusAcpiProtocol.TryDecodeScalar(raw, out var decoded));
        Assert.Equal(value, decoded);
    }

    [Fact]
    public void CurveSelectorFollowsHc()
    {
        Assert.Equal(0u, AsusAcpiProtocol.CurveSelector(0));
        Assert.Equal(2u, AsusAcpiProtocol.CurveSelector(1));
        Assert.Equal(1u, AsusAcpiProtocol.CurveSelector(2));
    }

    [Fact]
    public void OnlyReviewedIdsAreWritable()
    {
        Assert.False(AsusAcpiProtocol.IsWritable(AsusAcpiId.CpuFanSpeed));
        Assert.False(AsusAcpiProtocol.IsWritable((AsusAcpiId)0x00090020));
        Assert.True(AsusAcpiProtocol.IsWritable(AsusAcpiId.FastPower));
        Assert.True(AsusAcpiProtocol.IsValidCurve(AllyFanCapability.DefaultCpuCurve));
        Assert.False(AsusAcpiProtocol.IsValidCurve(new byte[16]));
    }

    [Fact]
    public void ClassicMotionMapsLikeHcRogAllyJson()
    {
        var model = AllyModels.ById("rc71l")!;
        var raw = new Vector3(1, 2, 3);

        Assert.Equal(new Vector3(-1, -3, 2), model.Gyro.Apply(raw));
        Assert.Equal(new Vector3(-1, -3, 2), model.Accelerometer.Apply(raw));
    }

    [Fact]
    public void XboxGyroSignsDifferFromItsAccelerometer()
    {
        var model = AllyModels.ById("rc73ya")!;
        var raw = new Vector3(1, 2, 3);

        Assert.Equal(new Vector3(1, 3, -2), model.Gyro.Apply(raw));
        Assert.Equal(new Vector3(-1, -3, 2), model.Accelerometer.Apply(raw));
    }

    [Fact]
    public void XInputButtonsDecodeToCanonical()
    {
        var buttons = (ushort)(AllyControllerCodec.A | AllyControllerCodec.Back | AllyControllerCodec.Guide
                               | AllyControllerCodec.DPadLeft);

        Assert.Equal(CanonicalButtons.A | CanonicalButtons.View | CanonicalButtons.Guide | CanonicalButtons.DPadLeft,
            AllyControllerCodec.Buttons(buttons, CanonicalButtons.Guide));
        Assert.Equal(CanonicalButtons.QuickAccess, AllyControllerCodec.Buttons(AllyControllerCodec.Guide,
            CanonicalButtons.QuickAccess));
        Assert.Equal(-1f, AllyControllerCodec.Axis(short.MinValue));
        Assert.Equal(1f, AllyControllerCodec.Axis(short.MaxValue));
    }

    [Fact]
    public void LatchedOemButtonsExpire()
    {
        var state = new AllyOemButtonState();
        var now = DateTimeOffset.UtcNow;
        state.Latch(CanonicalButtons.Guide, now);
        state.Hold(CanonicalButtons.RearPaddle1, true);

        Assert.Equal(CanonicalButtons.Guide | CanonicalButtons.RearPaddle1, state.Current(now));
        Assert.Equal(CanonicalButtons.RearPaddle1, state.Current(now + AllyOemButtonState.HoldDuration));
        state.Hold(CanonicalButtons.RearPaddle1, false);
        Assert.Equal(CanonicalButtons.None, state.Current(now + AllyOemButtonState.HoldDuration));
    }
}
