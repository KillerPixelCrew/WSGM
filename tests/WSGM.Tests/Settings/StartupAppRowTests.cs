using WSGM.Settings;

namespace WSGM.Tests;

public sealed class StartupAppRowTests
{
    [Fact]
    public void StartupAppRowRaisesAPropertyChangeForEachEditedValue()
    {
        var row = new StartupAppRow();
        var changed = new List<string>();
        row.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);

        row.Path = "C:\\Tools\\app.exe";
        row.Args = "--silent";
        row.Enabled = false;
        row.Elevated = true;
        row.AutoRelaunch = true;

        Assert.Equal(
            ["Path", "Args", "Enabled", "Elevated", "AutoRelaunch"],
            changed);
    }
}
