using LibHandheld.Contracts;
using WSGM.Core;
using WSGM.Settings;
using WSGM.Testing;

namespace WSGM.Tests.Settings;

public sealed class SettingsInventoryTests
{
    private static readonly HandheldDefinition Claw = new("msi.claw8", "msi-claw", "MSI Claw", "msi-claw");

    [Fact]
    public async Task OpeningDoesNotReadHardwareUntilTheInjectedWorkerStartsAndRepeatedCallsShareOneRead()
    {
        var config = new AppConfig { DeviceIntegration = new DeviceIntegrationConfig { Enabled = false } };
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<SettingsInventory>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var services = SettingsTestServices.Inert(config) with
        {
            ReadInventory = () =>
            {
                Interlocked.Increment(ref calls);
                started.TrySetResult(Thread.CurrentThread.IsThreadPoolThread);
                return finish.Task.GetAwaiter().GetResult();
            }
        };
        var model = new SettingsViewModel(config, services);
        Assert.Equal(0, calls);
        Assert.Empty(model.GraphicsDrivers);

        var work = model.StartInventoryDiscoveryAsync();
        Assert.True(await started.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(work, model.StartInventoryDiscoveryAsync());
        Assert.False(work.IsCompleted);
        finish.SetResult(new SettingsInventory(Claw, [BuiltinGpuDrivers.All[0]]));
        await work;

        Assert.False(model.DeviceIntegrationEnabled);
        Assert.True(model.DeviceProfilesAvailable);
        Assert.Equal("wsgm.gpu.intel", Assert.Single(model.GraphicsDrivers).Capture().PluginId);
        model.AddDeviceProfile("thermal.fan-curve");
        Assert.Single(model.DeviceProfiles);
        await model.StartInventoryDiscoveryAsync();
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ClosingDuringNativeDiscoveryDiscardsTheLateResult()
    {
        var config = new AppConfig();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<SettingsInventory>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new SettingsViewModel(config, SettingsTestServices.Inert(config) with
        {
            ReadInventory = () =>
            {
                started.SetResult();
                return finish.Task.GetAwaiter().GetResult();
            }
        });
        var work = model.StartInventoryDiscoveryAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        model.StopInventoryDiscovery();
        finish.SetResult(new SettingsInventory(Claw, [BuiltinGpuDrivers.All[0]]));
        await work;

        Assert.False(model.DeviceProfilesAvailable);
        Assert.Empty(model.GraphicsDrivers);
        await model.StartInventoryDiscoveryAsync();
        Assert.False(model.DeviceProfilesAvailable);
    }

    [Fact]
    public async Task ClosingAfterTheWorkerReturnsStillDiscardsQueuedUiPublication()
    {
        var config = new AppConfig();
        var publication = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new SettingsViewModel(config, SettingsTestServices.Inert(config) with
        {
            ReadInventory = () => new SettingsInventory(Claw, [BuiltinGpuDrivers.All[0]]),
            PostToUi = publish => publication.SetResult(publish)
        });
        var work = model.StartInventoryDiscoveryAsync();
        var publish = await publication.Task.WaitAsync(TimeSpan.FromSeconds(5));
        model.StopInventoryDiscovery();
        publish();
        await work;

        Assert.False(model.DeviceProfilesAvailable);
        Assert.Empty(model.GraphicsDrivers);
    }

    [Fact]
    public async Task NativeDiscoveryFailureKeepsSettingsUsableAndReportsThroughTheInjectedServices()
    {
        var config = new AppConfig();
        var reports = new List<string>();
        var model = new SettingsViewModel(config, SettingsTestServices.Inert(config, reports) with
        {
            ReadInventory = () => throw new InvalidOperationException("device unavailable")
        });
        await model.StartInventoryDiscoveryAsync();

        Assert.Contains("device unavailable", model.DeviceProfilesEmptyReason);
        Assert.Contains("Settings machine inventory failed.", reports);
        model.StartupDelayMs = 500;
        Assert.Equal(500, model.CaptureSaveRequest().Values.StartupDelayMs);
    }

    [Fact]
    public async Task ExistingExactMetadataKeepsItsOfflineProfileDraftWhenInventoryArrives()
    {
        var config = new AppConfig { DeviceIntegration = new DeviceIntegrationConfig { Enabled = false } };
        var model = new SettingsViewModel(config, SettingsTestServices.Inert(config), Claw);
        model.AddDeviceProfile("thermal.fan-curve");
        var profile = Assert.Single(model.DeviceProfiles);
        profile.Name = "Keep my offline edit";

        await model.StartInventoryDiscoveryAsync();

        Assert.True(model.DeviceProfilesAvailable);
        Assert.Same(profile, Assert.Single(model.DeviceProfiles));
        Assert.Equal("Keep my offline edit", Assert.Single(model.CaptureSaveRequest().DeviceProfiles!).Name);
        Assert.False(model.DeviceIntegrationEnabled);
    }
}
