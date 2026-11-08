using WSGM.Core;
using WSGM.Tests.Fakes;

namespace WSGM.Tests.Core;

public sealed class OtherManagersTests
{
    [Fact]
    public void HandheldCompanion_IsFoundByTheLogonTaskThatRunsIt_WhateverTheTaskIsCalled()
    {
        FakeAutostartSystem autostart = new();
        autostart.Tasks[@"\HC Autostart"] = "\"C:\\Program Files\\Handheld Companion\\HandheldCompanion.exe\"";
        autostart.TaskEnabled[@"\HC Autostart"] = true;

        var found = Assert.Single(OtherManagers.Detect(autostart, new FakeServices(), []));

        Assert.Equal("handheld-companion", found.Manager.Id);
        Assert.Equal([@"\HC Autostart"], found.Tasks);
        Assert.Equal("Handheld Companion: starts at sign-in", found.Describe());
    }

    [Fact]
    public void MakerApps_AreFoundByServiceAndTask_AndDisabledServicesDoNotCount()
    {
        FakeAutostartSystem autostart = new();
        autostart.TaskEnabled[@"\MSI_Center_M_Server"] = true;
        FakeServices services = new();
        services.Services["MSI Foundation Service"] = (2, true);
        services.Services["DAService"] = (4, false);

        var found = Assert.Single(OtherManagers.Detect(autostart, services, ["MSI Center M"]));

        Assert.Equal("msi-center-m", found.Manager.Id);
        Assert.Equal(["MSI Foundation Service"], found.Services);
        // The catalog task has no sign-in trigger here, so it is not described as one.
        Assert.Equal("MSI Center M: 1 service, 1 scheduled task, running now", found.Describe());
    }

    [Fact]
    public void CatalogTasksThatDoNotExist_AreNotDetected()
    {
        // A catalog task that does not exist reads as no state, never as enabled.
        FakeAutostartSystem autostart = new();

        Assert.Empty(OtherManagers.Detect(autostart, new FakeServices(), []));
    }

    [Fact]
    public void Restore_TreatsATaskRemovedMeanwhileAsRestored()
    {
        FakeAutostartSystem autostart = new() { TaskWritesFail = true };
        OtherManagerRecord record = new() { ManagerId = "msi-center-m", Kind = "task", Name = @"\MSI_Center_M_Server" };

        var restored = OtherManagers.Restore([record], autostart, new FakeServices());

        Assert.Equal([record], restored);
        Assert.Empty(autostart.Writes);
    }

    [Fact]
    public void Disable_RecordsBeforeChanging_ThenRestorePutsBackTheExactStartType()
    {
        FakeAutostartSystem autostart = new();
        autostart.TaskEnabled[@"\MSI_Center_M_Server"] = true;
        FakeServices services = new();
        services.Services["MSI Foundation Service"] = (2, true);
        var detected = OtherManagers.Detect(autostart, services, []);
        List<OtherManagerRecord> records = [];

        var result = OtherManagers.Disable(detected, entry =>
        {
            // The change must not have happened yet when it is recorded.
            if (entry.Kind == "service")
            {
                Assert.Equal(2, services.Services[entry.Name].Start);
            }

            records.Add(entry);
        }, autostart, services, running => running);

        Assert.Equal(2, result.Disabled.Count);
        Assert.Empty(result.Failed);
        Assert.False(autostart.TaskEnabled[@"\MSI_Center_M_Server"]);
        Assert.Equal(4, services.Services["MSI Foundation Service"].Start);
        Assert.Contains("stop MSI Foundation Service", services.Calls);

        var restored = OtherManagers.Restore(records, autostart, services);

        Assert.Equal(2, restored.Count);
        Assert.True(autostart.TaskEnabled[@"\MSI_Center_M_Server"]);
        Assert.Equal((2, true), services.Services["MSI Foundation Service"]);
        Assert.Contains("start MSI Foundation Service", services.Calls);
    }

    [Fact]
    public void ArmouryCrate_IsFoundByItsKeyControlHelper_AndByItsServiceComingBack()
    {
        // Handheld Companion's uninstaller re-enables ArmouryCrateControlInterface, which starts the
        // ArmouryCrateKeyControl helper that shows the "install Armoury Crate SE" dialog on the button.
        FakeServices services = new();
        services.Services["ArmouryCrateControlInterface"] = (2, false);

        var found = Assert.Single(OtherManagers.Detect(new FakeAutostartSystem(), services,
            ["ArmouryCrateKeyControl"]));

        Assert.Equal("armoury-crate", found.Manager.Id);
        Assert.Equal(["ArmouryCrateControlInterface"], found.Services);
        Assert.Equal(["ArmouryCrateKeyControl"], found.Running);
        Assert.Equal("Armoury Crate: 1 service, running now", found.Describe());
    }

    [Fact]
    public void PredatorSenseDesktop_IsNeitherClosedNorBlocked_TheRestOfTheAcerStackStaysOff()
    {
        // The user starts Predator Sense for Handheld for Acer's BIOS updates (2026-11-07).
        var predator = OtherManagers.Known.Single(manager => manager.Id == "acer-predator-handheld");

        Assert.Equal(["Handheld.Widget"], predator.Processes);
        Assert.Equal(["ULICTekInc.PredatorSenseforHandHeldWidget_nt9dgb7efx6bt"], predator.PackageFamilies);
        Assert.Equal(9, predator.Services.Count);
        Assert.Contains("PredatorHandheldLauncher", predator.Tasks);
        Assert.NotNull(predator.DedicatedKeyboardDeviceId);
    }

    [Fact]
    public void RestoreUnblocked_PutsBackOnlyTheDesktopPackage_Once()
    {
        using var temporary = new TemporaryConfigStore();
        OtherManagerRecord desktop = new()
        {
            ManagerId = "acer-predator-handheld", Kind = "package",
            Name = "ULICTekInc.PredatorSenseforHandHeld_1.0.1563.0_x64__nt9dgb7efx6bt"
        };
        OtherManagerRecord widget = new()
        {
            ManagerId = "acer-predator-handheld", Kind = "package",
            Name = "ULICTekInc.PredatorSenseforHandHeldWidget_1.0.1563.0_x64__nt9dgb7efx6bt"
        };
        OtherManagerRecord launcher = new()
        {
            ManagerId = "acer-predator-handheld", Kind = "task", Name = @"\PredatorHandheldLauncher"
        };
        foreach (var entry in new[] { desktop, widget, launcher })
        {
            OtherManagers.Record(temporary.Store, entry);
        }

        FakeTargets refusing = new() { RestoreResult = false };
        Assert.Equal(1, OtherManagers.RestoreUnblocked(temporary.Store, new FakeServices(), refusing));
        Assert.Equal([desktop.Name], refusing.Restored.Select(entry => entry.Name));
        Assert.Equal(3, temporary.Store.Read().RequireConfig().OtherManagersDisabled.Count);

        FakeTargets accepting = new();
        Assert.Equal(0, OtherManagers.RestoreUnblocked(temporary.Store, new FakeServices(), accepting));
        Assert.Equal([desktop.Name], accepting.Restored.Select(entry => entry.Name));
        Assert.Equal([widget.Name, launcher.Name],
            temporary.Store.Read().RequireConfig().OtherManagersDisabled.Select(entry => entry.Name));

        FakeTargets again = new();
        Assert.Equal(0, OtherManagers.RestoreUnblocked(temporary.Store, new FakeServices(), again));
        Assert.Empty(again.Restored);
    }

    [Fact]
    public void RestoreUnblocked_PutsBackAfzsvcOnlyWhileItIsStillDisabled_AndKeepsTheOtherAcerServicesOff()
    {
        // The tester's built-in controller needed AFZsvc after Acer's component update (2026-11-08).
        var predator = OtherManagers.Known.Single(manager => manager.Id == "acer-predator-handheld");
        Assert.DoesNotContain("AFZsvc", predator.Services);
        Assert.Contains("AASHHSvc", predator.Services);

        using var temporary = new TemporaryConfigStore();
        OtherManagerRecord focus = new()
        {
            ManagerId = "acer-predator-handheld", Kind = "service", Name = "AFZsvc", PreviousStart = 2
        };
        OtherManagerRecord central = new()
        {
            ManagerId = "acer-predator-handheld", Kind = "service", Name = "AASHHSvc", PreviousStart = 2
        };
        OtherManagers.Record(temporary.Store, focus);
        OtherManagers.Record(temporary.Store, central);
        FakeServices services = new();
        services.Services["AFZsvc"] = (4, false);
        services.Services["AASHHSvc"] = (4, false);

        Assert.Equal(0, OtherManagers.RestoreUnblocked(temporary.Store, services, new FakeTargets()));

        Assert.Equal(["AFZsvc=2", "start AFZsvc"], services.Calls);
        Assert.Equal((4, false), services.Services["AASHHSvc"]);
        Assert.Equal(["AASHHSvc"], temporary.Store.Read().RequireConfig().OtherManagersDisabled.Select(entry => entry.Name));
    }

    [Fact]
    public void RestoreUnblocked_LeavesAnAfzsvcTheUserChangedAsItIs()
    {
        using var temporary = new TemporaryConfigStore();
        OtherManagers.Record(temporary.Store, new OtherManagerRecord
        {
            ManagerId = "acer-predator-handheld", Kind = "service", Name = "AFZsvc", PreviousStart = 2
        });
        FakeServices services = new();
        services.Services["AFZsvc"] = (3, false);

        Assert.Equal(0, OtherManagers.RestoreUnblocked(temporary.Store, services, new FakeTargets()));

        Assert.Empty(services.Calls);
        Assert.Empty(temporary.Store.Read().RequireConfig().OtherManagersDisabled);
    }

    [Fact]
    public void DescribeRecords_NamesTheManagersAndCountsWhatWasTurnedOff()
    {
        List<OtherManagerRecord> records =
        [
            new() { ManagerId = "handheld-companion", Kind = "task", Name = @"\HC Autostart" },
            new() { ManagerId = "armoury-crate", Kind = "service", Name = "AsusAppService", PreviousStart = 2 },
            new()
            {
                ManagerId = "armoury-crate", Kind = "service", Name = "ArmouryCrateControlInterface", PreviousStart = 2
            }
        ];

        Assert.Equal("Armoury Crate and Handheld Companion: 2 services and 1 scheduled task are turned off",
            OtherManagers.DescribeRecords(records));
        Assert.Equal("", OtherManagers.DescribeRecords([]));
    }

    [Fact]
    public void Disable_NeverEndsAProcess_ItOnlyReportsWhatStayedOpen()
    {
        FakeServices services = new();
        var detected = OtherManagers.Detect(new FakeAutostartSystem(), services, ["HandheldCompanion"]);

        var result = OtherManagers.Disable(detected, _ => { }, new FakeAutostartSystem(), services,
            running => running);

        Assert.Equal(["HandheldCompanion"], result.StillRunning);
        Assert.Empty(services.Calls);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\HC\\HandheldCompanion.exe\" --minimized", "HandheldCompanion")]
    [InlineData("C:\\Tools\\LegionSpace.exe", "LegionSpace")]
    public void ExecutableName_ReadsQuotedAndPlainCommands(string command, string name)
    {
        Assert.Equal(name, OtherManagers.ExecutableName(command));
    }

    private sealed class FakeServices : IServiceSystem
    {
        internal Dictionary<string, (int Start, bool Delayed)> Services { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        internal List<string> Calls { get; } = [];

        public int? ReadStart(string service, out bool delayed)
        {
            delayed = Services.TryGetValue(service, out var state) && state.Delayed;
            return Services.TryGetValue(service, out state) ? state.Start : null;
        }

        public bool SetStart(string service, int start, bool delayed)
        {
            Calls.Add($"{service}={start}{(delayed ? " delayed" : "")}");
            Services[service] = (start, delayed);
            return true;
        }

        public bool Stop(string service)
        {
            Calls.Add("stop " + service);
            return true;
        }

        public bool Start(string service)
        {
            Calls.Add("start " + service);
            return true;
        }
    }

    private sealed class FakeTargets : IOtherManagerTargets
    {
        internal List<OtherManagerRecord> Restored { get; } = [];

        internal bool RestoreResult { get; init; } = true;

        public bool RestartRequired => false;

        public bool MatchesDevice(string definitionId)
        {
            return false;
        }

        public IReadOnlyList<OtherManagerRecord> Detect(OtherManager manager)
        {
            return [];
        }

        public bool Disable(OtherManagerRecord target)
        {
            throw new InvalidOperationException("Restore must not disable anything.");
        }

        public bool Restore(OtherManagerRecord target)
        {
            Restored.Add(target);
            return RestoreResult;
        }
    }
}
