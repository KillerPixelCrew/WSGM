using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>Choosing, clearing and resolving device profile selections.</summary>
public sealed class DeviceProfileSelectionTests
{
    private const string Fan = "thermal.fan-curve";

    private static DeviceAuthoredProfile Profile(string id)
    {
        return new DeviceAuthoredProfile
        {
            ProfileId = id,
            Name = id,
            CapabilityId = Fan,
            Curve = [new AuthoredCurvePoint { Input = 0, Output = 10 }]
        };
    }

    private static DeviceProfileSelection Selection(
        string? global,
        params (string Application, string Profile)[] overrides)
    {
        return new DeviceProfileSelection
        {
            CapabilityId = Fan,
            GlobalProfileId = global,
            ApplicationOverrides =
            [
                .. overrides.Select(entry =>
                    new DeviceApplicationProfileSelection
                    {
                        ApplicationId = entry.Application,
                        ProfileId = entry.Profile
                    })
            ]
        };
    }

    [Fact]
    public void TheGlobalChoiceAppliesWhenNoApplicationOverridesIt()
    {
        var resolution = DeviceProfileSelectionStore.Resolve(
            [Selection("quiet")],
            [Profile("quiet")],
            Fan,
            "steam:42");

        Assert.Equal("quiet", resolution.Profile?.ProfileId);
        Assert.False(resolution.ApplicationScoped);
    }

    [Fact]
    public void AnApplicationOverrideOutranksTheGlobalChoice()
    {
        var resolution = DeviceProfileSelectionStore.Resolve(
            [Selection("quiet", ("steam:42", "loud"))],
            [Profile("quiet"), Profile("loud")],
            Fan,
            "steam:42");

        Assert.Equal("loud", resolution.Profile?.ProfileId);
        Assert.True(resolution.ApplicationScoped);
    }

    [Fact]
    public void AnotherApplicationStillGetsTheGlobalChoice()
    {
        var resolution = DeviceProfileSelectionStore.Resolve(
            [Selection("quiet", ("steam:42", "loud"))],
            [Profile("quiet"), Profile("loud")],
            Fan,
            "process:game.exe");

        Assert.Equal("quiet", resolution.Profile?.ProfileId);
    }

    [Fact]
    public void NoRunningApplicationUsesTheGlobalChoice()
    {
        var resolution = DeviceProfileSelectionStore.Resolve(
            [Selection("quiet", ("steam:42", "loud"))],
            [Profile("quiet"), Profile("loud")],
            Fan,
            null);

        Assert.Equal("quiet", resolution.Profile?.ProfileId);
    }

    [Fact]
    public void NoSelectionAtAllLeavesTheCapabilityAlone()
    {
        // Inventing a choice would take the capability away from whatever else drives it.
        var resolution = DeviceProfileSelectionStore.Resolve(
            [],
            [Profile("quiet")],
            Fan,
            "steam:42");

        Assert.Null(resolution.Profile);
        Assert.Null(resolution.Diagnostic);
    }

    [Fact]
    public void ASelectionForAnotherCapabilityIsNotUsed()
    {
        var resolution = DeviceProfileSelectionStore.Resolve(
            [Selection("quiet")],
            [Profile("quiet")],
            "lighting.color",
            null);

        Assert.Null(resolution.Profile);
    }

    [Fact]
    public void AnApplicationOverrideNamingADeletedProfileIsReportedNotDowngraded()
    {
        // Falling back to the global profile would hide that the user's intent for this application
        // is gone, and the fans would quietly run someone else's curve.
        var resolution = DeviceProfileSelectionStore.Resolve(
            [Selection("quiet", ("steam:42", "deleted"))],
            [Profile("quiet")],
            Fan,
            "steam:42");

        Assert.Null(resolution.Profile);
        Assert.True(resolution.ApplicationScoped);
        Assert.Contains("deleted", resolution.Diagnostic);
        Assert.Contains("steam:42", resolution.Diagnostic);
    }

    [Fact]
    public void AGlobalSelectionNamingADeletedProfileIsReported()
    {
        var resolution = DeviceProfileSelectionStore.Resolve(
            [Selection("gone")],
            [Profile("quiet")],
            Fan,
            null);

        Assert.Null(resolution.Profile);
        Assert.Contains("gone", resolution.Diagnostic);
    }

    [Fact]
    public void ASelectionReferencesTheProfileSoEditsPropagate()
    {
        // By id, never by copy: editing a profile has to change every application already using it.
        var profile = Profile("quiet");
        var selection = Selection("quiet", ("steam:42", "quiet"));

        profile.Curve = [new AuthoredCurvePoint { Input = 40, Output = 80 }];
        var resolution = DeviceProfileSelectionStore.Resolve(
            [selection],
            [profile],
            Fan,
            "steam:42");

        Assert.Equal(80, resolution.Profile?.Curve[0].Output);
    }

    private static PluginSettingsScope Scope()
    {
        return new PluginSettingsScope
        {
            DeviceDefinitionId = "msi.claw8",
            PluginId = "wsgm.device.msi"
        };
    }

    [Fact]
    public void ChoosingAGlobalProfileCreatesTheSelection()
    {
        var scope = Scope();

        Assert.True(DeviceProfileSelectionStore.SetSelection(
            scope,
            Fan,
            "quiet",
            DeviceProfileScope.Global));

        Assert.Equal("quiet", scope.ProfileSelections[0].GlobalProfileId);
    }

    [Fact]
    public void ChoosingTheSameProfileAgainReportsNoChange()
    {
        var scope = Scope();
        DeviceProfileSelectionStore.SetSelection(scope, Fan, "quiet", DeviceProfileScope.Global);

        Assert.False(DeviceProfileSelectionStore.SetSelection(
            scope,
            Fan,
            "quiet",
            DeviceProfileScope.Global));
    }

    [Fact]
    public void AnApplicationOverrideIsReadBackAsApplicationScoped()
    {
        var scope = Scope();
        DeviceProfileSelectionStore.SetSelection(scope, Fan, "quiet", DeviceProfileScope.Global);
        DeviceProfileSelectionStore.SetSelection(
            scope,
            Fan,
            "loud",
            DeviceProfileScope.Application,
            "steam:42");

        var read = DeviceProfileSelectionStore.ReadSelection(
            scope,
            Fan,
            "steam:42",
            out var applicationScoped);

        Assert.Equal("loud", read);
        Assert.True(applicationScoped);
    }

    [Fact]
    public void AnotherApplicationReadsTheGlobalChoice()
    {
        var scope = Scope();
        DeviceProfileSelectionStore.SetSelection(scope, Fan, "quiet", DeviceProfileScope.Global);
        DeviceProfileSelectionStore.SetSelection(
            scope,
            Fan,
            "loud",
            DeviceProfileScope.Application,
            "steam:42");

        var read = DeviceProfileSelectionStore.ReadSelection(
            scope,
            Fan,
            "process:other.exe",
            out var applicationScoped);

        Assert.Equal("quiet", read);
        Assert.False(applicationScoped);
    }

    [Fact]
    public void ClearingAnOverrideFallsBackToTheGlobalChoice()
    {
        // "This game uses the default" is what clearing an override means; there is deliberately no
        // way to express "this game uses nothing".
        var scope = Scope();
        DeviceProfileSelectionStore.SetSelection(scope, Fan, "quiet", DeviceProfileScope.Global);
        DeviceProfileSelectionStore.SetSelection(
            scope,
            Fan,
            "loud",
            DeviceProfileScope.Application,
            "steam:42");

        Assert.True(DeviceProfileSelectionStore.SetSelection(
            scope,
            Fan,
            null,
            DeviceProfileScope.Application,
            "steam:42"));

        Assert.Equal(
            "quiet",
            DeviceProfileSelectionStore.ReadSelection(scope, Fan, "steam:42", out _));
    }

    [Fact]
    public void AnApplicationScopedChoiceWithNoRunningApplicationIsRefused()
    {
        // Silently widening a per-game change to every game is the worst possible reading of what
        // the user meant.
        var scope = Scope();

        Assert.False(DeviceProfileSelectionStore.SetSelection(
            scope,
            Fan,
            "quiet",
            DeviceProfileScope.Application));

        Assert.Empty(scope.ProfileSelections);
    }

    [Fact]
    public void ClearingAChoiceThatWasNeverMadeCreatesNothing()
    {
        var scope = Scope();

        Assert.False(DeviceProfileSelectionStore.SetSelection(
            scope,
            Fan,
            null,
            DeviceProfileScope.Global));

        Assert.Empty(scope.ProfileSelections);
    }

    [Fact]
    public void ChangingAnExistingOverrideReplacesItRatherThanAddingASecond()
    {
        var scope = Scope();
        DeviceProfileSelectionStore.SetSelection(
            scope,
            Fan,
            "loud",
            DeviceProfileScope.Application,
            "steam:42");
        DeviceProfileSelectionStore.SetSelection(
            scope,
            Fan,
            "quiet",
            DeviceProfileScope.Application,
            "steam:42");

        Assert.Single(scope.ProfileSelections[0].ApplicationOverrides);
        Assert.Equal("quiet", scope.ProfileSelections[0].ApplicationOverrides[0].ProfileId);
    }

    [Fact]
    public void NothingChosenReadsAsNull()
    {
        Assert.Null(DeviceProfileSelectionStore.ReadSelection(Scope(), Fan, "steam:42", out _));
    }
}
