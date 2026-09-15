using System.ComponentModel;
using System.Text.Json;
using WSGM.Core;
using WSGM.Interop;
using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class PowerSchemeSelectionTests
{
    [Fact]
    public void CorePowerProfilesKeepDeviceAvailableWhenThePluginIsOff()
    {
        var navigation = new OverlayNavigation();
        navigation.SetDeviceVisible(false, coreControlsAvailable: true);
        Assert.Contains(OverlayDestination.Device, navigation.VisibleDestinations);
        navigation.Select(OverlayDestination.Device);
        navigation.SetDeviceVisible(true, coreControlsAvailable: true);
        navigation.SetDeviceVisible(false, coreControlsAvailable: true);
        Assert.Equal(OverlayDestination.Device, navigation.Destination);
    }
    private static readonly Guid First = Guid.NewGuid();
    private static readonly Guid Second = Guid.NewGuid();

    [Fact]
    public async Task SteamDropdownUsesTheSameVerifiedGuidBackend()
    {
        FakeApi api = new();
        Guid? saved = null;
        var qam = new NativeQamPowerProfileService(new PowerSchemes(api), id => saved = id);
        var state = await qam.ReadAsync();
        Assert.True(state!.Available);
        Assert.Equal(First.ToString("D"), state.Current);
        Assert.Equal(2, state.Options.Count);
        await qam.SetPowerProfileAsync("not-a-guid", default);
        await qam.SetPowerProfileAsync(Guid.NewGuid().ToString("D"), default);
        Assert.Equal(0, api.Writes);
        await qam.SetPowerProfileAsync(Second.ToString("D"), default);
        Assert.Equal(Second, saved);
        Assert.Equal(Second.ToString("D"), (await qam.ReadAsync())!.Current);
        api.Reject = true;
        await qam.SetPowerProfileAsync(First.ToString("D"), default);
        await qam.SetPowerProfileAsync(First.ToString("D"), default);
        Assert.Equal(2, api.Writes);
        Assert.Equal(Second, saved);
        Assert.Contains("not confirmed", (await qam.ReadAsync())!.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshReadsWindowsWithoutReapplyingTheSavedReference()
    {
        FakeApi api = new();
        using var model = new PowerSchemeSelection(new PowerSchemes(api), _ => throw new Exception("Unexpected save"));
        await model.RefreshAsync();
        Assert.True(model.CanSelect);
        Assert.Equal(First, model.ActiveId);
        api.Active = Second;
        await model.RefreshAsync();
        Assert.Equal(Second, model.ActiveId);
        Assert.Equal(0, api.Writes);
    }

    [Fact]
    public async Task VerifiedApplyPersistsOnlyTheGuid()
    {
        FakeApi api = new();
        AppConfig config = new();
        using var model = new PowerSchemeSelection(new PowerSchemes(api), id => config.LastSelectedPowerSchemeId = id);
        await model.RefreshAsync();
        await model.ApplyAsync(Second);
        Assert.Equal(Second, model.ActiveId);
        Assert.Equal(1, api.Writes);
        string json = JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig);
        Assert.Equal(Second, JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig)!.LastSelectedPowerSchemeId);
        Assert.DoesNotContain("Duplicate localized name", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UncertainWriteRequiresRefreshAndNeverPersistsOrRetries()
    {
        FakeApi api = new();
        using var model = new PowerSchemeSelection(new PowerSchemes(api), _ => throw new Exception("Unexpected save"));
        await model.RefreshAsync();
        api.Reject = true;
        await model.ApplyAsync(Second);
        Assert.Null(model.ActiveId);
        Assert.False(model.CanSelect);
        Assert.Contains("Refresh", model.Status, StringComparison.Ordinal);
        await model.ApplyAsync(Second);
        Assert.Equal(1, api.Writes);
        await model.RefreshAsync();
        Assert.True(model.CanSelect);
    }

    [Fact]
    public async Task SaveFailureReportsAppliedStateWithoutUndoingWindows()
    {
        FakeApi api = new();
        using var model = new PowerSchemeSelection(new PowerSchemes(api), _ => throw new IOException("Disk full"));
        await model.RefreshAsync();
        await model.ApplyAsync(Second);
        Assert.Equal(Second, model.ActiveId);
        Assert.Contains("could not save", model.Status, StringComparison.Ordinal);
        Assert.Equal(1, api.Writes);
    }

    [Fact]
    public async Task PreviewAndUnknownIdsCannotWrite()
    {
        FakeApi api = new();
        using var preview = new PowerSchemeSelection(new PowerSchemes(api), _ => { }, readOnly: true);
        await preview.RefreshAsync();
        await preview.ApplyAsync(Second);
        Assert.False(preview.CanSelect);
        using var model = new PowerSchemeSelection(new PowerSchemes(api), _ => { });
        await model.RefreshAsync();
        await model.ApplyAsync(Guid.NewGuid());
        Assert.Equal(0, api.Writes);
    }

    [Fact]
    public async Task EmptyOrFailedEnumerationDisablesSelection()
    {
        FakeApi api = new() { Empty = true };
        using var model = new PowerSchemeSelection(new PowerSchemes(api), _ => { });
        await model.RefreshAsync();
        Assert.False(model.CanSelect);
        api.Empty = false;
        api.ReadFailure = true;
        await model.RefreshAsync();
        Assert.Null(model.ActiveId);
        Assert.False(model.CanSelect);
    }

    [Fact]
    public async Task ClosingDuringReadPreventsLatePublicationAndDuplicateOperations()
    {
        FakeApi api = new();
        using ManualResetEventSlim release = new(false);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        api.BeforeRead = () => { entered.TrySetResult(); release.Wait(TimeSpan.FromSeconds(10)); };
        using var model = new PowerSchemeSelection(new PowerSchemes(api), _ => { });
        int notifications = 0;
        model.Changed += () => notifications++;
        Task pending = model.RefreshAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await model.RefreshAsync();
            await model.ApplyAsync(Second);
            Assert.True(model.Busy);
            model.Dispose();
        }
        finally { release.Set(); }
        await pending;
        Assert.Equal(1, notifications);
        Assert.Empty(model.Schemes);
        Assert.Equal(0, api.Writes);
    }

    private sealed class FakeApi : IPowerSchemeApi
    {
        internal Guid Active { get; set; } = First;
        internal int Writes { get; private set; }
        internal bool Reject { get; set; }
        internal bool Empty { get; set; }
        internal bool ReadFailure { get; set; }
        internal Action? BeforeRead { get; set; }
        public Guid? Enumerate(uint index) => Empty ? null : index switch { 0 => First, 1 => Second, _ => null };
        public string ReadName(Guid id) => "Duplicate localized name";
        public Guid ReadActive()
        {
            BeforeRead?.Invoke();
            if (ReadFailure) { throw new Win32Exception(5); }
            return Active;
        }
        public void SetActive(Guid id)
        {
            Writes++;
            if (Reject) { throw new Win32Exception(5); }
            Active = id;
        }
    }
}
