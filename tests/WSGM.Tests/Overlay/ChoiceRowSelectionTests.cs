using System.ComponentModel;
using WSGM.Core;
using WSGM.Interop;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.Tests.Fakes;

namespace WSGM.Tests.Overlay;

public sealed class ChoiceRowSelectionTests
{
    private static readonly Guid First = Guid.NewGuid();
    private static readonly Guid Second = Guid.NewGuid();

    private static ChoiceRowSelection Model(NativeQamPowerProfileService profiles, bool readOnly = false)
    {
        return new ChoiceRowSelection("system.power-profile.choice", "Windows power profile", profiles.ReadAsync,
            profiles.SetPowerProfileAsync, readOnly);
    }

    [Fact]
    public async Task OverlayAndQamShareSelectionWithoutAReadBetweenWrites()
    {
        FakeApi api = new();
        var profiles = new NativeQamPowerProfileService(new PowerSchemes(api));
        using var model = Model(profiles);
        await model.RefreshAsync();
        var reads = api.ActiveReads;
        var enumerations = api.Enumerations;
        api.ReadFailure = true;

        await model.SelectAsync(Second.ToString("D"));
        Assert.Equal(Second.ToString("D"), model.State.Current);
        Assert.True(model.CanSelect);
        Assert.True((await profiles.SetPowerProfileAsync(First.ToString("D"), CancellationToken.None)).Succeeded);
        Assert.Equal(2, api.Writes);
        Assert.Equal(reads, api.ActiveReads);
        Assert.Equal(enumerations, api.Enumerations);
        Assert.Equal(First.ToString("D"), (await profiles.ReadAsync())!.Current);
        Assert.Equal(reads, api.ActiveReads);
    }

    [Fact]
    public async Task ClosingOneOverlayDoesNotRetireTheSharedPowerProfileService()
    {
        FakeApi api = new();
        var profiles = new NativeQamPowerProfileService(new PowerSchemes(api));
        var firstSheet = Model(profiles);
        await firstSheet.RefreshAsync();
        firstSheet.Dispose();

        Assert.True((await profiles.SetPowerProfileAsync(Second.ToString("D"), CancellationToken.None)).Succeeded);
        using var nextSheet = Model(profiles);
        await nextSheet.RefreshAsync();
        Assert.Equal(Second.ToString("D"), nextSheet.State.Current);
        await nextSheet.SelectAsync(First.ToString("D"));
        Assert.Equal(First.ToString("D"), nextSheet.State.Current);
        Assert.Equal(2, api.Writes);
    }

    [Fact]
    public async Task AcceptedSelectionPublishesWithoutReadingWindowsAgain()
    {
        FakeApi api = new();
        var qam = new NativeQamPowerProfileService(new PowerSchemes(api));
        await qam.ReadAsync();
        var reads = api.ActiveReads;
        api.ReadFailure = true;

        Assert.True((await qam.SetPowerProfileAsync(Second.ToString("D"), CancellationToken.None)).Succeeded);
        Assert.Equal(Second.ToString("D"), (await qam.ReadAsync())!.Current);
        Assert.Equal(reads, api.ActiveReads);
    }

    [Fact]
    public async Task QamKeepsKnownChoicesWhenTheActiveGuidCannotBeRead()
    {
        FakeApi api = new() { ReadFailure = true };
        var profiles = new NativeQamPowerProfileService(new PowerSchemes(api));
        var state = await profiles.ReadAsync();
        Assert.True(state!.Available);
        Assert.Equal(2, state.Options.Count);
        Assert.Empty(state.Current);
        var reads = api.ActiveReads;
        Assert.True((await profiles.SetPowerProfileAsync(Second.ToString("D"), CancellationToken.None)).Succeeded);
        Assert.Equal(Second.ToString("D"), (await profiles.ReadAsync())!.Current);
        Assert.Equal(reads, api.ActiveReads);
        Assert.Equal(1, api.Writes);
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
        var qam = new NativeQamPowerProfileService(new PowerSchemes(api));
        var state = await qam.ReadAsync();
        Assert.True(state!.Available);
        Assert.Equal(First.ToString("D"), state.Current);
        Assert.Equal(2, state.Options.Count);
        Assert.All(state.Options, option => Assert.Contains("Duplicate localized name (", option.Label));
        await qam.SetPowerProfileAsync("not-a-guid", CancellationToken.None);
        await qam.SetPowerProfileAsync(Guid.NewGuid().ToString("D"), CancellationToken.None);
        Assert.Equal(0, api.Writes);
        await qam.SetPowerProfileAsync(Second.ToString("D"), CancellationToken.None);
        Assert.Equal(Second.ToString("D"), (await qam.ReadAsync())!.Current);
        api.Reject = true;
        await qam.SetPowerProfileAsync(First.ToString("D"), CancellationToken.None);
        await qam.SetPowerProfileAsync(First.ToString("D"), CancellationToken.None);
        Assert.Equal(3, api.Writes);
        Assert.Contains("failed", (await qam.ReadAsync())!.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SteamDropdownCachesSchemesUntilASelectionOrBoundedRefresh()
    {
        FakeApi api = new();
        ManualTimeProvider time = new(DateTimeOffset.UnixEpoch);
        var qam = new NativeQamPowerProfileService(new PowerSchemes(api), time);

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
    public async Task RefreshReadsWindowsWithoutReapplyingASelection()
    {
        FakeApi api = new();
        using var model = Model(new NativeQamPowerProfileService(new PowerSchemes(api)));
        await model.RefreshAsync();
        Assert.True(model.CanSelect);
        Assert.Equal(First.ToString("D"), model.State.Current);
        api.Active = Second;
        await model.RefreshAsync();
        Assert.Equal(Second.ToString("D"), model.State.Current);
        Assert.Equal(0, api.Writes);
    }

    [Fact]
    public async Task FailedWriteAllowsAnotherExplicitSelectionWithoutAutomaticRetry()
    {
        FakeApi api = new();
        using var model = Model(new NativeQamPowerProfileService(new PowerSchemes(api)));
        await model.RefreshAsync();
        api.Reject = true;
        await model.SelectAsync(Second.ToString("D"));
        Assert.Equal(First.ToString("D"), model.State.Current);
        Assert.True(model.CanSelect);
        Assert.Contains("failed", model.Status, StringComparison.Ordinal);
        await model.SelectAsync(Second.ToString("D"));
        Assert.Equal(2, api.Writes);
        await model.RefreshAsync();
        Assert.True(model.CanSelect);
    }

    [Fact]
    public async Task PreviewAndUnknownIdsCannotWrite()
    {
        FakeApi api = new();
        using var preview = Model(new NativeQamPowerProfileService(new PowerSchemes(api)), true);
        await preview.RefreshAsync();
        await preview.SelectAsync(Second.ToString("D"));
        Assert.False(preview.CanSelect);
        Assert.Contains("Preview only", preview.Status, StringComparison.Ordinal);
        using var model = Model(new NativeQamPowerProfileService(new PowerSchemes(api)));
        await model.RefreshAsync();
        await model.SelectAsync(Guid.NewGuid().ToString("D"));
        Assert.Equal(0, api.Writes);
    }

    [Fact]
    public async Task OnePowerProfileIsNothingToChooseInEitherPicker()
    {
        FakeApi api = new() { Single = true };
        using var model = Model(new NativeQamPowerProfileService(new PowerSchemes(api)));
        Assert.Empty(model.State.Options);
        await model.RefreshAsync();
        Assert.False(model.CanSelect);
        Assert.Empty(model.State.Options);

        var qam = new NativeQamPowerProfileService(new PowerSchemes(new FakeApi { Single = true }));
        var state = await qam.ReadAsync();
        Assert.False(state!.Available);
        Assert.Empty(state.Options);
        Assert.Equal(First.ToString("D"), state.Current);
        Assert.Contains("one power profile", state.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyEnumerationDisablesSelectionButAnUnreadableActiveGuidDoesNotGateWrites()
    {
        using var empty = Model(new NativeQamPowerProfileService(new PowerSchemes(new FakeApi { Empty = true })));
        await empty.RefreshAsync();
        Assert.False(empty.CanSelect);

        FakeApi api = new() { ReadFailure = true };
        using var model = Model(new NativeQamPowerProfileService(new PowerSchemes(api)));
        await model.RefreshAsync();
        Assert.Empty(model.State.Current);
        Assert.True(model.CanSelect);
        var reads = api.ActiveReads;
        await model.SelectAsync(Second.ToString("D"));
        Assert.Equal(Second.ToString("D"), model.State.Current);
        Assert.Equal(1, api.Writes);
        Assert.Equal(reads, api.ActiveReads);
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
        var model = Model(new NativeQamPowerProfileService(new PowerSchemes(api)));
        var notifications = 0;
        model.Changed += () => notifications++;
        var pending = model.RefreshAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await model.RefreshAsync();
            await model.SelectAsync(Second.ToString("D"));
            Assert.False(model.CanSelect);
            model.Dispose();
        }
        finally
        {
            release.Set();
        }

        await pending;
        Assert.Equal(0, notifications);
        Assert.Empty(model.State.Options);
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
