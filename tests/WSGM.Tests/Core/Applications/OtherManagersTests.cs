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
    public void PredatorSense_IsNoLongerTurnedOff_OnlyItsDedicatedKeyboardIs()
    {
        // Acer delivers Atlas BIOS updates through Predator Sense (2026-11-07).
        var predator = OtherManagers.Known.Single(manager => manager.Id == "acer-predator-handheld");
        FakeAutostartSystem autostart = new();
        autostart.TaskEnabled[@"\PredatorHandheldLauncher"] = true;
        FakeServices services = new();
        services.Services["AcerServiceSvc"] = (2, false);
        services.Services["AASHHSvc"] = (2, false);

        Assert.Empty(predator.Processes);
        Assert.Empty(predator.Services);
        Assert.Empty(predator.Tasks);
        Assert.Empty(predator.PackageFamilies);
        Assert.NotNull(predator.DedicatedKeyboardDeviceId);
        Assert.Empty(OtherManagers.Detect(autostart, services, ["Handheld.Desktop", "Handheld.Widget"]));
    }

    [Fact]
    public void Retired_SelectsOnlyChangesTheCatalogNoLongerMakes()
    {
        OtherManagerRecord service = new()
        {
            ManagerId = "acer-predator-handheld", Kind = "service", Name = "AcerServiceSvc", PreviousStart = 2
        };
        OtherManagerRecord task = new()
        {
            ManagerId = "acer-predator-handheld", Kind = "task", Name = @"\PredatorHandheldLauncher"
        };
        OtherManagerRecord package = new()
        {
            ManagerId = "acer-predator-handheld", Kind = "package",
            Name = "ULICTekInc.PredatorSenseforHandHeld_1.0.1563.0_x64__nt9dgb7efx6bt"
        };
        OtherManagerRecord keyboard = new()
        {
            ManagerId = "acer-predator-handheld", Kind = "device",
            Name = @"HID\VID_0502&PID_F901&MI_02&COL01\7&338D4E88&0&0000"
        };
        OtherManagerRecord companionTask = new()
        {
            ManagerId = "handheld-companion", Kind = "task", Name = @"\HC Autostart"
        };
        OtherManagerRecord msiService = new()
        {
            ManagerId = "msi-center-m", Kind = "service", Name = "MSI Foundation Service", PreviousStart = 2
        };
        OtherManagerRecord unknown = new() { ManagerId = "retired-app", Kind = "service", Name = "Gone" };

        var retired = OtherManagers.Retired([service, task, package, keyboard, companionTask, msiService, unknown]);

        Assert.Equal([service, task, package, unknown], retired);
    }

    [Fact]
    public void RestoreRetired_PutsBackPredatorSenseOnce_AndKeepsTheKeyboardAndARefusedChangeRecorded()
    {
        using var temporary = new TemporaryConfigStore();
        OtherManagerRecord launcher = new()
        {
            ManagerId = "acer-predator-handheld", Kind = "task", Name = @"\PredatorHandheldLauncher"
        };
        OtherManagerRecord bridge = new()
        {
            ManagerId = "acer-predator-handheld", Kind = "service", Name = "AcerServiceSvc", PreviousStart = 2
        };
        OtherManagerRecord package = new()
        {
            ManagerId = "acer-predator-handheld", Kind = "package",
            Name = "ULICTekInc.PredatorSenseforHandHeld_1.0.1563.0_x64__nt9dgb7efx6bt"
        };
        OtherManagerRecord keyboard = new()
        {
            ManagerId = "acer-predator-handheld", Kind = "device",
            Name = @"HID\VID_0502&PID_F901&MI_02&COL01\7&338D4E88&0&0000"
        };
        foreach (var entry in new[] { launcher, bridge, package, keyboard })
        {
            OtherManagers.Record(temporary.Store, entry);
        }

        FakeAutostartSystem autostart = new();
        autostart.TaskEnabled[launcher.Name] = false;
        FakeServices services = new();
        services.Services[bridge.Name] = (4, false);
        FakeTargets targets = new() { RestoreResult = false };

        Assert.Equal(1, OtherManagers.RestoreRetired(temporary.Store, autostart, services, targets));

        Assert.True(autostart.TaskEnabled[launcher.Name]);
        Assert.Equal((2, false), services.Services[bridge.Name]);
        Assert.Equal([package.Name], targets.Restored.Select(entry => entry.Name));
        Assert.Equal([Key(package), Key(keyboard)],
            temporary.Store.Read().RequireConfig().OtherManagersDisabled.Select(Key));

        // A second setup writes nothing the first one settled; the refused package is tried again.
        FakeAutostartSystem again = new();
        FakeServices untouched = new();
        FakeTargets accepting = new();
        Assert.Equal(0, OtherManagers.RestoreRetired(temporary.Store, again, untouched, accepting));
        Assert.Empty(again.Writes);
        Assert.Empty(untouched.Calls);
        Assert.Equal([package.Name], accepting.Restored.Select(entry => entry.Name));
        Assert.Equal([Key(keyboard)], temporary.Store.Read().RequireConfig().OtherManagersDisabled.Select(Key));
    }

    private static string Key(OtherManagerRecord entry)
    {
        return entry.Kind + "|" + entry.Name;
    }

    [Fact]
    public void Restore_PutsBackOnlyWhatStillHoldsWsgmsValue()
    {
        FakeAutostartSystem autostart = new();
        autostart.TaskEnabled[@"\PredatorHandheldLauncher"] = false;
        autostart.TaskEnabled[@"\UEIPInvitation"] = true;
        FakeServices services = new();
        services.Services["AcerServiceSvc"] = (4, false);
        services.Services["AASHHSvc"] = (3, false);
        List<OtherManagerRecord> records =
        [
            new() { ManagerId = "acer-predator-handheld", Kind = "task", Name = @"\PredatorHandheldLauncher" },
            new() { ManagerId = "acer-predator-handheld", Kind = "task", Name = @"\UEIPInvitation" },
            new()
            {
                ManagerId = "acer-predator-handheld", Kind = "service", Name = "AcerServiceSvc", PreviousStart = 2,
                PreviousDelayed = true
            },
            new() { ManagerId = "acer-predator-handheld", Kind = "service", Name = "AASHHSvc", PreviousStart = 2 }
        ];

        var restored = OtherManagers.Restore(records, autostart, services, new FakeTargets());

        // Every record is settled, but the task and service the user changed afterwards keep their state.
        Assert.Equal(records, restored);
        Assert.Equal([@"task/\PredatorHandheldLauncher=True"], autostart.Writes);
        Assert.Equal(["AcerServiceSvc=2 delayed", "start AcerServiceSvc"], services.Calls);
        Assert.Equal((3, false), services.Services["AASHHSvc"]);
    }

    [Fact]
    public void Restore_KeepsARefusedChangeRecorded_WithoutRetrying()
    {
        FakeServices services = new() { WritesFail = true };
        services.Services["AcerServiceSvc"] = (4, false);
        FakeTargets targets = new() { RestoreResult = false };
        OtherManagerRecord service = new()
        {
            ManagerId = "acer-predator-handheld", Kind = "service", Name = "AcerServiceSvc", PreviousStart = 2
        };
        OtherManagerRecord package = new()
        {
            ManagerId = "acer-predator-handheld", Kind = "package",
            Name = "ULICTekInc.PredatorSenseforHandHeld_1.0.1563.0_x64__nt9dgb7efx6bt"
        };

        var restored = OtherManagers.Restore([service, package], new FakeAutostartSystem(), services, targets);

        Assert.Empty(restored);
        Assert.Equal(["AcerServiceSvc=2"], services.Calls);
        Assert.Equal([package], targets.Restored);
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

        internal bool WritesFail { get; init; }

        public int? ReadStart(string service, out bool delayed)
        {
            delayed = Services.TryGetValue(service, out var state) && state.Delayed;
            return Services.TryGetValue(service, out state) ? state.Start : null;
        }

        public bool SetStart(string service, int start, bool delayed)
        {
            Calls.Add($"{service}={start}{(delayed ? " delayed" : "")}");
            if (WritesFail)
            {
                return false;
            }

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
