using WSGM.Setup.Engine;
using WSGM.Testing;

namespace WSGM.Tests.Setup;

public sealed class WindowsSetupTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HeldFilesAreScheduledBeforeTheirDirectoriesAndEveryRefusalIsReported(bool accepted)
    {
        using TemporaryDirectory temporary = new();
        var previousLog = SetupLog.Path;
        SetupLog.Path = temporary.GetPath("setup.log");
        var root = temporary.GetPath("locked");
        var child = Path.Combine(root, "child");
        var nested = Path.Combine(child, "nested");
        Directory.CreateDirectory(nested);
        var first = Path.Combine(root, "first.bin");
        var second = Path.Combine(nested, "second.bin");
        File.WriteAllText(first, "first");
        File.WriteAllText(second, "second");
        using var firstHandle = File.Open(first, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var secondHandle = File.Open(second, FileMode.Open, FileAccess.Read, FileShare.Read);
        List<string> scheduled = [];
        try
        {
            var result = WindowsSetup.DeleteOrScheduleAtReboot(root, path =>
            {
                scheduled.Add(path);
                // Refuse only one file: later files and directories must still be attempted.
                return path != first || accepted;
            });

            Assert.Equal(accepted, result);
            Assert.Equal(5, scheduled.Count);
            Assert.Equal(new[] { first, second }.Order(), scheduled.Take(2).Order());
            Assert.Equal([nested, child, root], scheduled.Skip(2));
            Assert.True(File.Exists(first));
            Assert.True(File.Exists(second));
            var log = File.ReadAllText(SetupLog.Path);
            Assert.Equal(accepted, log.Contains("is deleted at the next restart", StringComparison.Ordinal));
        }
        finally
        {
            SetupLog.Path = previousLog;
        }
    }

    [Fact]
    public void ImmediateDeletionAndMissingPathsDoNotScheduleAnything()
    {
        using TemporaryDirectory temporary = new();
        var file = temporary.GetPath("unlocked.bin");
        File.WriteAllText(file, "data");
        var schedules = 0;

        bool Schedule(string path)
        {
            schedules++;
            return false;
        }

        Assert.True(WindowsSetup.DeleteOrScheduleAtReboot(file, Schedule));
        Assert.True(WindowsSetup.DeleteOrScheduleAtReboot(file, Schedule));
        Assert.False(File.Exists(file));
        Assert.Equal(0, schedules);
    }
}
