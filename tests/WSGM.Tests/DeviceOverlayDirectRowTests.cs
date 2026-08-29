using WSGM.Core;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Input;
using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.Tests;

/// <summary>
/// The Device rows that are WSGM's own rather than a plugin's.
/// </summary>
/// <remarks>
/// AutoTDP, the controller target, glyph selection and cycle recovery are WSGM settings and WSGM
/// actions. They never arrive through the plugin capability list, which is what keeps the capability
/// invoke path single-purpose — and is also why each has to be counted into its section by hand, or
/// a section holding only one of them is dropped from the menu and the row becomes unreachable.
/// </remarks>
public sealed class DeviceOverlayDirectRowTests
{
    [Fact]
    public void ControllerManagementSwitchedOffShowsNoRowRatherThanAnInertOne()
    {
        // Off is a setting, not a fault, and the page has other rows. A permanently greyed control
        // the user cannot act on from this page is worse than nothing.
        Assert.Null(DeviceOverlayBridge.ControllerView(
            enabled: false,
            Status(ControllerManagementState.Off, null)));
    }

    [Fact]
    public void AnActiveTargetIsNamedInTheRowRatherThanOnlyMarkedOn()
    {
        DeviceOverlayController? row = DeviceOverlayBridge.ControllerView(
            enabled: true,
            Status(ControllerManagementState.Active, ManagedControllerTarget.SteamDeckComposite));

        Assert.NotNull(row);
        Assert.Equal("DECK", row.TrailingText);
        Assert.Equal(DeviceOverlayStatus.Available, row.Status);
        Assert.True(row.CanCycle);
    }

    [Fact]
    public void ARunningGameIsToldTheChangeWillNotReachIt()
    {
        // A game holds the target it launched with. Without saying so, the control looks broken.
        DeviceOverlayController? row = DeviceOverlayBridge.ControllerView(
            enabled: true,
            Status(
                ControllerManagementState.Active,
                ManagedControllerTarget.Xbox360,
                applicationId: "steam:70"));

        Assert.NotNull(row);
        Assert.Contains("restart the running game", row.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnavailableBackendCannotBeCycledIntoAnotherBrokenTarget()
    {
        DeviceOverlayController? row = DeviceOverlayBridge.ControllerView(
            enabled: true,
            Status(
                ControllerManagementState.Unavailable,
                null,
                "The virtual controller component is not installed."));

        Assert.NotNull(row);
        Assert.False(row.CanCycle);
        Assert.Equal("NONE", row.TrailingText);
        Assert.Equal(DeviceOverlayStatus.Unsupported, row.Status);
    }

    [Theory]
    [InlineData(null, ManagedControllerTarget.SteamDeckComposite)]
    [InlineData(ManagedControllerTarget.SteamDeckComposite, ManagedControllerTarget.Xbox360)]
    [InlineData(ManagedControllerTarget.Xbox360, ManagedControllerTarget.DualShock4)]
    [InlineData(ManagedControllerTarget.DualShock4, ManagedControllerTarget.SteamDeckComposite)]
    public void CyclingVisitsEveryTargetAndReturns(
        ManagedControllerTarget? current,
        ManagedControllerTarget expected) =>
        Assert.Equal(expected, DeviceOverlayBridge.NextTarget(current));

    [Fact]
    public void AHealthyCycleOffersNoRecoveryRow()
    {
        // A recovery control that is always present but almost always inert trains a user to ignore
        // it, which is the opposite of what it is for.
        Assert.Null(DeviceOverlayBridge.RecoveryView(
            DeviceCycleState.Active,
            retryAvailableAt: null,
            DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void APendingRetryWaitsRatherThanOfferingAnActionThatWouldBeRefused()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        DeviceOverlayRecovery? row = DeviceOverlayBridge.RecoveryView(
            DeviceCycleState.Faulted,
            now.AddMinutes(2),
            now);

        Assert.NotNull(row);
        Assert.False(row.CanRetry);
        Assert.Equal("WAIT", row.TrailingText);
        Assert.Equal(DeviceOverlayStatus.Progress, row.Status);
    }

    [Fact]
    public void AnAvailableRetryIsOfferedAndCarriesTheCycleState()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        DeviceOverlayRecovery? row = DeviceOverlayBridge.RecoveryView(
            DeviceCycleState.Faulted,
            now.AddSeconds(-1),
            now);

        Assert.NotNull(row);
        Assert.True(row.CanRetry);
        Assert.Equal("READY", row.TrailingText);
        // The row says what is wrong, not only that a button exists.
        Assert.Contains("·", row.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ASectionHoldingOnlyAWsgmRowStillAppearsInTheMenu()
    {
        // The regression this guards: no plugin publishes a controller capability, so the Controller
        // and motion section is empty of capabilities and would be dropped, taking the only way to
        // reach the target row with it.
        DeviceOverlaySnapshot snapshot = new(
            Visible: true,
            Status: "Active",
            Detail: string.Empty,
            GlyphSelection: null,
            Capabilities: [],
            AutoTdp: null,
            Controller: DeviceOverlayBridge.ControllerView(
                enabled: true,
                Status(ControllerManagementState.Active, ManagedControllerTarget.Xbox360)),
            Recovery: null);

        DeviceOverlaySectionEntry entry = Assert.Single(DeviceOverlaySectionPages.Build(snapshot));

        Assert.Equal(DeviceOverlaySection.ControllerAndMotion, entry.Section);
        Assert.Equal(1, entry.Count);
    }

    [Fact]
    public void EachWsgmRowIsCountedIntoItsOwnSection()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        DeviceOverlaySnapshot snapshot = new(
            Visible: true,
            Status: "Active",
            Detail: string.Empty,
            GlyphSelection: new DeviceOverlayGlyphSelection(
                DeviceOverlayStatus.Available,
                "Physical glyphs",
                string.Empty,
                "AUTO",
                CanCycle: true),
            Capabilities: [],
            AutoTdp: DeviceOverlayBridge.AutoTdpView(enabled: false, status: null),
            Controller: DeviceOverlayBridge.ControllerView(
                enabled: true,
                Status(ControllerManagementState.Idle, ManagedControllerTarget.Xbox360)),
            Recovery: DeviceOverlayBridge.RecoveryView(
                DeviceCycleState.Faulted,
                now.AddSeconds(-1),
                now));

        Assert.Equal(
            new[]
            {
                DeviceOverlaySection.PowerAndThermals,
                DeviceOverlaySection.ControllerAndMotion,
                DeviceOverlaySection.Glyphs,
                DeviceOverlaySection.Diagnostics,
            },
            DeviceOverlaySectionPages.Build(snapshot).Select(entry => entry.Section).ToArray());
    }

    [Fact]
    public void WithNoProfilesTheRowSaysWhereToMakeOneRatherThanVanishing()
    {
        // Unlike recovery, this row is always present: profiles are a feature a user has to find
        // before they can use it, and an absent row would read as the feature being missing.
        DeviceOverlayProfile row = DeviceOverlayBridge.ProfileView([], selected: null);

        Assert.False(row.CanCycle);
        Assert.Equal("NONE", row.TrailingText);
        Assert.Contains("Settings", row.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ASelectedProfileIsNamedAndMarkedActive()
    {
        DeviceOverlayProfile row = DeviceOverlayBridge.ProfileView(["docked", "handheld"], "handheld");

        Assert.Equal("HANDHELD", row.TrailingText);
        Assert.Equal(DeviceOverlayStatus.Available, row.Status);
        Assert.True(row.CanCycle);
    }

    [Fact]
    public void ASelectionNamingAProfileThatNoLongerExistsReadsAsNone()
    {
        // Which is what it now behaves as: the resolver finds no value under that name and falls
        // through to the power and global layers. Showing the stale name would claim otherwise.
        DeviceOverlayProfile row = DeviceOverlayBridge.ProfileView(["docked"], "deleted");

        Assert.Equal("NONE", row.TrailingText);
        Assert.Equal(DeviceOverlayStatus.None, row.Status);
    }

    [Theory]
    [InlineData(null, "docked")]
    [InlineData("docked", "handheld")]
    [InlineData("handheld", null)]
    public void CyclingProfilesPassesThroughNoneSoDefaultsAreAlwaysReachable(
        string? selected,
        string? expected)
    {
        // None is a position in the cycle rather than a separate control, so the same button that
        // applied a profile can always get back to unmodified defaults.
        Assert.Equal(expected, DeviceOverlayBridge.NextProfile(["docked", "handheld"], selected));
    }

    [Fact]
    public void CyclingFromAnUnknownSelectionLandsOnTheFirstProfileRatherThanStalling()
    {
        Assert.Equal("docked", DeviceOverlayBridge.NextProfile(["docked", "handheld"], "deleted"));
    }

    [Fact]
    public void CyclingWithNoProfilesStaysAtNone()
    {
        Assert.Null(DeviceOverlayBridge.NextProfile([], "anything"));
    }

    private static ControllerManagerStatus Status(
        ControllerManagementState state,
        ManagedControllerTarget? target,
        string detail = "",
        string? applicationId = null) =>
        new(
            state,
            target,
            ControllerTargetSource.GlobalDefault,
            applicationId,
            UiInputSource.ManagedCanonical,
            detail);
}
