using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WindowsDeviceControl;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Interop;
using WSGM.Overlay;

namespace WSGM.UiTests;

public sealed class HybridCoreViewTests
{
    [AvaloniaFact]
    public async Task AHybridMachineOffersTheCorePreferenceOnTheDevicePowerPage()
    {
        using FakeDevice device = new();
        using UiFixture fixture = new();
        OverlayWindow window = fixture.Overlay();
        using HybridCoreSelection selection = new(new HybridCores(new FakeApi()));
        window.AttachHybridCores(selection);
        await selection.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        OpenDevicePowerPage(window, device);

        var section = UiFixture.Named<Expander>(window, "DeviceHybridCores");
        Assert.True(section.IsVisible);

        // Collapsed by default, like the energy plan beside it, so its content is only realized
        // once the section is opened.
        section.IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
        var modes = UiFixture.Named<HybridCoreView>(window, "DeviceHybridCoreHost")
            .GetVisualDescendants().OfType<ComboBox>().First();
        Assert.Equal(5, modes.ItemsSource!.Cast<object>().Count());
        Assert.Equal("Automatic", ((HybridCoreOption)modes.SelectedItem!).Name);
    }

    [AvaloniaFact]
    public async Task AMachineWithOneKindOfCoreNeverShowsTheSection()
    {
        using FakeDevice device = new();
        using UiFixture fixture = new();
        OverlayWindow window = fixture.Overlay();
        using HybridCoreSelection selection = new(
            new HybridCores(new FakeApi { Classes = [new(0, 8, 16)] }));
        window.AttachHybridCores(selection);
        await selection.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        OpenDevicePowerPage(window, device);

        Assert.False(UiFixture.Named<Control>(window, "DeviceHybridCores").IsVisible);
    }

    [AvaloniaFact]
    public async Task ApplyingFromTheOverlayWritesBothPowerSourcesAndConfirmsTheResult()
    {
        using UiFixture fixture = new();
        OverlayWindow window = fixture.Overlay();
        FakeApi api = new();
        using HybridCoreSelection selection = new(new HybridCores(api));
        window.AttachHybridCores(selection);
        await selection.RefreshAsync();

        await selection.ApplyAsync(HybridCoreMode.PreferPerformance);

        Assert.Equal(HybridCoreMode.PreferPerformance, selection.Status.OnAc);
        Assert.Equal(HybridCoreMode.PreferPerformance, selection.Status.OnBattery);
        Assert.Equal(1, api.Refreshes);
    }

    [AvaloniaFact]
    public async Task APreviewOverlayReadsTheStateButRefusesToChangeIt()
    {
        using UiFixture fixture = new();
        OverlayWindow window = fixture.Overlay();
        FakeApi api = new();
        using HybridCoreSelection selection = new(new HybridCores(api), readOnly: true);
        window.AttachHybridCores(selection);
        await selection.RefreshAsync();

        await selection.ApplyAsync(HybridCoreMode.EfficiencyOnly);

        Assert.True(selection.Status.Supported);
        Assert.False(selection.CanSelect);
        Assert.Equal(0, api.Refreshes);
        Assert.Contains("Preview only", selection.Detail, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task AFailedWriteLeavesTheReasonOnTheSurfaceRatherThanThrowingIntoTheUi()
    {
        using UiFixture fixture = new();
        OverlayWindow window = fixture.Overlay();
        using HybridCoreSelection selection = new(new HybridCores(new FakeApi { IgnoreWrites = true }));
        window.AttachHybridCores(selection);
        await selection.RefreshAsync();

        await selection.ApplyAsync(HybridCoreMode.PerformanceOnly);

        Assert.False(selection.Busy);
        Assert.Contains("was not applied", selection.Detail, StringComparison.Ordinal);
    }

    private static void OpenDevicePowerPage(OverlayWindow window, FakeDevice device)
    {
        window.AttachDeviceBridge(device);
        UiFixture.Click(window, UiFixture.Tab(window, 3));
        UiFixture.Click(window, window.GetVisualDescendants().OfType<CardButton>()
            .Single(card => card.IsEffectivelyVisible && card.Title == "Power"));
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class FakeApi : IHybridCoreApi
    {
        private static readonly Guid Scheme = new("381b4222-f694-41f0-9685-ff5bb260df2e");

        internal IReadOnlyList<HybridCoreClass> Classes { get; set; } = [new(0, 4, 4), new(1, 4, 4)];

        internal bool IgnoreWrites { get; set; }

        internal int Refreshes { get; private set; }

        private readonly Dictionary<bool, HybridCoreState> _states = new()
        {
            [false] = new(0, HybridSchedulingPolicy.Automatic, HybridSchedulingPolicy.Automatic),
            [true] = new(0, HybridSchedulingPolicy.Automatic, HybridSchedulingPolicy.Automatic),
        };

        private static readonly HybridSchedulingPolicy[] Policies =
        [
            HybridSchedulingPolicy.AllProcessors,
            HybridSchedulingPolicy.PerformantProcessors,
            HybridSchedulingPolicy.PreferPerformantProcessors,
            HybridSchedulingPolicy.EfficientProcessors,
            HybridSchedulingPolicy.PreferEfficientProcessors,
            HybridSchedulingPolicy.Automatic,
        ];

        public Guid ReadActiveScheme() => Scheme;

        public HybridCoreSupport Query(Guid scheme) => new(Classes, true, [0], Policies, Policies);

        public HybridCoreState Read(Guid scheme, bool onBattery) => _states[onBattery];

        public void Write(Guid scheme, bool onBattery, HybridCoreState state)
        {
            if (!IgnoreWrites) { _states[onBattery] = state; }
        }

        public void RefreshActiveScheme() => Refreshes++;
    }
}
