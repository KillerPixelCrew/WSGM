using WSGM.Core;
using WSGM.Testing;

namespace WSGM.Tests.Core.Library;

/// <summary>The actual backing content and selected emulator determine launch availability together.</summary>
public sealed class ManagedContentStorageTests
{
    [Fact]
    public void AnExistingEmulatorDoesNotMakeAMissingRomAvailable()
    {
        using TemporaryDirectory temporary = new();
        var executable = temporary.GetPath("emulator.exe");
        File.WriteAllText(executable, "fixture executable");
        EmulatorStore store = new()
        {
            Installations =
            [
                new EmulatorInstallation
                {
                    Id = "installed", ExecutablePath = executable, Systems = ["psx"], LaunchArguments = ["{rom}"]
                }
            ]
        };
        ManagedContentRecord content = new()
        {
            Id = "0123456789abcdef0123456789abcdef", SourceKind = LibrarySourceKind.Rom,
            Name = "Missing disc", Location = "Test library", SystemId = "psx", EmulatorInstallationId = "installed",
            BackingPath = new ManagedContentPath { AbsolutePath = temporary.GetPath("missing.chd") }
        };

        var result = ManagedContentStorage.Check(content, store);

        Assert.False(result.Available);
        Assert.Equal(ContentAvailability.FileMissing, result.Availability);
        Assert.Contains("Test library", result.Detail);
        Assert.Null(result.Launch);
    }

    [Fact]
    public void ASystemPreferenceResolvesItsAliasAndCoreIntoTheCurrentRomCommand()
    {
        using TemporaryDirectory temporary = new();
        var executable = temporary.GetPath("retroarch.exe");
        var corePath = temporary.GetPath("core.dll");
        var romPath = temporary.GetPath("game.md");
        File.WriteAllText(executable, "fixture executable");
        File.WriteAllText(corePath, "fixture core");
        File.WriteAllText(romPath, "fixture content");
        var installed = new EmulatorInstallation
        {
            Id = "installed", DefinitionId = "retroarch", ExecutablePath = executable, DataPath = temporary.Root,
            DataPolicy = EmulatorCatalog.LoadBundled().Definitions.Single(definition => definition.Id == "retroarch")
                .DataPolicy,
            LaunchArguments = ["-L", "{core}", "{rom}"],
            Cores = [new EmulatorCore { Id = "selected-core", Path = corePath, Systems = ["genesis"] }]
        };
        IniFile.SetValues(Path.Combine(temporary.Root, "retroarch.cfg"),
            installed.DataPolicy.ConfigPaths.ToDictionary(pair => pair.Key, pair =>
                '"' + pair.Value.Replace("{program}", temporary.Root).Replace("{data}", temporary.Root) + '"'));
        EmulatorStore store = new()
        {
            Installations = [installed],
            SystemPreferences =
            [
                new EmulatorSystemPreference
                    { SystemId = "genesis", InstallationId = "installed", CoreId = "selected-core" }
            ]
        };
        ManagedContentRecord content = new()
        {
            Id = "0123456789abcdef0123456789abcdef", SourceKind = LibrarySourceKind.Rom,
            Name = "Game", SystemId = "megadrive", FollowSystemPreference = true,
            BackingPath = new ManagedContentPath { AbsolutePath = romPath }
        };

        var result = ManagedContentStorage.Check(content, store);

        Assert.True(result.Available, result.Detail);
        Assert.Equal("installed", result.Content!.EmulatorInstallationId);
        Assert.Equal("selected-core", result.Content.CoreId);
        Assert.Equal(executable, result.Launch!.Executable);
        Assert.Equal(["-c", Path.Combine(temporary.Root, "retroarch.cfg"), "-L", corePath, romPath],
            result.Launch.Arguments);
        Assert.Empty(content.EmulatorInstallationId);
        Assert.Empty(content.CoreId);
    }

    [Theory]
    [InlineData(ContentPathState.Present, ContentAvailability.Available)]
    [InlineData(ContentPathState.Missing, ContentAvailability.FileMissing)]
    [InlineData(ContentPathState.VolumeAbsent, ContentAvailability.LibraryMissing)]
    [InlineData(ContentPathState.Unreadable, ContentAvailability.LibraryMissing)]
    public void EveryPathObservationMapsToOneOfTheThreePublishedStates(ContentPathState state,
        ContentAvailability expected)
    {
        Assert.Equal(expected, ManagedContentStorage.Classify(state));
    }

    [Fact]
    public void ALiteralPathIsPresentOrMissingBelowAReadableFolder()
    {
        using TemporaryDirectory temporary = new();
        var file = temporary.GetPath("game.exe");
        File.WriteAllText(file, "fixture");

        Assert.Equal(ContentPathState.Present,
            ManagedContentStorage.Probe(new ManagedContentPath { AbsolutePath = file }));
        Assert.Equal(ContentPathState.Present,
            ManagedContentStorage.Probe(new ManagedContentPath { AbsolutePath = temporary.Root, Directory = true }));
        Assert.Equal(ContentPathState.Missing,
            ManagedContentStorage.Probe(new ManagedContentPath { AbsolutePath = file, Directory = true }));
        Assert.Equal(ContentPathState.Missing,
            ManagedContentStorage.Probe(new ManagedContentPath { AbsolutePath = temporary.GetPath("gone.exe") }));
        Assert.Equal(ContentPathState.Missing, ManagedContentStorage.Probe(new ManagedContentPath
        {
            AbsolutePath = Path.Combine(temporary.Root, "moved", "deeper", "game.exe")
        }));
    }

    [Fact]
    public void ALiteralPathOnADriveWithNothingMountedIsVolumeAbsent()
    {
        var letter = Enumerable.Range('D', 23).Select(value => (char)value)
            .First(candidate => !Directory.Exists(candidate + @":"));

        Assert.Equal(ContentPathState.VolumeAbsent,
            ManagedContentStorage.Probe(new ManagedContentPath { AbsolutePath = letter + @":\Games\game.exe" }));
    }

    [Fact]
    public void AVolumeBoundPathIsQueriedThroughItsVolumeAndAnUnmountedVolumeIsAbsent()
    {
        using TemporaryDirectory temporary = new();
        var file = temporary.GetPath("game.rom");
        File.WriteAllText(file, "fixture");
        var bound = ManagedContentStorage.CapturePath(file);
        Assert.NotEmpty(bound.VolumeId);

        Assert.Equal(ContentPathState.Present, ManagedContentStorage.Probe(bound));
        File.Delete(file);
        Assert.Equal(ContentPathState.Missing, ManagedContentStorage.Probe(bound));
        Assert.Equal(ContentPathState.VolumeAbsent, ManagedContentStorage.Probe(new ManagedContentPath
        {
            AbsolutePath = file, VolumeId = @"\\?\Volume{00000000-0000-4000-8000-000000000001}\",
            RelativePath = bound.RelativePath
        }));
    }

    [Fact]
    public void ARecordsPathSetSkipsTheUnsetProgramOfARom()
    {
        ManagedContentRecord rom = new()
        {
            BackingPath = new ManagedContentPath { AbsolutePath = @"E:\Roms\game.cue" },
            RequiredPaths = [new ManagedContentPath { AbsolutePath = @"E:\Roms\game.bin" }]
        };

        Assert.Equal([@"E:\Roms\game.cue", @"E:\Roms\game.bin"], rom.Paths().Select(path => path.AbsolutePath));
    }
}
