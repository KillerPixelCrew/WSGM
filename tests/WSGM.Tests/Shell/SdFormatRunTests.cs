using SteamUiToolkit;
using WSGM.Core;
using WSGM.Interop;
using WSGM.Shell;
using WSGM.Testing;
using WSGM.Tests.Fakes;

namespace WSGM.Tests.Shell;

public sealed partial class SdFormatTests
{
    [Fact]
    public async Task FormatRun_RechecksEveryAttemptAndAssignmentBeforeCreatingTheLibrary()
    {
        using var run = new FormatRunFixture();
        run.FormatResults.Enqueue(FormatRunFixture.Failed);
        run.FormatResults.Enqueue(FormatRunFixture.Failed);
        run.Letters.Enqueue(null);
        run.Letters.Enqueue('E');

        await run.StartAsync();

        Assert.True(run.Success);
        Assert.Equal(["clean", "format", "format", "format", "assign"], run.Stages);
        foreach (var stage in new[] { "clean", "format", "assign" })
        {
            foreach (var index in run.Events.Select((value, index) => (value, index))
                         .Where(item => item.value == stage).Select(item => item.index))
            {
                Assert.Equal("identity", run.Events[index - 1]);
            }
        }

        Assert.Equal(2, run.Events.Count(value => value == "delay"));
        Assert.Equal(6, run.IdentityReads);
        Assert.Contains("volume", run.Events);
        Assert.True(run.Events.IndexOf("volume") < run.Events.IndexOf("format"));
        Assert.Contains(run.Logs, line => line.Contains("volume on disk 3 appeared after 125 ms"));
        Assert.DoesNotContain(run.Config.Store.Read().RequireConfig().CardLibraries, card => card.ContentId == "222");
        Assert.Contains(run.Config.Store.Read().RequireConfig().CardLibraries, card => card.ContentId == "999");
        Assert.True(File.Exists(Path.Combine(run.LibraryPath, "steam.dll")));
        Assert.Contains("New card", File.ReadAllText(run.MarkerPath));
        Assert.False(run.Manager.Busy);
        Assert.Same(run.Manager.Completion, run.RunTask);
    }

    [Fact]
    public async Task FormatRun_StopsAfterExactlyThreeKnownFormatFailures()
    {
        using var run = new FormatRunFixture();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            run.FormatResults.Enqueue(FormatRunFixture.Failed);
        }

        await run.StartAsync();

        Assert.False(run.Success);
        Assert.Equal(["clean", "format", "format", "format"], run.Stages);
        Assert.Equal(5, run.IdentityReads);
        Assert.DoesNotContain("letter", run.Events);
        Assert.DoesNotContain("retrim", run.Events);
        Assert.DoesNotContain("marker", run.Events);
    }

    [Fact]
    public async Task FormatRun_PreCleanIdentityChangeRestoresOnlyTheRemovedRegistration()
    {
        using var run = new FormatRunFixture();
        run.IdentityReader = read => read == 1 ? run.Identity : run.Identity with { SystemDisk = true };

        await run.StartAsync();

        Assert.False(run.Success);
        Assert.Empty(run.Stages);
        Assert.True(SteamLibraryVdf.IsContentIdRegistered(run.LibraryConfigText, "222"));
        Assert.Equal("Original label", SteamLibraryVdf.LabelForContentId(run.LibraryConfigText, "222"));
        Assert.Equal(1, run.Events.Count(value => value == "marker"));
        Assert.Contains(run.Config.Store.Read().RequireConfig().CardLibraries, card => card.ContentId == "222");
    }

    [Fact]
    public async Task FormatRun_PreCleanSwapDoesNotRestoreAnotherCardsMarker()
    {
        using var run = new FormatRunFixture();
        run.SteamRunning = true;
        run.AfterRemoval = () =>
        {
            run.Identity = run.Identity with { SizeBytes = run.Identity.SizeBytes * 2 };
            run.WriteMarker("333");
        };

        await run.StartAsync();

        Assert.False(run.Success);
        Assert.Empty(run.Stages);
        Assert.Empty(run.LiveAdds);
        Assert.False(SteamLibraryVdf.IsContentIdRegistered(run.LibraryConfigText, "222"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FormatRun_KnownPartitionFailureRestoresTheRemovedRegistration(bool processStarted)
    {
        using var run = new FormatRunFixture();
        var outcome = processStarted ? ConsoleToolRunOutcome.Failed : ConsoleToolRunOutcome.NotStarted;
        run.PartitionResult =
            new ConsoleToolResult(outcome, outcome == ConsoleToolRunOutcome.Failed ? 1 : null, "not erased");

        await run.StartAsync();

        Assert.False(run.Success);
        Assert.Equal(["clean"], run.Stages);
        Assert.True(SteamLibraryVdf.IsContentIdRegistered(run.LibraryConfigText, "222"));
        Assert.Equal("Original label", SteamLibraryVdf.LabelForContentId(run.LibraryConfigText, "222"));
        Assert.Contains(run.Config.Store.Read().RequireConfig().CardLibraries, card => card.ContentId == "222");
        Assert.Equal(1, run.Events.Count(value => value == "marker"));
    }

    [Fact]
    public async Task FormatRun_DoesNotInventARegistrationThatWasNotRemoved()
    {
        using var run = new FormatRunFixture();
        Assert.True(SteamLibraryVdf.TryRemoveContentId(run.LibraryConfigText, "222", out var text));
        File.WriteAllText(run.LibraryConfigPath, text);
        run.IdentityReader = read => read == 1 ? run.Identity : run.Identity with { SystemDisk = true };

        await run.StartAsync();

        Assert.False(run.Success);
        Assert.Empty(run.Stages);
        Assert.DoesNotContain("marker", run.Events);
        Assert.False(SteamLibraryVdf.IsContentIdRegistered(run.LibraryConfigText, "222"));
    }

    [Fact]
    public async Task FormatRun_LiveRestorationUsesTheOriginalPathAndLabelOnce()
    {
        using var run = new FormatRunFixture();
        run.SteamRunning = true;
        run.PartitionResult = FormatRunFixture.Failed;

        await run.StartAsync();

        Assert.False(run.Success);
        var restored = Assert.Single(run.LiveAdds);
        Assert.Equal(run.LibraryPath, restored.Path);
        Assert.Equal("Original label", restored.Label);
        Assert.False(restored.Replace);
        Assert.Contains(run.Config.Store.Read().RequireConfig().CardLibraries, card => card.ContentId == "222");
    }

    [Fact]
    public async Task FormatRun_ASecondRequestDoesNotReplaceTheInFlightCompletion()
    {
        using var run = new FormatRunFixture();
        run.SteamRunning = true;
        run.RemovalCompletion =
            new TaskCompletionSource<SteamLibraryRemoveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = run.StartAsync();
        await run.RemovalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await run.Manager.FormatAsync(run.Entry, "Second request");

        Assert.Same(first, run.Manager.Completion);
        Assert.Empty(run.Stages);
        run.RemovalCompletion.SetResult(new SteamLibraryRemoveResult(SteamLibraryRemoveStatus.Removed, null));
        await first;
        Assert.True(run.Success);
        Assert.Equal(["clean", "format"], run.Stages);
    }

    [Fact]
    public async Task FormatRun_CancelledLifetimeDoesNotStartAnyOperation()
    {
        using var run = new FormatRunFixture();
        run.Cancellation.Cancel();

        await run.StartAsync();

        Assert.Empty(run.Events);
        Assert.False(run.Manager.Busy);
        Assert.True(SteamLibraryVdf.IsContentIdRegistered(run.LibraryConfigText, "222"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task FormatRun_InitialIdentityCheckIsStrictAndPrecedesRemoval(int failure)
    {
        using var run = new FormatRunFixture();
        run.Identity = failure switch
        {
            0 => run.Identity with { SystemDisk = true },
            1 => run.Identity with { HandleOpened = false, SizeBytes = 0 },
            2 => run.Identity with { Removable = false },
            3 => run.Identity with { SizeBytes = 0 },
            _ => run.Identity with { BusType = -1 }
        };

        await run.StartAsync();

        Assert.False(run.Success);
        Assert.Equal(["identity"], run.Events);
        Assert.Empty(run.Stages);
        Assert.True(SteamLibraryVdf.IsContentIdRegistered(run.LibraryConfigText, "222"));
    }

    [Fact]
    public async Task FormatRun_AsyncRemovalAndRefreshedRowCannotReplaceThePickedIdentity()
    {
        using var run = new FormatRunFixture();
        run.SteamRunning = true;
        run.RemovalCompletion =
            new TaskCompletionSource<SteamLibraryRemoveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = run.StartAsync();
        await run.RemovalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(task.IsCompleted);
        Assert.Empty(run.Stages);

        run.Manager.Apply([
            new SdFormatManager.FormatTarget("card", 3, "replacement",
                512_000_000_000L, NativeStorage.BusTypeUsb, ['F'], false)
        ]);
        Assert.Same(run.Entry, Assert.Single(run.Manager.Targets));
        run.Identity = run.Identity with { SizeBytes = run.Entry.SizeBytes, BusType = run.Entry.BusType };
        run.WriteMarker("333");
        run.RemovalCompletion.SetResult(new SteamLibraryRemoveResult(SteamLibraryRemoveStatus.Removed, null));
        await task;

        Assert.False(run.Success);
        Assert.Empty(run.Stages);
        Assert.Empty(run.LiveAdds);
        Assert.Equal(2, run.IdentityReads);
    }

    [Fact]
    public async Task FormatRun_PostCleanSwapRetiresTheOldIdentityWithoutRestoringIt()
    {
        using var run = new FormatRunFixture();
        run.AfterClean = () => run.Identity = run.Identity with { SizeBytes = run.Identity.SizeBytes * 2 };

        await run.StartAsync();

        Assert.False(run.Success);
        Assert.Equal(["clean"], run.Stages);
        Assert.DoesNotContain("marker", run.Events);
        Assert.False(SteamLibraryVdf.IsContentIdRegistered(run.LibraryConfigText, "222"));
        Assert.DoesNotContain(run.Config.Store.Read().RequireConfig().CardLibraries, card => card.ContentId == "222");
    }

    [Fact]
    public async Task FormatRun_SwapDuringRetryDelayPreventsTheNextFormatAttempt()
    {
        using var run = new FormatRunFixture();
        run.FormatResults.Enqueue(FormatRunFixture.Failed);
        run.OnDelay = () => run.Identity = run.Identity with { BusType = NativeStorage.BusTypeUsb };

        await run.StartAsync();

        Assert.False(run.Success);
        Assert.Equal(["clean", "format"], run.Stages);
        Assert.Equal(4, run.IdentityReads);
        Assert.DoesNotContain("marker", run.Events);
    }

    [Fact]
    public async Task FormatRun_SwapBeforeAssignmentPreventsAssignment()
    {
        using var run = new FormatRunFixture();
        run.Letters.Enqueue(null);
        run.OnLetter = () => run.Identity = run.Identity with { Removable = false };

        await run.StartAsync();

        Assert.False(run.Success);
        Assert.Equal(["clean", "format"], run.Stages);
        Assert.Equal(4, run.IdentityReads);
        Assert.DoesNotContain("retrim", run.Events);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FormatRun_UnknownEraseRestoresAndRetainsOnlyWhenTheMarkerSurvives(bool markerSurvives)
    {
        using var run = new FormatRunFixture();
        run.PartitionResult = FormatRunFixture.Unknown;
        run.EraseOnUnknown = !markerSurvives;

        await run.StartAsync();

        Assert.False(run.Success);
        Assert.Equal(["clean"], run.Stages);
        Assert.Contains("may have been erased", run.Manager.StatusText);
        Assert.Equal(markerSurvives, SteamLibraryVdf.IsContentIdRegistered(run.LibraryConfigText, "222"));
        Assert.Equal(markerSurvives,
            run.Config.Store.Read().RequireConfig().CardLibraries.Any(card => card.ContentId == "222"));
        Assert.DoesNotContain("delay", run.Events);
    }

    [Fact]
    public async Task FormatRun_UnknownFormatIsNotRetriedOrCompensated()
    {
        using var run = new FormatRunFixture();
        run.FormatResults.Enqueue(FormatRunFixture.Unknown);

        await run.StartAsync();

        Assert.False(run.Success);
        Assert.Equal(["clean", "format"], run.Stages);
        Assert.Contains("format result is unknown", run.Manager.StatusText);
        Assert.DoesNotContain("delay", run.Events);
        Assert.DoesNotContain("marker", run.Events);
    }

    [Fact]
    public async Task FormatRun_CancelledProcessWaitKeepsAnUnknownEraseOutcome()
    {
        using var run = new FormatRunFixture();
        run.PartitionCompletion =
            new TaskCompletionSource<ConsoleToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = run.StartAsync();
        await run.PartitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(task.IsCompleted);

        run.Cancellation.Cancel();
        run.PartitionCompletion.SetResult(FormatRunFixture.Unknown);
        await task;

        Assert.False(run.Success);
        Assert.Equal(["clean"], run.Stages);
        Assert.Contains("may have been erased", run.Manager.StatusText);
        Assert.True(SteamLibraryVdf.IsContentIdRegistered(run.LibraryConfigText, "222"));
        Assert.Contains(run.Config.Store.Read().RequireConfig().CardLibraries, card => card.ContentId == "222");
        Assert.False(run.Manager.Busy);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FormatRun_CancellationBeforeAndAfterCleanPreservesTheRecoveryBoundary(bool beforeClean)
    {
        using var run = new FormatRunFixture();
        if (beforeClean)
        {
            run.IdentityReader = read =>
            {
                if (read == 2)
                {
                    run.Cancellation.Cancel();
                }

                return run.Identity;
            };
        }
        else
        {
            run.OnVolume = run.Cancellation.Cancel;
        }

        await run.StartAsync();

        Assert.False(run.Success);
        Assert.Contains("stopped during shutdown", run.Manager.StatusText);
        Assert.Equal(beforeClean ? 0 : 1, run.Stages.Count);
        Assert.Equal(beforeClean, SteamLibraryVdf.IsContentIdRegistered(run.LibraryConfigText, "222"));
        Assert.Equal(beforeClean,
            run.Config.Store.Read().RequireConfig().CardLibraries.Any(card => card.ContentId == "222"));
        Assert.False(run.Manager.Busy);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FormatRun_UnreadableOrUnknownBusMidRunIsNotSwapEvidence(bool unreadable)
    {
        using var run = new FormatRunFixture();
        run.IdentityReader = read => read == 1
            ? run.Identity
            : unreadable
                ? new SdFormatManager.DiskIdentitySnapshot(false, false, false, 0, -1)
                : run.Identity with { BusType = -1 };

        await run.StartAsync();

        Assert.True(run.Success);
        Assert.Equal(["clean", "format"], run.Stages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task FormatRun_OnlyDefiniteLackOfElevationBlocks(bool? elevated)
    {
        using var run = new FormatRunFixture();
        run.Elevated = elevated;

        await run.StartAsync();

        Assert.Equal(elevated is null, run.Success);
        Assert.Equal(elevated is null ? 2 : 0, run.Stages.Count);
    }

    [Fact]
    public async Task FormatRun_WrongVolumeDiskStopsBeforeAnyLibraryWrite()
    {
        using var run = new FormatRunFixture();
        run.VolumeDisk = 4;

        await run.StartAsync();

        Assert.False(run.Success);
        Assert.False(File.Exists(run.MarkerPath));
        Assert.DoesNotContain("retrim", run.Events);
        Assert.Empty(run.LiveAdds);
    }

    [Fact]
    public async Task FormatRun_WritesUseTheVolumeRootWhileSteamReceivesTheLetterPath()
    {
        using var run = new FormatRunFixture();
        run.SteamRunning = true;
        run.LetterRootPath = run.Files.GetPath("letter", "E") + Path.DirectorySeparatorChar;
        run.VolumeRootPath = run.Files.GetPath("volume-guid") + Path.DirectorySeparatorChar;
        run.MapLetterMarkerToVolume = true;

        await run.StartAsync();

        Assert.True(run.Success, run.Manager.StatusText + "\n" + string.Join("\n", run.Logs));
        Assert.False(Directory.Exists(Path.Combine(run.LetterRootPath, "SteamLibrary")));
        var volumeLibrary = Path.Combine(run.VolumeRootPath, "SteamLibrary");
        Assert.True(Directory.Exists(Path.Combine(volumeLibrary, "steamapps")));
        Assert.True(File.Exists(Path.Combine(volumeLibrary, "steam.dll")));
        Assert.True(File.Exists(Path.Combine(volumeLibrary, "libraryfolder.vdf")));
        var registration = Assert.Single(run.LiveAdds);
        Assert.Equal(Path.Combine(run.LetterRootPath, "SteamLibrary"), registration.Path);
        Assert.True(registration.Replace);
    }

    [Fact]
    public async Task FormatRun_ChangedLetterMarkerIsNotRegisteredAfterVolumeWrites()
    {
        using var run = new FormatRunFixture();
        run.SteamRunning = true;
        var originalRoot = run.VolumeRootPath;
        run.OnRetrim = () => run.LetterRootPath = run.Files.GetPath("replacement") + Path.DirectorySeparatorChar;

        await run.StartAsync();

        Assert.True(run.Success);
        Assert.Contains("changed before it could be added", run.Manager.StatusText);
        Assert.Empty(run.LiveAdds);
        Assert.True(File.Exists(Path.Combine(originalRoot, "SteamLibrary", "libraryfolder.vdf")));
        Assert.False(Directory.Exists(run.LetterRootPath));
    }

    [Fact]
    public async Task FormatRun_FailureToKeepTheLetterStopsBeforeLibraryWrites()
    {
        using var run = new FormatRunFixture();
        run.Letters.Enqueue('F');
        run.Letters.Enqueue('F');

        await run.StartAsync();

        Assert.False(run.Success);
        Assert.Equal(["clean", "format", "assign"], run.Stages);
        Assert.Contains("could not keep drive letter E:", run.Manager.StatusText);
        Assert.False(File.Exists(run.MarkerPath));
        Assert.DoesNotContain("retrim", run.Events);
    }

    [Fact]
    public async Task FormatRun_NoVolumeDiagnosticStillAttemptsTheFormat()
    {
        using var run = new FormatRunFixture();
        run.VolumeWait = -1;

        await run.StartAsync();

        Assert.True(run.Success);
        Assert.Contains(run.Logs, line => line.Contains("no volume appeared on disk 3"));
        Assert.Equal(["clean", "format"], run.Stages);
    }

    private sealed class FormatRunFixture : IDisposable
    {
        internal static readonly ConsoleToolResult Succeeded = new(ConsoleToolRunOutcome.Succeeded, 0, "ok");
        internal static readonly ConsoleToolResult Failed = new(ConsoleToolRunOutcome.Failed, 1, "refused");
        internal static readonly ConsoleToolResult Unknown = new(ConsoleToolRunOutcome.Unknown, null, "wait lost");
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TemporaryConfigStore Config = new();
        internal readonly List<string> Events = [];

        internal readonly TemporaryDirectory Files = new();
        internal readonly Queue<ConsoleToolResult> FormatResults = new();
        internal readonly Queue<char?> Letters = new();
        internal readonly List<(string Path, string Label, bool Replace)> LiveAdds = [];
        internal readonly List<string> Logs = [];

        internal readonly TaskCompletionSource PartitionStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal readonly TaskCompletionSource RemovalStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<string> Stages = [];

        internal FormatRunFixture()
        {
            LetterRootPath = Files.GetPath("card") + Path.DirectorySeparatorChar;
            VolumeRootPath = LetterRootPath;
            SteamExe = Files.GetPath("steam", "steam.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(SteamExe)!);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(SteamExe)!, "steam.dll"), "fake client");
            WriteMarker("222");
            Assert.True(SteamLibraryVdf.TrySplice("\"libraryfolders\"\n{\n}\n", LibraryPath, "222",
                Identity.SizeBytes, out var text, "Original label"));
            File.WriteAllText(LibraryConfigPath, text);
            Config.Store.Update(config =>
            {
                config.CardLibraries.Add(new CardLibraryConfig { ContentId = "222", Name = "Original label" });
                config.CardLibraries.Add(new CardLibraryConfig { ContentId = "999", Name = "Other card" });
                return true;
            });

            var operations = new SdFormatManager.Operations
            {
                IsElevated = () => Elevated,
                ReadDiskIdentity = disk =>
                {
                    Assert.Equal(3, disk);
                    Events.Add("identity");
                    IdentityReads++;
                    return IdentityReader?.Invoke(IdentityReads) ?? Identity;
                },
                RunDiskpart = async (script, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    var stage = script.Contains("clean\r\n") ? "clean" :
                        script.Contains("format fs=") ? "format" : "assign";
                    Events.Add(stage);
                    Stages.Add(stage);
                    var result = stage == "clean" ? PartitionResult
                        : stage == "format" && FormatResults.Count > 0 ? FormatResults.Dequeue() : Succeeded;
                    if (stage == "clean")
                    {
                        PartitionStarted.TrySetResult();
                        if (PartitionCompletion is not null)
                        {
                            result = await PartitionCompletion.Task;
                        }

                        if (result.Outcome == ConsoleToolRunOutcome.Succeeded || EraseOnUnknown)
                        {
                            if (File.Exists(MarkerPath))
                            {
                                File.Delete(MarkerPath);
                            }
                        }

                        AfterClean?.Invoke();
                    }

                    return result;
                },
                WaitForVolume = (disk, token) =>
                {
                    Assert.Equal(3, disk);
                    Events.Add("volume");
                    OnVolume?.Invoke();
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(VolumeWait);
                },
                WaitForLetter = (disk, _, token) =>
                {
                    Assert.Equal(3, disk);
                    token.ThrowIfCancellationRequested();
                    Events.Add("letter");
                    OnLetter?.Invoke();
                    return Task.FromResult(Letters.Count > 0 ? Letters.Dequeue() : 'E');
                },
                ReadVolume = _ => (VolumeRootPath, VolumeDisk),
                LettersOnDisk = _ => ['E'],
                LetterRoot = _ => LetterRootPath,
                ReadSteam = () => (SteamRunning, SteamExe),
                ReadLibraryFolders = () => (LibraryConfigPath, LibraryConfigText),
                ReadMarker = path =>
                {
                    Events.Add("marker");
                    if (MapLetterMarkerToVolume && path.StartsWith(LetterRootPath, StringComparison.OrdinalIgnoreCase))
                    {
                        path = Path.Combine(VolumeRootPath, Path.GetRelativePath(LetterRootPath, path));
                    }

                    SteamLibraryMarker.TryRead(path, out var id, out var label);
                    return (id, label);
                },
                RemoveLibrary = async (id, _, token) =>
                {
                    RemovalStarted.TrySetResult();
                    var result = RemovalCompletion is null
                        ? new SteamLibraryRemoveResult(SteamLibraryRemoveStatus.Removed, null)
                        : await RemovalCompletion.Task.WaitAsync(token);
                    if (result.Status == SteamLibraryRemoveStatus.Removed)
                    {
                        Assert.True(SteamLibraryVdf.TryRemoveContentId(LibraryConfigText, id, out var updated));
                        File.WriteAllText(LibraryConfigPath, updated);
                    }

                    AfterRemoval?.Invoke();
                    return result;
                },
                AddLibrary = (path, label, replace) =>
                {
                    LiveAdds.Add((path, label, replace));
                    return new SteamLibraryAddResult(SteamLibraryAddStatus.Added, null);
                },
                Retrim = (_, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    Events.Add("retrim");
                    OnRetrim?.Invoke();
                    return Task.FromResult(true);
                },
                BroadcastVolumeArrival = _ => Events.Add("broadcast"),
                Delay = (_, token) =>
                {
                    Events.Add("delay");
                    OnDelay?.Invoke();
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                },
                WriteLog = (_, message, error) => Logs.Add(error is null ? message : message + " " + error)
            };
            Manager = new SdFormatManager(Config.Store, operations, () => [], Cancellation.Token);
            Manager.Apply([
                new SdFormatManager.FormatTarget("card", 3, "original",
                    Identity.SizeBytes, Identity.BusType, ['E'], false)
            ]);
            Entry = Assert.Single(Manager.Targets);
            Manager.Finished += (_, success) => Success = success;
        }

        internal SdFormatManager Manager { get; }
        internal FormatTargetEntry Entry { get; }
        internal bool Success { get; private set; }
        internal Task? RunTask { get; private set; }
        internal string LetterRootPath { get; set; }
        internal string VolumeRootPath { get; set; }
        internal string SteamExe { get; }
        internal string LibraryPath => Path.Combine(LetterRootPath, "SteamLibrary");
        internal string MarkerPath => Path.Combine(LibraryPath, "libraryfolder.vdf");
        internal string LibraryConfigPath => Files.GetPath("libraryfolders.vdf");
        internal string LibraryConfigText => File.ReadAllText(LibraryConfigPath);
        internal bool? Elevated { get; set; } = true;
        internal bool SteamRunning { get; set; }
        internal bool EraseOnUnknown { get; set; }
        internal bool MapLetterMarkerToVolume { get; set; }
        internal int VolumeWait { get; set; } = 125;
        internal int VolumeDisk { get; set; } = 3;
        internal int IdentityReads { get; private set; }
        internal ConsoleToolResult PartitionResult { get; set; } = Succeeded;

        internal SdFormatManager.DiskIdentitySnapshot Identity { get; set; } = new(false, true, true,
            256_000_000_000L, NativeStorage.BusTypeSd);

        internal Func<int, SdFormatManager.DiskIdentitySnapshot>? IdentityReader { get; set; }
        internal TaskCompletionSource<SteamLibraryRemoveResult>? RemovalCompletion { get; set; }
        internal TaskCompletionSource<ConsoleToolResult>? PartitionCompletion { get; set; }
        internal Action? AfterRemoval { get; set; }
        internal Action? AfterClean { get; set; }
        internal Action? OnDelay { get; set; }
        internal Action? OnVolume { get; set; }
        internal Action? OnLetter { get; set; }
        internal Action? OnRetrim { get; set; }

        public void Dispose()
        {
            Cancellation.Dispose();
            Config.Dispose();
            Files.Dispose();
        }

        internal Task StartAsync()
        {
            RunTask = Manager.FormatAsync(Entry, "New card");
            return RunTask;
        }

        internal void WriteMarker(string id)
        {
            Directory.CreateDirectory(LibraryPath);
            File.WriteAllText(MarkerPath, SteamLibraryVdf.BuildMarker(id, SteamExe, "Original label"));
        }
    }
}
