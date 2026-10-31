using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.Testing;
using WSGM.UiTests.Fakes;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Overlay;

public sealed class HybridCoreRowTests
{
    private static ChoiceRowSelection Selection(FakeHybridCoreApi api, bool readOnly = false)
    {
        NativeQamHybridCoreService cores = new(api.Owner());
        return new ChoiceRowSelection("system.hybrid-cores.choice", "Processor core preference", cores.ReadAsync,
            cores.SetHybridCoresAsync, readOnly);
    }

    [AvaloniaFact]
    public async Task AHybridMachineOffersTheCorePreferenceOnTheDevicePowerPage()
    {
        using FakeDevice device = new();
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        using var selection = Selection(new FakeHybridCoreApi { HeterogeneousPolicies = [0] });
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
        var modes = UiFixture.Named<ChoiceRowView>(window, "DeviceHybridCoreHost")
            .GetVisualDescendants().OfType<ComboBox>().First();
        Assert.Equal(5, modes.ItemsSource!.Cast<object>().Count());
        Assert.Equal(HybridCores.IdFor(HybridCoreMode.Automatic), selection.State.Current);
        Assert.True(modes.SelectedIndex >= 0);
    }

    [AvaloniaFact]
    public async Task AMachineWithOneKindOfCoreNeverShowsTheSection()
    {
        using FakeDevice device = new();
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        using var selection = Selection(new FakeHybridCoreApi
            { HeterogeneousPolicies = [0], Classes = [new HybridCoreClass(0, 8, 16)] });
        window.AttachHybridCores(selection);
        await selection.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        OpenDevicePowerPage(window, device);

        Assert.False(UiFixture.Named<Control>(window, "DeviceHybridCores").IsVisible);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyingFromTheOverlayPublishesTheWrittenPreferenceForBothPowerSources(bool ignoreWrites)
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        FakeHybridCoreApi api = new() { HeterogeneousPolicies = [0], IgnoreWrites = ignoreWrites };
        using var selection = Selection(api);
        window.AttachHybridCores(selection);
        await selection.RefreshAsync();
        api.Calls.Clear();

        await selection.SelectAsync(HybridCores.IdFor(HybridCoreMode.PreferPerformance));

        Assert.Equal(HybridCores.IdFor(HybridCoreMode.PreferPerformance), selection.State.Current);
        Assert.Equal(1, api.Refreshes);
        Assert.Equal(["read", "read", "write", "write", "refresh"], api.Calls);
        var stored = ignoreWrites
            ? HybridSchedulingPolicy.Automatic
            : HybridSchedulingPolicy.PreferPerformantProcessors;
        Assert.All(api.States.Values, state =>
        {
            Assert.Equal(stored, state.Threads);
            Assert.Equal(stored, state.ShortThreads);
        });
    }

    [AvaloniaFact]
    public async Task APreviewOverlayReadsTheStateButRefusesToChangeIt()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        FakeHybridCoreApi api = new() { HeterogeneousPolicies = [0] };
        using var selection = Selection(api, true);
        window.AttachHybridCores(selection);
        await selection.RefreshAsync();

        await selection.SelectAsync(HybridCores.IdFor(HybridCoreMode.EfficiencyOnly));

        Assert.True(selection.State.Available);
        Assert.False(selection.CanSelect);
        Assert.Equal(0, api.Refreshes);
        Assert.Contains("Preview only", selection.Status, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task AFailedWriteLeavesTheReasonOnTheSurfaceRatherThanThrowingIntoTheUi()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        var api = new FakeHybridCoreApi
        {
            HeterogeneousPolicies = [0], NextWriteFailure = new InvalidOperationException("Synthetic write refusal")
        };
        using var selection = Selection(api);
        window.AttachHybridCores(selection);
        await selection.RefreshAsync();

        await selection.SelectAsync(HybridCores.IdFor(HybridCoreMode.PerformanceOnly));

        Assert.False(selection.Busy);
        Assert.Contains("Synthetic write refusal", selection.Status, StringComparison.Ordinal);
        Assert.Equal(HybridCores.IdFor(HybridCoreMode.Automatic), selection.State.Current);
        Assert.Equal(0, api.Refreshes);
    }

    private static void OpenDevicePowerPage(OverlayWindow window, FakeDevice device)
    {
        window.AttachDeviceBridge(device);
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        UiFixture.Click(window, UiFixture.Named<StackPanel>(window, "SectionRail").Children.OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == "Power"));
        Dispatcher.UIThread.RunJobs();
    }
}
