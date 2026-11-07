using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Uses the ordinary installer journal for shared plugin bytes and records their consumers.</summary>
internal sealed class GameToolSharedPlugins(string userRoot, Func<string, GameToolBinary>? identify = null)
{
    internal async Task EnsureAsync(uint appId, string directory, GameToolDefinition tool, GameToolPackage package,
        Func<string?> ensureClosed, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        var installer = Installer(directory);
        var current = Find(directory)?.Manifest;
        if (current?.State == GameToolManifestState.Pending)
        {
            var busy = ensureClosed();
            if (busy is not null || !installer.Rollback(0, directory, current))
            {
                throw new GameToolException(busy ?? "The shared plugin has an interrupted change; close its games and retry.");
            }

            current = Find(directory)?.Manifest;
        }

        if (current is null)
        {
            var existing = package.Hashes.Keys.Select(path => Path.Combine(directory,
                path == tool.PrimaryFile ? tool.Proxy.Default : path)).Where(File.Exists).ToArray();
            if (existing.Length > 0)
            {
                var expected = package.Hashes.Count;
                if (existing.Length != expected || existing.Any(path =>
                        (identify?.Invoke(path) ?? GameToolBinaryIdentification.Read(path)) is var file
                        && (file.ToolId != "dgvoodoo2" || file.Architecture != package.Architecture)))
                {
                    throw new GameToolException("Special K's plugin folder contains unknown, incomplete or incompatible files. They were left untouched.");
                }

                if (existing.Select(path => (identify?.Invoke(path) ?? GameToolBinaryIdentification.Read(path)).Version)
                    .Distinct(StringComparer.Ordinal).Count() != 1)
                {
                    throw new GameToolException("The existing shared dgVoodoo DLLs have different versions. They were left untouched.");
                }

                var existingRecord = Find(directory);
                Save(directory, new GameToolSharedPlugin { Directory = directory, External = true,
                    Consumers = [.. (existingRecord?.Consumers ?? []).Append(appId).Distinct()] });
                return; // A compatible user installation is borrowed, never updated or removed.
            }
        }

        if (current?.State == GameToolManifestState.Failed)
        {
            throw new GameToolException("The shared plugin has an unresolved recovery journal. Its files were left untouched.");
        }

        if (current is not null && current.Files.Any(file => File.Exists(Path.Combine(directory, file.RelativePath))
                && GameToolInstaller.Sha256(Path.Combine(directory, file.RelativePath)) != file.Sha256))
        {
            throw new GameToolException("The managed shared plugin was changed outside WSGM. Those files were left untouched.");
        }

        if (current is not null && current.ReleaseId == package.Release.Id
            && current.Files.All(file => File.Exists(Path.Combine(directory, file.RelativePath))
                                         && GameToolInstaller.Sha256(Path.Combine(directory, file.RelativePath)) == file.Sha256))
        {
            AddConsumer(directory, appId);
            return;
        }

        if (ensureClosed() is { } reason)
        {
            throw new GameToolException(reason);
        }

        var target = new GameToolTarget(0, directory, Path.Combine(directory, "shared-plugin.exe"), package.Architecture, "shared");
        await installer.InstallAsync(tool, target, package,
            new GameToolRequest(current is null ? "install" : "update", tool.Proxy.Default,
                new Dictionary<string, string>()), current, ensureClosed, _ => { }, token).ConfigureAwait(false);
        AddConsumer(directory, appId);
    }

    /// <summary>Reconciles crash leftovers and deletes an owned copy only after its last game reference is gone.</summary>
    internal void Reconcile(Func<string, string?> ensureClosed)
    {
        foreach (var snapshot in GameToolStorage.ReadLocked(userRoot).SharedPlugins)
        {
            var shared = snapshot;
            if (shared.Manifest?.State == GameToolManifestState.Pending)
            {
                if (ensureClosed(shared.Directory) is not null || !Installer(shared.Directory).Rollback(0, shared.Directory, shared.Manifest))
                {
                    continue;
                }
                shared = Find(shared.Directory)!;
            }
            var consumers = GameToolStorage.ReadLocked(userRoot).Games.Where(game =>
                    game.Tools.Any(tool => (tool.SharedDependencyDirectory.Length > 0
                        && StoragePaths.Same(tool.SharedDependencyDirectory, shared.Directory))
                        || (tool.SharedSettingsDirectory.Length > 0 && StoragePaths.Same(tool.SharedSettingsDirectory, shared.Directory))))
                .Select(game => game.AppId).ToArray();
            if (consumers.Length > 0)
            {
                Save(shared.Directory, shared with { Consumers = consumers });
                continue;
            }

            if (shared.Manifest is { } manifest)
            {
                if (ensureClosed(shared.Directory) is not null)
                {
                    continue;
                }

                var tool = new GameToolDefinition { Id = manifest.ToolId, Name = "Shared Game Tools files" };
                var target = GameToolTarget.FromManifest(0, shared.Directory, manifest);
                var outcome = Installer(shared.Directory).Remove(tool, target, manifest);
                if (outcome.Manifest is not null)
                {
                    continue;
                }
            }

            Save(shared.Directory, null); // An external copy has no owned manifest and is never touched.
        }
    }

    internal void Register(string directory, uint appId)
    {
        var record = Find(directory) ?? new GameToolSharedPlugin { Directory = directory };
        Save(directory, record with { Consumers = [.. record.Consumers.Append(appId).Distinct()] });
    }

    internal async Task ConfigureAsync(string directory, string file, GameToolDefinition owner,
        IReadOnlyDictionary<string, string> requested, string architecture, Func<string?> ensureClosed)
    {
        if (ensureClosed() is { } reason)
        {
            throw new GameToolException(reason);
        }

        Directory.CreateDirectory(directory);
        var tool = owner with
        {
            Id = owner.Id + "-shared-settings", Name = owner.Name + " shared settings", PrimaryFile = file,
            ConfigFiles = [file], Modes = [], SharedSettingsFile = "", SharedDependency = null,
            Architectures = [architecture], Payload = new GameToolPayload { Required = [file] },
            Proxy = new GameToolProxy { Default = file, Options = [new GameToolProxyOption { Name = file }] },
            Settings = [.. owner.Settings.Where(setting => setting.Scope == "shared").Select(setting => setting with { Scope = "game" })]
        };
        var installer = Installer(directory);
        var target = new GameToolTarget(0, directory, Path.Combine(directory, "shared-settings.exe"), architecture, "shared");
        if (Find(directory)?.Manifest is { } current)
        {
            installer.Configure(tool, target, current, null, requested, ensureClosed);
            return;
        }

        var scratch = Path.Combine(Path.GetTempPath(), "WSGM-shared-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            File.WriteAllText(Path.Combine(scratch, file), "");
            var release = new HostedRelease("1", "generated-configuration", "", "WSGM", new ReleaseAsset(file, ""));
            var package = new GameToolPackage(release, scratch, "1", "", architecture);
            await installer.InstallAsync(tool, target, package, new GameToolRequest("install", file, requested),
                null, ensureClosed, _ => { }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            GameToolStorage.DeleteTree(scratch);
        }
    }

    internal static GameToolDefinition Definition(GameToolDefinition owner)
    {
        var dependency = owner.SharedDependency ?? throw new GameToolException("The shared plugin descriptor is missing.");
        var source = GameToolCatalog.Current.Find(dependency.SourceToolId)!;
        return source with
        {
            Id = "special-k-plugin", Name = "Special K dgVoodoo plugin", Modes = [dependency.Mode],
            Proxy = new GameToolProxy { Default = dependency.Mode.Proxy }, ConfigFiles = [],
            Settings = [], SharedDependency = null
        };
    }

    private GameToolSharedPlugin? Find(string directory)
    {
        return GameToolStorage.ReadLocked(userRoot).SharedPlugins.FirstOrDefault(plugin => StoragePaths.Same(plugin.Directory, directory));
    }

    private GameToolInstaller Installer(string directory)
    {
        return new GameToolInstaller(userRoot, (_, manifest, _) =>
        {
            var record = Find(directory) ?? new GameToolSharedPlugin { Directory = directory };
            Save(directory, record with { Manifest = manifest, External = false });
        });
    }

    private void AddConsumer(string directory, uint appId)
    {
        var record = Find(directory)!;
        Save(directory, record with { Consumers = [.. record.Consumers.Append(appId).Distinct()] });
    }

    private void Save(string directory, GameToolSharedPlugin? record)
    {
        GameToolStorage.Mutate(userRoot, store => store with
        {
            SharedPlugins = [.. store.SharedPlugins.Where(plugin => !StoragePaths.Same(plugin.Directory, directory)),
                .. record is null ? Array.Empty<GameToolSharedPlugin>() : [record]]
        });
    }
}
