using System.ComponentModel;
using System.Text.Json;
using WSGM.Core;
using WSGM.Interop;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.Tests.Fakes;

namespace WSGM.Tests.Overlay;

public sealed class PowerSchemeSelectionTests
{
    private static readonly Guid First = Guid.NewGuid();
    private static readonly Guid Second = Guid.NewGuid();

    [Fact]
    public async Task AcceptedSelectionPublishesWithoutReadingWindowsAgain()
    {
        FakeApi api = new();
        var qam = new NativeQamPowerProfileService(new PowerSchemes(api), _ => { });
        await qam.ReadAsync();
        var reads = api.ActiveReads;
        api.ReadFailure = true;

        Assert.True((await qam.SetPowerProfileAsync(Second.ToString("D"), CancellationToken.None)).Succeeded);
        Assert.Equal(Second.ToString("D"), (await qam.ReadAsync())!.Current);
        Assert.Equal(reads, api.ActiveReads);
    }

    [Fact]
    public void CorePowerProfilesKeepDeviceAvailableWhenThePluginIsOff()
    {
        var navigation = new OverlayNavigation();
        navigation.SetDeviceVisible(false, true);
        Assert.Contains(OverlayDestination.Device, navigation.VisibleDestinations);
        navigation.Select(OverlayDestination.Device);
        navigation.SetDeviceVisible(true, true);
        navigation.SetDeviceVisible(false, true);
        Assert.Equal(OverlayDestination.Device, navigation.Destination);
    }

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
        Assert.All(state.Options, option => Assert.Contains("Duplicate localized name (", option.Label));
        await qam.SetPowerProfileAsync("not-a-guid", CancellationToken.None);
        await qam.SetPowerProfileAsync(Guid.NewGuid().ToString("D"), CancellationToken.None);
        Assert.Equal(0, api.Writes);
        await qam.SetPowerProfileAsync(Second.ToString("D"), CancellationToken.None);
        Assert.Equal(Second, saved);
        Assert.Equal(Second.ToString("D"), (await qam.ReadAsync())!.Current);
        api.Reject = true;
        await qam.SetPowerProfileAsync(First.ToString("D"), CancellationToken.None);
        await qam.SetPowerProfileAsync(First.ToString("D"), CancellationToken.None);
        Assert.Equal(3, api.Writes);
        Assert.Equal(Second, saved);
        Assert.Contains("failed", (await qam.ReadAsync())!.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SteamDropdownCachesSchemesUntilASelectionOrBoundedRefresh()
    {
        FakeApi api = new();
        ManualTimeProvider time = new(DateTimeOffset.UnixEpoch);
        var qam = new NativeQamPowerProfileService(new PowerSchemes(api), _ => { }, time);

        await qam.ReadAsync();
        await qam.ReadAsync();
        Assert.Equal(1, api.Enumerations);
        Assert.Equal(2, api.ActiveReads);

        await qam.SetPowerProfileAsync(Second.ToString("D"), CancellationToken.None);
        await qam.ReadAsync();
        Assert.Equal(1, api.Enumerations); // Publish the accepted selection before refreshing Windows.
        await qam.ReadAsync();
        Assert.Equal(2, api.Enumerations);

        await qam.ReadAsync();
        time.Advance(TimeSpan.FromMinutes(1));
        await qam.ReadAsync();
        Assert.Equal(3, api.Enumerations);
        Assert.Equal(5, api.ActiveReads);
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
        var json = JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig);
        Assert.Equal(Second,
            JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig)!.LastSelectedPowerSchemeId);
        Assert.DoesNotContain("Duplicate localized name", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedWriteAllowsAnotherExplicitSelectionWithoutAutomaticRetry()
    {
        FakeApi api = new();
        using var model = new PowerSchemeSelection(new PowerSchemes(api), _ => throw new Exception("Unexpected save"));
        await model.RefreshAsync();
        api.Reject = true;
        await model.ApplyAsync(Second);
        Assert.Equal(First, model.ActiveId);
        Assert.True(model.CanSelect);
        Assert.Contains("Choose again", model.Status, StringComparison.Ordinal);
        await model.ApplyAsync(Second);
        Assert.Equal(2, api.Writes);
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
        using var preview = new PowerSchemeSelection(new PowerSchemes(api), _ => { }, true);
        await preview.RefreshAsync();
        await preview.ApplyAsync(Second);
        Assert.False(preview.CanSelect);
        using var model = new PowerSchemeSelection(new PowerSchemes(api), _ => { });
        await model.RefreshAsync();
        await model.ApplyAsync(Guid.NewGuid());
        Assert.Equal(0, api.Writes);
    }

    [Fact]
    public async Task OnePowerProfileIsNothingToChooseInEitherPicker()
    {
        FakeApi api = new() { Single = true };
        using var model = new PowerSchemeSelection(new PowerSchemes(api), _ => { });
        Assert.False(model.Offered);
        await model.RefreshAsync();
        Assert.True(model.CanSelect);
        Assert.False(model.Offered, "one plan hides the overlay section");
        api.Single = false;
        await model.RefreshAsync();
        Assert.True(model.Offered);

        var qam = new NativeQamPowerProfileService(new PowerSchemes(new FakeApi { Single = true }), _ => { });
        var state = await qam.ReadAsync();
        Assert.False(state!.Available);
        Assert.Empty(state.Options);
        Assert.Equal(First.ToString("D"), state.Current);
        Assert.Contains("one power profile", state.StatusText, StringComparison.Ordinal);
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
        api.BeforeRead = () =>
        {
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(10));
        };
        var model = new PowerSchemeSelection(new PowerSchemes(api), _ => { });
        var notifications = 0;
        model.Changed += () => notifications++;
        var pending = model.RefreshAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await model.RefreshAsync();
            await model.ApplyAsync(Second);
            Assert.True(model.Busy);
            model.Dispose();
        }
        finally
        {
            release.Set();
        }

        await pending;
        Assert.Equal(1, notifications);
        Assert.Empty(model.Schemes);
        Assert.Equal(0, api.Writes);
    }

    private sealed class FakeApi : IPowerSchemeApi
    {
        internal Guid Active { get; set; } = First;
        internal int ActiveReads { get; private set; }
        internal int Enumerations { get; private set; }
        internal int Writes { get; private set; }
        internal bool Reject { get; set; }
        internal bool Empty { get; set; }
        internal bool Single { get; set; }
        internal bool ReadFailure { get; set; }
        internal Action? BeforeRead { get; set; }

        public uint ReadSetting(Guid scheme, Guid subgroup, Guid setting, bool onBattery)
        {
            throw new InvalidOperationException("Unexpected power setting read");
        }

        public void WriteSetting(Guid scheme, Guid subgroup, Guid setting, bool onBattery, uint value)
        {
            throw new InvalidOperationException("Unexpected power setting write");
        }

        public Guid? Enumerate(uint index)
        {
            if (index == 0)
            {
                Enumerations++;
            }

            return Empty ? null : index switch { 0 => First, 1 when !Single => Second, _ => null };
        }

        public string ReadName(Guid id)
        {
            return "Duplicate localized name";
        }

        public Guid ReadActive()
        {
            ActiveReads++;
            BeforeRead?.Invoke();
            return ReadFailure ? throw new Win32Exception(5) : Active;
        }

        public void SetActive(Guid id)
        {
            Writes++;
            if (Reject)
            {
                throw new Win32Exception(5);
            }

            Active = id;
        }
    }
}
