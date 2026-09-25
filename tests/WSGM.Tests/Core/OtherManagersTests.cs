using WSGM.Core;

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
        // The live IsTaskEnabled reads a missing task as enabled; detection must not rely on it.
        FakeAutostartSystem autostart = new() { MissingTasksReadEnabled = true };

        Assert.Empty(OtherManagers.Detect(autostart, new FakeServices(), []));
    }

    [Fact]
    public void Restore_TreatsATaskRemovedMeanwhileAsRestored()
    {
        FakeAutostartSystem autostart = new() { TaskWritesFail = true, MissingTasksReadEnabled = true };
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
        Assert.False(autostart.IsTaskEnabled(@"\MSI_Center_M_Server"));
        Assert.Equal(4, services.Services["MSI Foundation Service"].Start);
        Assert.Contains("stop MSI Foundation Service", services.Calls);

        var restored = OtherManagers.Restore(records, autostart, services);

        Assert.Equal(2, restored.Count);
        Assert.True(autostart.IsTaskEnabled(@"\MSI_Center_M_Server"));
        Assert.Equal((2, true), services.Services["MSI Foundation Service"]);
        Assert.Contains("start MSI Foundation Service", services.Calls);
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
}
