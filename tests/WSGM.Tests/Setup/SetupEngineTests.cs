using WSGM.Setup;
using WSGM.Setup.Engine;

namespace WSGM.Tests.Setup;

public sealed class SetupEngineTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void QuietUninstallIncludesEveryOwnedDriverUnlessComponentsAreKept(bool keepComponents, bool owned)
    {
        using SetupTestInstallation installation = new();
        installation.Engine.Components = new InstalledComponents
        {
            Usbip = owned, HidHide = owned, PawnIo = owned, InpOut = owned
        };
        var choices = QuietSetup.CreateUninstallChoices(new SetupOptions
        {
            Mode = SetupMode.Uninstall, Quiet = true, KeepComponents = keepComponents
        });
        var plan = installation.Engine.PlanUninstall(choices);
        foreach (var label in new[]
                 {
                     "Removing the USB/IP driver", "Removing HidHide", "Removing PawnIO",
                     "Removing Steam Deck firmware access"
                 })
        {
            Assert.Equal(owned && !keepComponents, plan.Any(step => step.Label == label));
        }

        Assert.True(choices.KeepData);
        Assert.Equal(owned, installation.Engine.Components.PawnIo);
        Assert.Equal(owned, installation.Engine.Components.InpOut);
        Assert.Empty(installation.Runtime.Calls);
    }

    [Fact]
    public void QuietUninstallOnlyRemovesUserDataWhenExplicitlyRequested()
    {
        var choices = QuietSetup.CreateUninstallChoices(new SetupOptions
        {
            Mode = SetupMode.Uninstall, Quiet = true, RemoveData = true, KeepComponents = true
        });
        Assert.False(choices.KeepData);
        Assert.False(choices.RemovePawnIo);
        Assert.False(choices.RemoveInpOut);
    }

    [Fact]
    public void InstallPlanKeepsRegistrationFailureNonfatalAndCommitsTheAppliedProfile()
    {
        using SetupTestInstallation installation = new();
        var engine = installation.Engine;
        var plan = installation.InstallPlan();
        var registration = Assert.Single(plan, step => step.Label == "Registering the sign-in service");
        var profile = Assert.Single(plan, step => step.Label == "Applying your profile");
        installation.Runtime.OnRun = (_, arguments) => arguments == "--install" ? 1 : 0;

        Assert.False(registration.Fatal);
        Assert.Same(profile, plan.Last(step => step.Fatal));
        Assert.True(
            engine.Run([.. plan.TakeWhile(step => step.Label != "Installing media preview runtime")], () => { }));

        Assert.Equal(StepState.Done, profile.State);
        Assert.Equal(StepState.Failed, registration.State);
        Assert.Equal("The sign-in service could not be registered; see setup.log.", registration.Note);
        Assert.Equal(1, installation.Runtime.Stops);
        Assert.Equal("new-app", File.ReadAllText(Path.Combine(installation.Root, "App", "WSGM.exe")));
        Assert.False(Directory.Exists(Path.Combine(installation.Root, "App.previous")));
        Assert.False(File.Exists(Path.Combine(installation.Machine, "setup-transaction.json")));
        Assert.DoesNotContain("RestoreVersion", installation.Runtime.Calls);
        var apply = installation.Runtime.Calls.FindIndex(call =>
            call.StartsWith("Run --setup --answers=", StringComparison.Ordinal));
        Assert.True(apply >= 0 && apply < installation.Runtime.Calls.IndexOf("Run --install"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CommitFailureStopsTheNewServiceBeforeRestoringFilesOrRetainsTheTransaction(bool rollbackStopSucceeds)
    {
        using SetupTestInstallation installation = new();
        var root = installation.Root;
        var journal = Path.Combine(installation.Machine, "setup-transaction.json");
        FileStream? service = null;
        FileStream? commit = null;
        installation.Runtime.OnRun = (_, arguments) =>
        {
            if (arguments == "--install")
            {
                // Model the just-registered service holding its new image, and refuse Commit's write.
                service = File.Open(Path.Combine(root, "App", "WSGM.LogonService.exe"),
                    FileMode.Open, FileAccess.Read, FileShare.Read);
                commit = File.Open(journal + ".tmp", FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                installation.Runtime.Version = "2.1.0.1";
            }

            return 0;
        };
        installation.Runtime.OnStop = count =>
        {
            if (count == 1)
            {
                return true;
            }

            // None of the five file targets may have been restored before this stop.
            Assert.Equal("new-app", File.ReadAllText(Path.Combine(root, "App", "WSGM.exe")));
            Assert.Equal("new-plugin", File.ReadAllText(Path.Combine(root, "Plugins", "wsgm.test-2.1.0.wsgmpkg")));
            Assert.Equal("new-plugin",
                File.ReadAllText(Path.Combine(root, "Setup", "Packages", "wsgm.test-2.1.0.wsgmpkg")));
            Assert.Equal("new-setup", File.ReadAllText(Path.Combine(root, "Setup", "WSGM.Setup.exe")));
            Assert.NotEqual("old-bundle", File.ReadAllText(Path.Combine(installation.Machine, "bundle.json")));
            if (rollbackStopSucceeds)
            {
                service!.Dispose();
                commit!.Dispose();
            }

            return rollbackStopSucceeds;
        };

        try
        {
            Assert.False(installation.Engine.Run(installation.InstallThroughService(), () => { }));
            Assert.Equal(2, installation.Runtime.Stops);
            Assert.Equal(!rollbackStopSucceeds, installation.Engine.RollbackIncomplete);
            Assert.Equal(!rollbackStopSucceeds, File.Exists(journal));
            Assert.Equal(!rollbackStopSucceeds, Directory.Exists(Path.Combine(root, "App.previous")));
            if (rollbackStopSucceeds)
            {
                Assert.Equal("old-app", File.ReadAllText(Path.Combine(root, "App", "WSGM.exe")));
                Assert.Equal("old-service", File.ReadAllText(Path.Combine(root, "App", "WSGM.LogonService.exe")));
                Assert.Equal("old-plugin", File.ReadAllText(Path.Combine(root, "Plugins", "wsgm.test-2.0.0.wsgmpkg")));
                Assert.False(File.Exists(Path.Combine(root, "Plugins", "wsgm.test-2.1.0.wsgmpkg")));
                Assert.Equal("old-package", File.ReadAllText(Path.Combine(root, "Setup", "Packages", "old.wsgmpkg")));
                Assert.Equal("old-setup", File.ReadAllText(Path.Combine(root, "Setup", "WSGM.Setup.exe")));
                Assert.Equal("old-bundle", File.ReadAllText(Path.Combine(installation.Machine, "bundle.json")));
                Assert.Equal("2.0.0.1", installation.Runtime.Version);
                Assert.True(installation.Runtime.Calls.LastIndexOf("StopService")
                            < installation.Runtime.Calls.IndexOf("RestoreVersion"));
            }
            else
            {
                Assert.Equal("new-app", File.ReadAllText(Path.Combine(root, "App", "WSGM.exe")));
                Assert.DoesNotContain("RestoreVersion", installation.Runtime.Calls);
                Assert.DoesNotContain(installation.Runtime.Calls,
                    call => call.StartsWith("Start ", StringComparison.Ordinal));
            }
        }
        finally
        {
            service?.Dispose();
            commit?.Dispose();
        }
    }

    [Fact]
    public void RefusedRebootSchedulingFailsThePlannedDeletionStepAndPreservesRecoveryRecords()
    {
        List<string> scheduled = [];
        using SetupTestInstallation installation = new(path => WindowsSetup.DeleteOrScheduleAtReboot(path, entry =>
        {
            scheduled.Add(entry);
            return false;
        }));
        SetupTestInstallation.Write(installation.User, "cache/held.bin", "cache");
        SetupTestInstallation.Write(installation.User, "config.json", "settings");
        SetupTestInstallation.Write(installation.User, "hidhide-ownership.json", "recovery");
        var cache = Path.Combine(installation.User, "cache");
        var held = Path.Combine(cache, "held.bin");
        using var file = File.Open(held, FileMode.Open, FileAccess.Read, FileShare.Read);
        var deletion = installation.Engine.PlanUninstall(new UninstallChoices(false,
            false, false)).Last();

        Assert.True(installation.Engine.Run([deletion], () => { }));

        Assert.Equal(StepState.Failed, deletion.State);
        Assert.Contains(cache, deletion.Note, StringComparison.Ordinal);
        Assert.Contains("could neither delete these nor schedule them", deletion.Note, StringComparison.Ordinal);
        Assert.Equal([held, cache], scheduled);
        Assert.True(File.Exists(held));
        Assert.Equal("settings", File.ReadAllText(Path.Combine(installation.User, "config.json")));
        Assert.Equal("recovery", File.ReadAllText(Path.Combine(installation.User, "hidhide-ownership.json")));
    }
}
