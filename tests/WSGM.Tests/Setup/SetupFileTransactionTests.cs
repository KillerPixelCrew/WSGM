using System.Text.Json;
using WSGM.Setup.Engine;
using WSGM.Testing;

namespace WSGM.Tests.Setup;

public sealed class SetupFileTransactionTests
{
    [Fact]
    public void InterruptedReplacementRestoresApplicationPluginsAndRepairMetadataTogether()
    {
        using TemporaryDirectory temporary = new();
        var root = temporary.GetPath("installation");
        var data = temporary.GetPath("data");
        Write(root, "App/app.exe", "old-app");
        Write(root, "Plugins/device/plugin.dll", "old-plugin");
        Write(root, "Setup/Packages/device.zip", "old-package");
        Write(root, "Setup/WSGM.Setup.exe", "old-setup");
        Write(data, "bundle.json", "old-bundle");
        var version = "2.0.0.1";
        SetupFileTransaction transaction = new(root, data, () => version, saved => version = saved);
        transaction.Begin();
        Directory.Move(Path.Combine(root, "App"), Path.Combine(root, "App.previous"));
        Write(root, "App/app.exe", "new-app");
        Write(root, "Plugins/device/plugin.dll", "new-plugin");
        Write(root, "Plugins/added/plugin.dll", "new-only");
        Write(root, "Setup/Packages/device.zip", "new-package");
        Write(root, "Setup/WSGM.Setup.exe", "new-setup");
        Write(data, "bundle.json", "new-bundle");
        version = "2.0.0.2";

        new SetupFileTransaction(root, data, () => version, saved => version = saved).Recover();

        Assert.Equal("old-app", Read(root, "App/app.exe"));
        Assert.Equal("old-plugin", Read(root, "Plugins/device/plugin.dll"));
        Assert.False(Directory.Exists(Path.Combine(root, "Plugins", "added")));
        Assert.Equal("old-package", Read(root, "Setup/Packages/device.zip"));
        Assert.Equal("old-setup", Read(root, "Setup/WSGM.Setup.exe"));
        Assert.Equal("old-bundle", Read(data, "bundle.json"));
        Assert.Equal("2.0.0.1", version);
        Assert.False(File.Exists(Path.Combine(data, "setup-transaction.json")));
    }

    [Fact]
    public void CommittedJournalOnlyCleansBackupsAndRetainsTheNewInstallation()
    {
        using TemporaryDirectory temporary = new();
        var root = temporary.GetPath("installation");
        var data = temporary.GetPath("data");
        Write(root, "App/app.exe", "new");
        Write(root, "App.previous/app.exe", "old");
        Write(data, "setup-transaction.json", JsonSerializer.Serialize(new
        {
            Schema = 1, Committed = true, Existing = new[] { "App" }, BackedUp = Array.Empty<string>()
        }));

        new SetupFileTransaction(root, data).Recover();

        Assert.Equal("new", Read(root, "App/app.exe"));
        Assert.False(Directory.Exists(Path.Combine(root, "App.previous")));
    }

    [Fact]
    public void UnsupportedJournalRetainsTheBackupsAndRefusesRecovery()
    {
        using TemporaryDirectory temporary = new();
        var root = temporary.GetPath("installation");
        var data = temporary.GetPath("data");
        Write(root, "App.previous/app.exe", "old");
        Write(data, "setup-transaction.json", """{"Schema":1,"Existing":[null],"BackedUp":[]}""");

        Assert.Throws<InvalidDataException>(() => new SetupFileTransaction(root, data).Recover());
        Assert.Equal("old", Read(root, "App.previous/app.exe"));
    }

    [Fact]
    public void ANewTargetIsRemovedOnRollbackAndLegacyMissingAppIsRecovered()
    {
        using TemporaryDirectory temporary = new();
        var root = temporary.GetPath("installation");
        var data = temporary.GetPath("data");
        SetupFileTransaction transaction = new(root, data);
        transaction.Begin();
        Write(root, "Plugins/new/plugin.dll", "new");
        transaction.RollBack();
        Assert.False(Directory.Exists(Path.Combine(root, "Plugins")));
        Write(root, "App.previous/app.exe", "old");
        new SetupFileTransaction(root, data).Recover();
        Assert.Equal("old", Read(root, "App/app.exe"));
    }

    [Fact]
    public void ExecutingNewRepairImageIsRetainedWhileTheAdvertisedOldSetupIsRestored()
    {
        using TemporaryDirectory temporary = new();
        var root = temporary.GetPath("installation");
        var data = temporary.GetPath("data");
        var target = Path.Combine(root, "Setup", "WSGM.Setup.exe");
        Write(root, "Setup/WSGM.Setup.exe", "old-setup");
        var executing = target;
        SetupFileTransaction transaction = new(root, data, executingSetup: () => executing,
            relocatedSetup: path => executing = path);
        transaction.Begin();
        File.WriteAllText(target, "running-new-setup");
        transaction.RollBack();
        Assert.NotEqual(target, executing);
        Assert.Equal("running-new-setup", File.ReadAllText(executing!));
        Assert.Equal("old-setup", File.ReadAllText(target));
        Assert.False(File.Exists(Path.Combine(data, "setup-transaction.json")));
    }

    [Fact]
    public void InterruptedFreshInstallRetiresItsExecutingSetupAndRemovesTheAdvertisedTarget()
    {
        using TemporaryDirectory temporary = new();
        var root = temporary.GetPath("installation");
        var data = temporary.GetPath("data");
        var target = Path.Combine(root, "Setup", "WSGM.Setup.exe");
        var executing = target;
        SetupFileTransaction transaction = new(root, data, executingSetup: () => executing,
            relocatedSetup: path => executing = path);
        transaction.Begin();
        Write(root, "Setup/WSGM.Setup.exe", "running-new-setup");
        transaction.RollBack();
        Assert.False(File.Exists(target));
        Assert.NotEqual(target, executing);
        Assert.Equal("running-new-setup", File.ReadAllText(executing!));
        Assert.False(File.Exists(Path.Combine(data, "setup-transaction.json")));
    }

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Read(string root, string relative)
    {
        return File.ReadAllText(Path.Combine(root, relative));
    }
}
