using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Tests.Core;

public sealed class ProfileEditsTests
{
    private static ActiveProfile Running(ProfileConfig config, string id = "steam:42", string? exe = "game.exe")
    {
        return ProfileResolver.Activate(config, id, exe, 42);
    }

    [Fact]
    public void OptingInCreatesAnEmptyProfileAndCopiesNothingFromGlobal()
    {
        // The model: a game profile holds only what was changed for that game. Seeding it from Global
        // froze every Global value into the game, so later Global changes never reached it.
        ProfileConfig config = new() { Global = new ProfileValues { FrameLimit = 60, SustainedWatts = 15 } };

        Assert.True(ProfileEdits.SetGameEnabled(config, Running(config), true));

        var game = Assert.Single(config.Games);
        Assert.Equal("steam:42", game.Id);
        Assert.Equal(["game.exe"], game.ProcessNames);
        Assert.True(game.Enabled);
        Assert.Equal(0, game.Values.Count());
    }

    [Fact]
    public void SwitchingOffKeepsTheValuesAndSwitchingOnRestoresThem()
    {
        ProfileConfig config = new();
        ProfileEdits.SetGameEnabled(config, Running(config), true);
        config.Games[0].Values.FrameLimit = 40;

        Assert.True(ProfileEdits.SetGameEnabled(config, Running(config), false));
        Assert.Equal(40, config.Games[0].Values.FrameLimit);
        Assert.False(ProfileEdits.SetGameEnabled(config, Running(config), false));

        Assert.True(ProfileEdits.SetGameEnabled(config, Running(config), true));
        Assert.Equal(40, config.Games[0].Values.FrameLimit);
    }

    [Fact]
    public void AProfileNothingCouldMatchIsNeverCreated()
    {
        // The application's id is already used by a profile bound to another executable, and this
        // one's executable is not known yet: a generated id would match nothing.
        ProfileConfig config = new()
        {
            Games = [new GameProfile { Id = "steam:42", ProcessNames = ["other.exe"], Enabled = true }]
        };

        Assert.False(ProfileEdits.SetGameEnabled(config, Running(config, exe: null), true));
        Assert.Single(config.Games);
    }

    [Fact]
    public void NothingRunningCannotBeOptedIn()
    {
        ProfileConfig config = new();

        Assert.False(ProfileEdits.SetGameEnabled(config, ActiveProfile.None, true));
        Assert.Empty(config.Games);
    }

    [Fact]
    public void AnEditTargetsTheGameOnlyWhileItsProfileIsOn()
    {
        ProfileConfig config = new();
        ProfileEdits.SetGameEnabled(config, Running(config), true);

        Assert.Same(config.Games[0].Values, ProfileEdits.Target(config, Running(config), ProfileLayer.Active));
        Assert.Same(config.Global, ProfileEdits.Target(config, Running(config), ProfileLayer.Global));

        config.Games[0].Enabled = false;
        Assert.Same(config.Global, ProfileEdits.Target(config, Running(config), ProfileLayer.Active));
    }

    [Fact]
    public void AnEditForAGameProfileThatVanishedIsRefusedRatherThanWidenedToGlobal()
    {
        ProfileConfig config = new();
        ProfileEdits.SetGameEnabled(config, Running(config), true);
        var active = Running(config);
        config.Games.Clear();

        Assert.Null(ProfileEdits.Target(config, active, ProfileLayer.Active));
    }

    [Fact]
    public void ResettingAGameClearsEveryValueButKeepsTheProfile()
    {
        ProfileConfig config = new();
        ProfileEdits.SetGameEnabled(config, Running(config), true);
        config.Games[0].Values.FrameLimit = 40;
        config.Games[0].Values.SetDevice("claw", "lighting.zone-color", "buttons",
            new CapabilityValue
                { Kind = CapabilityValueKind.Color, ColorValue = 1 });

        Assert.True(ProfileEdits.ResetGame(config, "steam:42"));

        var game = Assert.Single(config.Games);
        Assert.True(game.Enabled);
        Assert.Equal(0, game.Values.Count());
        Assert.False(ProfileEdits.ResetGame(config, "steam:42"));
    }

    [Fact]
    public void ResettingGlobalClearsOnlyThePerformanceTab()
    {
        ProfileConfig config = new()
        {
            Global = new ProfileValues
            {
                FrameLimit = 60, OverlayLevel = 1, SustainedWatts = 15, VariableRefreshRate = true,
                ControllerTarget = ManagedControllerTarget.Xbox360
            }
        };
        config.Global.SetDevice("claw", "lighting.zone-color", "buttons",
            new CapabilityValue
                { Kind = CapabilityValueKind.Color, ColorValue = 1 });

        Assert.True(ProfileEdits.ResetGlobalPerformance(config));

        Assert.Null(config.Global.FrameLimit);
        Assert.Null(config.Global.SustainedWatts);
        Assert.Null(config.Global.VariableRefreshRate);
        Assert.Equal(ManagedControllerTarget.Xbox360, config.Global.ControllerTarget);
        Assert.Single(config.Global.Device);
    }

    [Fact]
    public void ClearingAnOverrideLetsTheValueFallBack()
    {
        ProfileValues values = new() { FrameLimit = 40 };
        values.SetDevice("claw", "fan.mode", null,
            new CapabilityValue
                { Kind = CapabilityValueKind.Integer, IntegerValue = 1 });

        Assert.True(values.Clear(new ProfileSettingKey(ProfileField.FrameLimit)));
        Assert.True(values.Clear(ProfileSettingKey.ForDevice("fan.mode", null), "claw"));
        Assert.False(values.Clear(new ProfileSettingKey(ProfileField.FrameLimit)));
        Assert.Equal(0, values.Count());
    }

    [Fact]
    public void DeletingAnAuthoredProfileClearsEveryLayerThatSelectedIt()
    {
        ProfileConfig config = new()
        {
            Global = new ProfileValues { FanCurveProfileId = "quiet" },
            Games = [new GameProfile { Id = "steam:1", Values = new ProfileValues { FanCurveProfileId = "quiet" } }]
        };

        Assert.True(ProfileEdits.RemoveFanCurveReferences(config, "quiet"));

        Assert.Null(config.Global.FanCurveProfileId);
        Assert.Null(config.Games[0].Values.FanCurveProfileId);
    }

    [Fact]
    public void SavingANamedProfileNeedsAnExecutableAndRefusesAClaimedOne()
    {
        ProfileConfig config = new();
        Assert.Throws<ArgumentException>(() => ProfileEdits.SaveGame(config, null, "Name", [], true));

        var id = ProfileEdits.SaveGame(config, null, " My game ", ["game.exe"], true);
        Assert.StartsWith("profile:", id, StringComparison.Ordinal);
        Assert.Equal("My game", config.Games[0].Name);

        Assert.Throws<ArgumentException>(() => ProfileEdits.SaveGame(config, null, "Other", ["GAME.exe"], true));
        Assert.Equal(id, ProfileEdits.SaveGame(config, id, "Renamed", ["game.exe", "launcher.exe"], false));
        Assert.False(config.Games[0].Enabled);
    }

    private static bool Set(
        ProfileConfig config, string id, string name, ManagedControllerTarget? target, bool removeEmptyProfile = true)
    {
        return ProfileEdits.SetApplicationControllerTarget(config, id, name, target, removeEmptyProfile, out _);
    }

    [Fact]
    public void AnImportedControllerTargetIsAProfileTheIdentityAloneActivates()
    {
        // The importer's controller-only route is this write and nothing else: no executable, so
        // the profile matches whatever Steam reports running under that identity.
        ProfileConfig config = new();

        Assert.True(Set(config, "steam:42", "Moonlit", ManagedControllerTarget.Xbox360));

        var game = Assert.Single(config.Games);
        Assert.Equal("steam:42", game.Id);
        Assert.Equal("Moonlit", game.Name);
        Assert.Empty(game.ProcessNames);
        Assert.True(game.Enabled);
        Assert.Equal(ManagedControllerTarget.Xbox360, game.Values.ControllerTarget);
    }

    [Fact]
    public void RewritingTheSameControllerTargetReportsNoChange()
    {
        ProfileConfig config = new();
        Set(config, "steam:42", "Moonlit", ManagedControllerTarget.Xbox360);

        Assert.False(Set(config, "steam:42", "Moonlit", ManagedControllerTarget.Xbox360));
    }

    [Fact]
    public void ClearingAnImportedTargetRemovesAProfileThatThenSaysNothing()
    {
        // Switching an import back to the overlay route must not leave an empty profile behind.
        ProfileConfig config = new();
        Set(config, "steam:42", "Moonlit", ManagedControllerTarget.Xbox360);

        Assert.True(Set(config, "steam:42", "Moonlit", null));

        Assert.Empty(config.Games);
        Assert.False(Set(config, "steam:42", "Moonlit", null));
    }

    [Fact]
    public void ClearingAnImportedTargetKeepsAProfileThatStillCarriesSomething()
    {
        ProfileConfig config = new();
        Set(config, "steam:42", "Moonlit", ManagedControllerTarget.Xbox360);
        config.Games[0].Values.FrameLimit = 60;

        Assert.True(Set(config, "steam:42", "Moonlit", null));

        Assert.Equal(60, Assert.Single(config.Games).Values.FrameLimit);
    }

    [Fact]
    public void AnImportedTargetNeverAddressesANamedProfile()
    {
        // A named profile is the user's own; the importer must not be able to repurpose one.
        ProfileConfig config = new();
        var id = ProfileEdits.SaveGame(config, null, "Mine", ["game.exe"], true);

        Assert.Throws<ArgumentException>(() => Set(config, id, "Moonlit", ManagedControllerTarget.Xbox360));
        Assert.Throws<ArgumentException>(() => Set(config, string.Empty, "Moonlit", ManagedControllerTarget.Xbox360));
    }

    [Fact]
    public void AnImportedTargetKeepsTheNameAProfileAlreadyHad()
    {
        ProfileConfig config = new();
        ProfileEdits.SetGameEnabled(config, Running(config), true);
        config.Games[0].Name = "Chosen by the user";

        Set(config, "steam:42", "Store name", ManagedControllerTarget.Xbox360);

        Assert.Equal("Chosen by the user", Assert.Single(config.Games).Name);
        Assert.Equal(["game.exe"], config.Games[0].ProcessNames);
    }

    [Fact]
    public void SettingATargetSaysWhetherItCreatedTheProfile()
    {
        // The importer remembers this, because only a profile it created may be removed later.
        ProfileConfig config = new();

        ProfileEdits.SetApplicationControllerTarget(
            config, "steam:42", "Moonlit", ManagedControllerTarget.Xbox360, false, out var created);
        Assert.True(created);

        config.Games[0].Values.ControllerTarget = null;
        ProfileEdits.SetApplicationControllerTarget(
            config, "steam:42", "Moonlit", ManagedControllerTarget.Xbox360, false, out created);
        Assert.False(created);
    }

    [Fact]
    public void ClearingATargetKeepsAProfileTheImporterDidNotCreate()
    {
        // A profile the user made, whose only value is its controller target, keeps its name,
        // executables and switch when an import over it is switched back or removed.
        ProfileConfig config = new();
        config.Games.Add(new GameProfile
        {
            Id = "steam:42", Name = "Mine", Enabled = false, ProcessNames = ["game.exe"],
            Values = new ProfileValues { ControllerTarget = ManagedControllerTarget.Xbox360 }
        });

        Assert.True(Set(config, "steam:42", string.Empty, null, false));

        var kept = Assert.Single(config.Games);
        Assert.Equal("Mine", kept.Name);
        Assert.Equal(["game.exe"], kept.ProcessNames);
        Assert.Null(kept.Values.ControllerTarget);
    }
}
