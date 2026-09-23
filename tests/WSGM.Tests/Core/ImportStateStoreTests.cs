using WSGM.Core;
using WSGM.Device.Tests;

namespace WSGM.Tests.Core;

/// <summary>
///     The import records on disk. The file is user-reachable, so the load path's job is to make a
///     hand-edited or corrupted one harmless rather than to trust it.
/// </summary>
public sealed class ImportStateStoreTests
{
    [Fact]
    public void ARecordWithANullStringIsDroppedRatherThanThrowing()
    {
        // Dereferencing one threw out of the scan, and out of every scan after it, so the importer
        // stayed unusable until the user found and deleted the file themselves.
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("library-import.json");
        File.WriteAllText(path, """
                                {"Entries":[
                                  {"Source":"xbox","Key":"A_x!App","Name":null,"Target":null,
                                   "LaunchOptions":null,"Mode":"ControllerOnly"},
                                  {"Source":"xbox","Key":"B_y!App","Name":"Kept","Target":"t",
                                   "LaunchOptions":"o","Mode":"ControllerOnly"}
                                ]}
                                """);

        var entries = new ImportStateStore(path).Entries();

        Assert.Equal("B_y!App", Assert.Single(entries).Key);
    }

    [Fact]
    public void AnOversizedOrUnknownRecordIsDropped()
    {
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("library-import.json");
        File.WriteAllText(path, $$"""
                                  {"Entries":[
                                    {"Source":"xbox","Key":"{{new string('k', 600)}}","Name":"Long",
                                     "Target":"t","LaunchOptions":"o","Mode":"ControllerOnly"},
                                    {"Source":"xbox","Key":"C_z!App","Name":"Mode","Target":"t",
                                     "LaunchOptions":"o","Mode":"NotAMode"}
                                  ]}
                                  """);

        Assert.Empty(new ImportStateStore(path).Entries());
    }

    [Fact]
    public void AFileThatIsNotJsonLeavesAnEmptyStoreRatherThanFailing()
    {
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("library-import.json");
        File.WriteAllText(path, "not json at all");

        Assert.Empty(new ImportStateStore(path).Entries());
    }

    [Fact]
    public void ASavedRecordSurvivesAReload()
    {
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("library-import.json");
        new ImportStateStore(path).Save(new ImportedEntry
        {
            Source = "xbox", Key = "A_x!App", Name = "Moonlit", AppId = 42,
            Target = "t", LaunchOptions = "o", Mode = nameof(ImportMode.SteamIntegration)
        });

        var entry = Assert.Single(new ImportStateStore(path).Entries());

        Assert.Equal(42u, entry.AppId);
        Assert.Equal(nameof(ImportMode.SteamIntegration), entry.Mode);
    }
}
