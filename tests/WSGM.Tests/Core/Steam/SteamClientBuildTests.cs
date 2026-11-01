using System.Text;
using WSGM.Core;
using WSGM.Testing;

namespace WSGM.Tests.Core;

public sealed class SteamClientBuildTests
{
    private const string Runtime =
        """r.u=e=>({3714:"sp",48:"chunk~a"}[e]||e)+".js?contenthash="+{48:"40e86f93f4be6b3b45de",3714:"3708f234683c51cc73a9"}[e],r.miniCssF=e=>"css/"+({3714:"sp"}[e]||e)+".css?contenthash="+{3714:"4354f29b8976cb732d25"}[e];""";

    private static readonly SteamClientBuild[] Known =
    [
        new("stable", "1792618285", "3708f234683c51cc73a9"),
        new("publicbeta", "1793235415", "7039b9918d21fc46ca79")
    ];

    [Fact]
    public void StableClientReadsItsManifestVersionAndSpHash()
    {
        using TemporaryDirectory steam = new();
        Client(steam.Root, null, "steam_client_win64.manifest", "1792618285");

        var build = SteamClientBuild.Read(steam.Root);

        Assert.Equal(new SteamClientBuild("stable", "1792618285", "3708f234683c51cc73a9"), build);
        Assert.Equal("stable/1792618285", build!.Key);
    }

    [Fact]
    public void BetaClientReadsItsChannelsOwnManifest()
    {
        using TemporaryDirectory steam = new();
        Client(steam.Root, "publicbeta\n", "steam_client_publicbeta_win64.manifest", "1793235415");
        // A stable manifest left beside it from before the switch is not the running build.
        File.WriteAllText(Path.Combine(steam.Root, "package", "steam_client_win64.manifest"),
            "\"win64\"\n{\n\t\"version\"\t\t\"1\"\n}\n");

        var build = SteamClientBuild.Read(steam.Root);

        Assert.Equal("publicbeta", build!.Channel);
        Assert.Equal("1793235415", build.Version);
    }

    [Fact]
    public void AnEmptyBetaFileIsStable()
    {
        using TemporaryDirectory steam = new();
        Client(steam.Root, "  ", "steam_client_win64.manifest", "7");

        Assert.Equal("stable", SteamClientBuild.Read(steam.Root)!.Channel);
    }

    [Fact]
    public void MissingDataReadsNoBuild()
    {
        using TemporaryDirectory steam = new();
        Assert.Null(SteamClientBuild.Read(null));
        Assert.Null(SteamClientBuild.Read(steam.Root));
        // The beta channel's manifest is missing: nothing is claimed about the build.
        Client(steam.Root, "publicbeta", "steam_client_win64.manifest", "1");
        Assert.Null(SteamClientBuild.Read(steam.Root));
    }

    [Fact]
    public void AMissingRuntimeLeavesOnlyTheHashUnknown()
    {
        using TemporaryDirectory steam = new();
        Client(steam.Root, null, "steam_client_win64.manifest", "5");
        File.Delete(Path.Combine(steam.Root, "steamui", "library.js"));

        Assert.Equal(new SteamClientBuild("stable", "5", null), SteamClientBuild.Read(steam.Root));
    }

    [Fact]
    public void SpHashIsTheScriptsNotTheStylesheets()
    {
        Assert.Equal("3708f234683c51cc73a9", SteamClientBuild.ReadSpContenthash(Runtime));
        Assert.Null(SteamClientBuild.ReadSpContenthash("r.u=e=>e+\".js\""));
    }

    [Fact]
    public void ClassifyComparesChannelAndVersion()
    {
        Assert.Equal(SteamClientBuildStatus.Checked,
            SteamClientBuilds.Classify(new SteamClientBuild("stable", "1792618285", null), Known));
        Assert.Equal(SteamClientBuildStatus.Unchecked,
            SteamClientBuilds.Classify(new SteamClientBuild("stable", "1793235415", null), Known));
        Assert.Equal(SteamClientBuildStatus.Unchecked,
            SteamClientBuilds.Classify(new SteamClientBuild("publicbeta", "1792618285", null), Known));
        Assert.Equal(SteamClientBuildStatus.Unreadable, SteamClientBuilds.Classify(null, Known));
    }

    [Fact]
    public void ParseReadsTheListAndRefusesAnIncompleteEntry()
    {
        using MemoryStream valid = new(Encoding.UTF8.GetBytes(
            """{"builds":[{"channel":"stable","version":"1","spContenthash":"a","checked":"2026-11-01"},{"channel":"publicbeta","version":"2"}]}"""));
        Assert.Equal([new SteamClientBuild("stable", "1", "a"), new SteamClientBuild("publicbeta", "2", null)],
            SteamClientBuilds.Parse(valid));
        using MemoryStream invalid = new(Encoding.UTF8.GetBytes("""{"builds":[{"channel":"stable"}]}"""));
        Assert.Throws<InvalidDataException>(() => SteamClientBuilds.Parse(invalid));
    }

    [Fact]
    public void TheShippedListHoldsAStableAndABetaBuild()
    {
        var builds = SteamClientBuilds.Checked;

        Assert.Contains(builds, build => build.Channel == "stable");
        Assert.Contains(builds, build => build.Channel == "publicbeta");
        Assert.All(builds, build => Assert.Matches("^[0-9]+$", build.Version));
    }

    private static void Client(string root, string? beta, string manifest, string version)
    {
        var package = Directory.CreateDirectory(Path.Combine(root, "package")).FullName;
        var steamui = Directory.CreateDirectory(Path.Combine(root, "steamui")).FullName;
        if (beta is not null)
        {
            File.WriteAllText(Path.Combine(package, "beta"), beta);
        }

        File.WriteAllText(Path.Combine(package, manifest),
            $"\"win64\"\n{{\n\t\"version\"\t\t\"{version}\"\n\t\"ostype\"\t\t\"win10\"\n}}\n");
        File.WriteAllText(Path.Combine(steamui, "library.js"), Runtime);
    }
}
