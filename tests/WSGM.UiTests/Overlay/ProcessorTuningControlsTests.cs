using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.UiTests.Infrastructure;
using WSGM.UiTests.Visual;

namespace WSGM.UiTests.Overlay;

public sealed class ProcessorTuningControlsTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownValuesAndResetResultStayReadableWithoutSendingCommands(bool restartRequired)
    {
        using UiFixture fixture = new();
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(16) };
        foreach (var (id, label, min, max, unit) in new[]
                 {
                     ("processor.cpu-curve-all", "CPU all-core curve offset", -24, 12, CapabilityUnit.None),
                     ("processor.gpu-clock", "GPU clock", 100, 2900, CapabilityUnit.Megahertz)
                 })
        {
            body.Children.Add(new DeviceSliderRow(id, label, string.Empty, min, max, 1, unit,
                null, true, _ => throw new InvalidOperationException("Unexpected hardware write")));
        }

        var descriptor = new CapabilityDescriptor
        {
            CapabilityId = "processor.reset", Role = CapabilityRole.GenericAction,
            Display = new CapabilityDisplay
            {
                Key = DisplayKey.Custom, CustomLabel = "Reset processor tuning",
                Description = "Restores supported values; a Windows restart may be needed for firmware defaults."
            },
            ValueKind = CapabilityValueKind.None, SupportsAction = true, Persistence = CapabilityPersistence.Volatile
        };
        var view = new DeviceCapabilityView(descriptor, new CapabilityProjection
        {
            State = new CapabilityState
            {
                CapabilityId = descriptor.CapabilityId, Available = true, Quality = HardwareStateQuality.Unknown,
                Reason = restartRequired
                    ? new CapabilityReason(CapabilityReasonCode.Unsupported,
                        "Saved processor overrides cleared. Restart Windows to restore firmware defaults for: GPU clock, CPU temperature limit.")
                    : null
            }
        }, null);
        var reset = CapabilityRowProjection.ToOverlayCapability(view, new HashSet<string>(),
            FanCurvePresetTable.HandheldCompanion);
        body.Children.Add(new DeviceCapabilityControl(reset, descriptor.CapabilityId,
            (_, _) => throw new InvalidOperationException("Unexpected hardware write"),
            _ => throw new InvalidOperationException("Unexpected reset"), null));
        var window = new Window { Width = 720, Height = 520, Content = body };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, window.GetVisualDescendants().OfType<TextBlock>().Count(text => text.Text == "Unknown"));
        Assert.Equal(2, window.GetVisualDescendants().OfType<TextBlock>().Count(text => text.Text == "Adjust to set a value"));
        Assert.All(window.GetVisualDescendants().OfType<Slider>(), slider => Assert.True(slider.IsEnabled));
        // An offset rests at 0 rather than its strongest setting; a range without 0 rests at its lowest.
        Assert.Equal(new double[] { 0, 100 }, window.GetVisualDescendants().OfType<Slider>().Select(slider => slider.Value));
        VisualBaseline.Verify(window, restartRequired ? "processor-reset-restart" : "processor-unknown-values");
        window.Close();
    }
}
