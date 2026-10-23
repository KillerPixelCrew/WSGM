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

    private static ProgramDefinition Stored(TemporaryConfigStore store, string id) =>
        store.Store.Read().RequireConfig().Programs.Single(entry => entry.Id == id);

    [Fact]
    public async Task AnEditorSnapshotCannotChangeTheApprovedExecutableOrUiIdentity()
    {
        using var store = new TemporaryConfigStore();
        using var files = new TemporaryDirectory();
        var executable = FakeExecutable(files);
        await using var service = new ProgramsService(store.Store, store.Store.Read().RequireConfig());
        var definition = new ProgramDefinition { Name = "Tool", Executable = executable };
        await service.SaveAsync(definition, approveExecutable: true);
        var approved = Stored(store, definition.Id);

        await service.SaveAsync(approved with
        {
            Name = " Renamed ",
            Arguments = "--flag",
            Executable = FakeExecutable(files, "other.exe"),
            ExecutableHash = "forged",
            UiExecutable = executable,
            UiExecutableHash = "forged"
        }, approveExecutable: false);

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

        await service.SaveAsync(definition, approveExecutable: true);

        var saved = Stored(store, definition.Id);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(executable))),
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
        await service.SaveAsync(definition, approveExecutable: true);
        var snapshot = Stored(store, definition.Id);
        await service.RemoveAsync(definition.Id);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(snapshot with { Name = "Renamed" }, approveExecutable: false));
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
        await service.SaveAsync(kept, approveExecutable: true);
        await service.SaveAsync(removed, approveExecutable: true);
        store.Store.Update(config =>
        {
            config.QuickAccessPins = ["home.desktop", ProgramsService.PinPrefix + kept.Id,
                ProgramsService.PinPrefix + removed.Id];
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
            service.SaveAsync(new ProgramDefinition { Name = "Notes", Executable = text }, approveExecutable: true));

        Assert.Empty(store.Store.Read().RequireConfig().Programs);
    }
}
