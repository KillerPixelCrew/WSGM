using System.Text;
using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>Ubisoft discovery over in-memory installs, product cache and protocol registrations.</summary>
public sealed class UbisoftLibrarySourceTests
{
    private const string Launcher = @"C:\Ubisoft\Ubisoft Game Launcher";
    private const string LocalAppData = @"C:\Users\Player\AppData\Local";

    private const string Cache =
        @"C:\Users\Player\AppData\Local\Ubisoft Game Launcher\cache\configuration\configurations";

    private const string UplayProgram = @"C:\Ubisoft\Ubisoft Game Launcher\upc.exe";

    // Normalised because a checkout may carry CRLF inside the raw literal, and the tests edit it by line.
    private static readonly string Moonlit =
        """
            version: 2.0
            root:
              name: l1
              sort_string: l2
              start_game:
                offline:
                  executables:
                  - path:
                      relative: bin/Moonlit.exe
                    working_directory:
                      register: HKEY_LOCAL_MACHINE\SOFTWARE\Ubisoft\Launcher\Installs\4311\InstallDir
                online:
                  executables:
                    - path:
                        relative: bin/MoonlitOnline.exe
              addons:
                - id: 999
                  name: Season Pass
            localizations:
              default:
                l1: "Moonlit Tides"
                l2: moonlit
            """.ReplaceLineEndings("\n");

    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase) { @"D:\Ubisoft\Moonlit" };

    private readonly HashSet<string> _executables = new(StringComparer.OrdinalIgnoreCase)
    {
        $@"{Launcher}\UbisoftConnect.exe"
    };

    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<UbisoftInstall> _installs = [new("4311", "D:/Ubisoft/Moonlit/")];

    private readonly List<UninstallEntry> _uninstall =
    [
        new("Uplay", "Ubisoft Connect", Launcher, "Ubisoft", "", "")
    ];

    private bool _protocolRegistered = true;

    private UbisoftLibrarySource Source()
    {
        return new UbisoftLibrarySource(
            () => _uninstall,
            () => _installs,
            LocalAppData,
            path => _files.GetValueOrDefault(path),
            path => _executables.Contains(path),
            path => _directories.Contains(path),
            uri => _protocolRegistered ? new ProtocolCommand(UplayProgram, $"\"{uri}\"") : null);
    }

    [Fact]
    public async Task AGameStartsDirectlyFirstAndThroughConnectSecond()
    {
        _files[Cache] = CacheFile(Entry(4311, 1, Moonlit));
        _executables.Add(@"D:\Ubisoft\Moonlit\bin\Moonlit.exe");

        var game = Assert.Single(await Source().DiscoverAsync(CancellationToken.None));

        Assert.Equal("ubisoft", game.SourceId);
        Assert.Equal("4311", game.Key);
        Assert.Equal("Moonlit Tides", game.Name);
        Assert.Equal(@"D:\Ubisoft\Moonlit", game.InstallPath);
        Assert.Equal(["direct", "launcher"], game.CommandRoutes.Select(route => route.Id));
        var direct = game.CommandRoutes[0];
        Assert.Equal(@"D:\Ubisoft\Moonlit\bin\Moonlit.exe", direct.Target);
        Assert.Equal(@"D:\Ubisoft\Moonlit", direct.StartDirectory);
        var launcher = game.CommandRoutes[1];
        Assert.Equal(UplayProgram, launcher.Target);
        Assert.Equal("\"uplay://launch/4311/0\"", launcher.LaunchOptions);
        Assert.Equal(direct.Label, game.Launch.Label);
    }

    [Fact]
    public async Task TheOnlineExecutableIsUsedWhenOfflineNamesNone()
    {
        _files[Cache] = CacheFile(Entry(4311, 1, Moonlit.Replace(
            "relative: bin/Moonlit.exe", "register: HKEY_LOCAL_MACHINE", StringComparison.Ordinal)));
        _executables.Add(@"D:\Ubisoft\Moonlit\bin\MoonlitOnline.exe");

        var game = Assert.Single(await Source().DiscoverAsync(CancellationToken.None));

        Assert.Equal(@"D:\Ubisoft\Moonlit\bin\MoonlitOnline.exe", game.CommandRoutes[0].Target);
    }

    [Fact]
    public async Task AGameTheCacheDoesNotKnowKeepsItsFolderNameAndTheLauncherRoute()
    {
        var game = Assert.Single(await Source().DiscoverAsync(CancellationToken.None));

        Assert.Equal("Moonlit", game.Name);
        Assert.Equal("launcher", Assert.Single(game.CommandRoutes).Id);
    }

    [Fact]
    public async Task AGameWithNoUsableRouteIsSkipped()
    {
        _protocolRegistered = false;

        Assert.Empty(await Source().DiscoverAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("  third_party_platform:\n    name: steam\n")]
    [InlineData("  is_ulc: true\n")]
    public async Task ThirdPartyAndDownloadableContentAreNotListed(string extra)
    {
        _files[Cache] = CacheFile(Entry(4311, 1, Moonlit.Replace(
            "  name: l1\n", "  name: l1\n" + extra, StringComparison.Ordinal)));

        Assert.Empty(await Source().DiscoverAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AProductWithoutStartGameIsNotListed()
    {
        _files[Cache] = CacheFile(Entry(4311, 1, "root:\n  name: Moonlit Tides\n"));

        Assert.Empty(await Source().DiscoverAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AnotherProductsAddOnIsNotListed()
    {
        _installs.Add(new UbisoftInstall("999", @"D:\Ubisoft\SeasonPass"));
        _directories.Add(@"D:\Ubisoft\SeasonPass");
        _files[Cache] = CacheFile(
            Entry(4311, 1, Moonlit),
            Entry(999, 2, "root:\n  name: Season Pass\n  start_game:\n    offline:\n      executables: []\n"));

        var game = Assert.Single(await Source().DiscoverAsync(CancellationToken.None));

        Assert.Equal("4311", game.Key);
    }

    [Fact]
    public async Task TheCacheBesideTheLauncherIsReadWhenTheUserCacheIsMissing()
    {
        _files[$@"{Launcher}\cache\configuration\configurations"] = CacheFile(Entry(4311, 1, Moonlit));

        Assert.Equal("Moonlit Tides", Assert.Single(await Source().DiscoverAsync(CancellationToken.None)).Name);
    }

    [Fact]
    public async Task NothingIsFoundWhenConnectIsNotInstalled()
    {
        _uninstall.Clear();

        var source = Source();

        Assert.Equal(SourceAvailability.NotFound, source.Detect());
        Assert.Empty(await source.DiscoverAsync(CancellationToken.None));
    }

    [Fact]
    public void ATruncatedCacheYieldsTheEntriesBeforeTheBreak()
    {
        var whole = CacheFile(Entry(4311, 1, Moonlit), Entry(5000, 2, Moonlit));

        var products = UbisoftConfigurations.Parse(whole.AsSpan(0, whole.Length - 10));

        Assert.Equal(4311u, Assert.Single(products).UplayId);
    }

    [Fact]
    public void UnknownFieldsAreSkippedByTheirWireType()
    {
        List<byte> bytes = [];
        bytes.AddRange([0x10, 0x05]); // field 2, varint
        bytes.AddRange([0x1D, 1, 2, 3, 4]); // field 3, fixed32
        bytes.AddRange([0x21, 1, 2, 3, 4, 5, 6, 7, 8]); // field 4, fixed64
        bytes.AddRange(CacheFile(Entry(4311, 1, Moonlit)));

        Assert.Equal("Moonlit Tides", Assert.Single(UbisoftConfigurations.Parse(bytes.ToArray())).Name);
    }

    [Fact]
    public void GarbageNeverThrows()
    {
        byte[] garbage = [0x0A, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F, 0x01];

        Assert.Empty(UbisoftConfigurations.Parse(garbage));
    }

    [Fact]
    public void TheYamlReaderNestsSequencesOfMappings()
    {
        var product = UbisoftConfigurations.Describe(4311, 1, Moonlit);

        Assert.NotNull(product);
        Assert.Equal("bin/Moonlit.exe", product.Executable);
        Assert.Equal([999u], product.Addons);
        Assert.True(product.HasStartGame);
        Assert.False(product.ThirdParty);
    }

    private static byte[] Entry(uint uplayId, uint installId, string yaml)
    {
        List<byte> bytes = [0x08];
        bytes.AddRange(Varint(uplayId));
        bytes.Add(0x10);
        bytes.AddRange(Varint(installId));
        bytes.Add(0x1A);
        var text = Encoding.UTF8.GetBytes(yaml);
        bytes.AddRange(Varint((ulong)text.Length));
        bytes.AddRange(text);
        return bytes.ToArray();
    }

    private static byte[] CacheFile(params byte[][] entries)
    {
        List<byte> bytes = [];
        foreach (var entry in entries)
        {
            bytes.Add(0x0A);
            bytes.AddRange(Varint((ulong)entry.Length));
            bytes.AddRange(entry);
        }

        return bytes.ToArray();
    }

    private static List<byte> Varint(ulong value)
    {
        List<byte> bytes = [];
        do
        {
            var current = (byte)(value & 0x7F);
            value >>= 7;
            bytes.Add(value == 0 ? current : (byte)(current | 0x80));
        } while (value != 0);

        return bytes;
    }
}
