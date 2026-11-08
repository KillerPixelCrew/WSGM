using System.Security.Cryptography;
using Avalonia.Media.Imaging;
using WSGM.Core;
using WSGM.Shell;
using WSGM.Testing;
using WSGM.Tests.Fakes;

namespace WSGM.Tests.Shell;

public sealed class ProgramsServiceTests
{
    private static string FakeExecutable(TemporaryDirectory directory, string name = "tool.exe", byte marker = 1)
    {
        var path = Path.Combine(directory.Root, name);
        File.WriteAllBytes(path, [(byte)'M', (byte)'Z', marker]);
        return path;
    }

    private const string Xbox = "Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App";

    /// <summary>Windows' installed applications, recorded; no window ever belongs to a packaged app here.</summary>
    private sealed class FakeInstalledHost : IInstalledApplicationHost
    {
        public bool Installed { get; set; } = true;
        public Exception? ListFailure { get; init; }
        public Action? OnActivate { get; init; }
        public List<string> Activated { get; } = [];

        public Task<IReadOnlyList<InstalledProgram>> ListAsync(CancellationToken cancellationToken)
        {
            return ListFailure is not null
                ? Task.FromException<IReadOnlyList<InstalledProgram>>(ListFailure)
                : Task.FromResult<IReadOnlyList<InstalledProgram>>([new InstalledProgram(Xbox, "Xbox", Xbox, null, "")]);
        }

        public Task<IReadOnlyDictionary<string, Bitmap?>> ReadIconsAsync(IReadOnlyList<string> keys,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyDictionary<string, Bitmap?>>(keys.ToDictionary(key => key, _ => (Bitmap?)null));
        }

        public bool IsInstalled(string appUserModelId)
        {
            return Installed;
        }

        public string? WindowApplicationId(AppWindow window)
        {
            return null;
        }

        public Task<ScheduledTaskLaunchDisposition> ActivateAsync(string appUserModelId,
            CancellationToken cancellationToken)
        {
            Activated.Add(appUserModelId);
            OnActivate?.Invoke();
            return Task.FromResult(ScheduledTaskLaunchDisposition.Dispatched);
        }
    }

    [Fact]
    public async Task APackagedProgramStoresItsIdAndNoExecutableAndSurvivesARestart()
    {
        using var store = new TemporaryConfigStore();
        FakeInstalledHost host = new();
        var definition = new ProgramDefinition { Name = " Xbox ", AppUserModelId = Xbox };
        await using (var service = new ProgramsService(store.Store, store.Store.Read().RequireConfig(), host))
        {
            await service.SaveAsync(definition, true);
        }

        var saved = Stored(store, definition.Id);
        Assert.Equal(Xbox, saved.AppUserModelId);
        Assert.Equal("Xbox", saved.Name);
        Assert.Equal("", saved.Executable);
        Assert.Equal("", saved.ExecutableHash);
        await using var restarted = new ProgramsService(store.Store, store.Store.Read().RequireConfig(), host);
        await restarted.ForgetUiAsync(definition.Id);
        var state = Assert.Single(restarted.ReadState());
        Assert.True(state.Available);
        Assert.True(state.CanLaunch);
        Assert.Equal("Not running", state.Status);
    }

    [Fact]
    public async Task AnEditorSnapshotCannotChangeAPackagedProgramsIdentity()
    {
        using var store = new TemporaryConfigStore();
        await using var service = new ProgramsService(store.Store, store.Store.Read().RequireConfig(),
            new FakeInstalledHost());
        var definition = new ProgramDefinition { Name = "Xbox", AppUserModelId = Xbox };
        await service.SaveAsync(definition, true);

        await service.SaveAsync(Stored(store, definition.Id) with
        {
            Name = "Games",
            AppUserModelId = "Other.App_8wekyb3d8bbwe!App"
        }, false);

        var saved = Stored(store, definition.Id);
        Assert.Equal("Games", saved.Name);
        Assert.Equal(Xbox, saved.AppUserModelId);
    }

    [Fact]
    public async Task APackageThatIsNotInstalledIsRefusedAndReportedUnavailable()
    {
        using var store = new TemporaryConfigStore();
        FakeInstalledHost host = new();
        await using var service = new ProgramsService(store.Store, store.Store.Read().RequireConfig(), host);
        var definition = new ProgramDefinition { Name = "Xbox", AppUserModelId = Xbox };
        await service.SaveAsync(definition, true);

        host.Installed = false;
        await service.ForgetUiAsync(definition.Id);

        var state = Assert.Single(service.ReadState());
        Assert.False(state.CanLaunch);
        Assert.Equal("Unavailable · app not installed", state.Status);
        Assert.Contains("no longer installed", await service.ActivateAsync(definition.Id, CancellationToken.None));
        Assert.Empty(host.Activated);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SaveAsync(new ProgramDefinition { Name = "Gone", AppUserModelId = Xbox }, true));
    }

    [Fact]
    public async Task ActivatingAPackagedProgramGoesThroughTheDeElevatedHost()
    {
        using var store = new TemporaryConfigStore();
        using CancellationTokenSource stop = new();
        // The wait for its window is cancelled once the dispatch is recorded, so no real window is touched.
        FakeInstalledHost host = new() { OnActivate = stop.Cancel };
        await using var service = new ProgramsService(store.Store, store.Store.Read().RequireConfig(), host);
        var definition = new ProgramDefinition { Name = "Xbox", AppUserModelId = Xbox };
        await service.SaveAsync(definition, true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ActivateAsync(definition.Id, stop.Token));

        Assert.Equal([Xbox], host.Activated);
    }

    [Fact]
    public async Task AFailedListingReportsItsReasonInsteadOfAnEmptyList()
    {
        using var store = new TemporaryConfigStore();
        await using var service = new ProgramsService(store.Store, store.Store.Read().RequireConfig(),
            new FakeInstalledHost { ListFailure = new InvalidOperationException("no shell") });

        var result = await service.ReadInstalledAsync(CancellationToken.None);

        Assert.Empty(result.Programs);
        Assert.NotNull(result.Error);
    }

    private static ProgramDefinition Stored(TemporaryConfigStore store, string id)
    {
        return store.Store.Read().RequireConfig().Programs.Single(entry => entry.Id == id);
    }

    [Fact]
    public async Task AnEditorSnapshotCannotChangeTheApprovedExecutableOrUiIdentity()
    {
        using var store = new TemporaryConfigStore();
        using var files = new TemporaryDirectory();
        var executable = FakeExecutable(files);
        await using var service = new ProgramsService(store.Store, store.Store.Read().RequireConfig());
        var definition = new ProgramDefinition { Name = "Tool", Executable = executable };
        await service.SaveAsync(definition, true);
        var approved = Stored(store, definition.Id);

        await service.SaveAsync(approved with
        {
            Name = " Renamed ",
            Arguments = "--flag",
            Executable = FakeExecutable(files, "other.exe"),
            ExecutableHash = "forged",
            UiExecutable = executable,
            UiExecutableHash = "forged"
        }, false);

        var saved = Stored(store, definition.Id);
        Assert.Equal("Renamed", saved.Name);
        Assert.Equal("--flag", saved.Arguments);
        Assert.Equal(approved.Executable, saved.Executable);
        Assert.Equal(approved.ExecutableHash, saved.ExecutableHash);
        Assert.Equal("", saved.UiExecutable);
        Assert.Equal("", saved.UiExecutableHash);
    }

    [Fact]
    public async Task ApprovalRecordsTheFreshFingerprintAndClearsTheResolvedUi()
    {
        using var store = new TemporaryConfigStore();
        using var files = new TemporaryDirectory();
        var executable = FakeExecutable(files);
        var definition = new ProgramDefinition
        {
            Name = "Tool", Executable = executable, UiExecutable = executable, UiExecutableHash = "old"
        };
        store.Store.Update(config =>
        {
            config.Programs.Add(definition with { });
            return true;
        });
        await using var service = new ProgramsService(store.Store, store.Store.Read().RequireConfig());
        File.WriteAllBytes(executable, [(byte)'M', (byte)'Z', 2]);

        await service.SaveAsync(definition, true);

        var saved = Stored(store, definition.Id);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable))),
            saved.ExecutableHash);
        Assert.Equal("", saved.UiExecutable);
        Assert.Equal("", saved.UiExecutableHash);
    }

    [Fact]
    public async Task AStaleEditorCannotRecreateARemovedProgram()
    {
        using var store = new TemporaryConfigStore();
        using var files = new TemporaryDirectory();
        await using var service = new ProgramsService(store.Store, store.Store.Read().RequireConfig());
        var definition = new ProgramDefinition { Name = "Tool", Executable = FakeExecutable(files) };
        await service.SaveAsync(definition, true);
        var snapshot = Stored(store, definition.Id);
        await service.RemoveAsync(definition.Id);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(snapshot with { Name = "Renamed" }, false));
        var forget = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ForgetUiAsync(definition.Id));

        Assert.Equal("This program was removed.", error.Message);
        Assert.Equal("This program was removed.", forget.Message);
        Assert.Empty(store.Store.Read().RequireConfig().Programs);
    }

    [Fact]
    public async Task RemovingAProgramDropsOnlyItsConfigurationAndPin()
    {
        using var store = new TemporaryConfigStore();
        using var files = new TemporaryDirectory();
        await using var service = new ProgramsService(store.Store, store.Store.Read().RequireConfig());
        var kept = new ProgramDefinition { Name = "Kept", Executable = FakeExecutable(files, "kept.exe") };
        var removed = new ProgramDefinition { Name = "Removed", Executable = FakeExecutable(files) };
        await service.SaveAsync(kept, true);
        await service.SaveAsync(removed, true);
        store.Store.Update(config =>
        {
            config.QuickAccessPins =
            [
                "home.desktop", ProgramsService.PinPrefix + kept.Id,
                ProgramsService.PinPrefix + removed.Id
            ];
            return true;
        });
        service.ConfigurationChanged(store.Store.Read().RequireConfig());

        await service.RemoveAsync(removed.Id);

        var config = store.Store.Read().RequireConfig();
        Assert.Equal([kept.Id], config.Programs.Select(entry => entry.Id));
        Assert.Equal(["home.desktop", ProgramsService.PinPrefix + kept.Id], config.QuickAccessPins);
        Assert.True(service.IsPinned(kept.Id));
        Assert.False(service.IsPinned(removed.Id));
        Assert.True(File.Exists(removed.Executable));
    }

    [Fact]
    public async Task ANonExecutableSelectionIsRejectedWithoutBeingStored()
    {
        using var store = new TemporaryConfigStore();
        using var files = new TemporaryDirectory();
        var text = Path.Combine(files.Root, "notes.exe");
        await File.WriteAllTextAsync(text, "plain text");
        await using var service = new ProgramsService(store.Store, store.Store.Read().RequireConfig());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(new ProgramDefinition { Name = "Notes", Executable = text }, true));

        Assert.Empty(store.Store.Read().RequireConfig().Programs);
    }
}
