using WSGM.Device.Tests;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Tests.Wizard;

public sealed class LabProjectTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_ThenOpen_RoundTripsTheManifest()
    {
        using TemporaryDirectory temporary = new();
        var directory = Path.Combine(temporary.Root, "test");

        var created = LabProject.Create(directory, LabStages.Ids, "1.2.3", Now);
        created.SetDevice(new LabDeviceIdentity { RecordId = "wsgm.claw-8-a2vm", DisplayName = "Claw" });
        var opened = LabProject.Open(directory);

        Assert.Equal(created.Manifest.Id, opened.Manifest.Id);
        Assert.Equal("wsgm.claw-8-a2vm", opened.Manifest.Device.RecordId);
        Assert.Equal(LabStages.Ids, opened.Manifest.Segments.Select(segment => segment.Id));
        Assert.All(opened.Manifest.Segments, segment => Assert.Equal(LabSegmentStatus.NotStarted, segment.Status));
    }

    [Fact]
    public void Create_RefusesAnExistingDirectory()
    {
        using TemporaryDirectory temporary = new();

        Assert.Throws<IOException>(() => LabProject.Create(temporary.Root, LabStages.Ids, "1.0.0", Now));
    }

    [Fact]
    public void Redo_KeepsEveryEarlierAttempt()
    {
        using TemporaryDirectory temporary = new();
        var project = LabProject.Create(Path.Combine(temporary.Root, "test"), LabStages.Ids, "1.0.0", Now);

        var first = project.BeginAttempt("buttons/a", Now);
        project.WriteEvidence(first, "press", new { Seen = "report" });
        project.Finish("buttons/a", LabSegmentStatus.Completed, "A", Now);
        var second = project.BeginAttempt("buttons/a", Now);

        Assert.NotEqual(first, second);
        Assert.True(File.Exists(Path.Combine(first, "press.json")));
        Assert.Equal(second, project.CurrentAttemptDirectory("buttons/a"));
        var state = LabProject.Open(project.Directory).Segment("buttons/a");
        Assert.Equal(2, state.Attempts);
        Assert.Equal(LabSegmentStatus.NotStarted, state.Status);
    }

    [Fact]
    public void Finish_RequiresAnAttempt()
    {
        using TemporaryDirectory temporary = new();
        var project = LabProject.Create(Path.Combine(temporary.Root, "test"), LabStages.Ids, "1.0.0", Now);

        Assert.Throws<InvalidOperationException>(() =>
            project.Finish(LabStages.Preflight, LabSegmentStatus.Completed, null, Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Buttons")]
    [InlineData("buttons//a")]
    [InlineData("../escape")]
    [InlineData("buttons/a b")]
    public void SegmentIds_AreRestrictedToSafePathParts(string id)
    {
        Assert.Throws<ArgumentException>(() => LabProject.ValidateSegmentId(id));
    }

    [Fact]
    public void WriteEvidence_NeverOverwrites()
    {
        using TemporaryDirectory temporary = new();
        var project = LabProject.Create(Path.Combine(temporary.Root, "test"), LabStages.Ids, "1.0.0", Now);
        var attempt = project.BeginAttempt(LabStages.Identity, Now);

        project.WriteEvidence(attempt, "identity", new { Value = 1 });

        Assert.Throws<IOException>(() => project.WriteEvidence(attempt, "identity", new { Value = 2 }));
    }

    [Fact]
    public void Open_RejectsAFolderWithoutAManifest()
    {
        using TemporaryDirectory temporary = new();

        Assert.Throws<InvalidDataException>(() => LabProject.Open(temporary.Root));
    }
}
