using WSGM.Plugin.Gpu;
using WSGM.Testing;
using Xunit;

namespace WSGM.Plugin.NvidiaGpu.Tests;

public sealed class DriverStateFileTests
{
    [Fact]
    public void OnlyAMissingFileSeedsEmptyState()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Root, "journal.json");
        var empty = new Dictionary<string, string>();

        Assert.Same(empty, DriverStateFile.Read(path, empty));
        Directory.CreateDirectory(path);
        Assert.Throws<DriverFailure>(() => DriverStateFile.Read(path, empty));
        Assert.True(Directory.Exists(path));
    }

    [Theory]
    [InlineData("invalid json")]
    [InlineData("null")]
    public void ACorruptJournalIsRefusedAndPreserved(string content)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Root, "journal.json");
        File.WriteAllText(path, content);

        Assert.Throws<DriverFailure>(() => DriverStateFile.Read(path, new Dictionary<string, string>()));
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void ALockedJournalIsRefusedAndPreserved()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Root, "journal.json");
        const string content = "{\"owned\":\"original\"}";
        File.WriteAllText(path, content);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<DriverFailure>(() => DriverStateFile.Read(path, new Dictionary<string, string>()));
        }

        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void ALargeValidJournalIsKeptWhole()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Root, "journal.json");
        var value = new string('x', 5 * 1024 * 1024);
        DriverStateFile.Write(path, new Dictionary<string, string> { ["owned"] = value });

        Assert.Equal(value, DriverStateFile.Read(path, new Dictionary<string, string>())["owned"]);
    }
}
