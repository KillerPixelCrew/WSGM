using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Windows.Management.Deployment;
using WindowsDeviceControl;
using LibHandheld;
using DeploymentPackageStatus = Windows.Management.Deployment.PackageStatus;

namespace WSGM.Core;

/// <summary>Package activation and dedicated keyboard operations used by manager takeover and recovery.</summary>
public interface IOtherManagerTargets
{
    /// <summary>Whether the exact handheld definition matches this machine.</summary>
    /// <param name="definitionId">Exact built-in LibHandheld definition ID.</param>
    /// <returns>True only for the detected definition.</returns>
    bool MatchesDevice(string definitionId);

    /// <summary>Captures active package/device targets and their original states without changing them.</summary>
    /// <param name="manager">Known manager catalog entry.</param>
    /// <returns>Changes that can be recorded before applying takeover.</returns>
    IReadOnlyList<OtherManagerRecord> Detect(OtherManager manager);

    /// <summary>Applies one recorded change without retrying uncertain output.</summary>
    /// <param name="target">The retained pre-change snapshot.</param>
    /// <returns>Whether Windows accepted the command.</returns>
    bool Disable(OtherManagerRecord target);

    /// <summary>Restores one recorded target; a removed target has nothing to restore.</summary>
    /// <param name="target">The retained pre-change snapshot.</param>
    /// <returns>Whether restoration completed or the target is gone.</returns>
    bool Restore(OtherManagerRecord target);

    /// <summary>Whether an accepted device command requires a restart.</summary>
    bool RestartRequired { get; }
}

/// <summary>Exact catalog-bound Windows operations; no package uninstall or shared keyboard-driver changes.</summary>
internal sealed class OtherManagerWindowsTargets : IOtherManagerTargets
{
    private static readonly Guid KeyboardClass = new("4d36e96b-e325-11ce-bfc1-08002be10318");
    // The machine cannot change while WSGM runs; a failed read is retried on the next use.
    private static readonly Lazy<string?> Definition = new(() => HandheldDevice.Detect(HandheldDevice.ReadIdentity())?.Id,
        LazyThreadSafetyMode.PublicationOnly);

    public bool RestartRequired { get; private set; }

    public bool MatchesDevice(string definitionId)
    {
        return Definition.Value == definitionId;
    }

    public IReadOnlyList<OtherManagerRecord> Detect(OtherManager manager)
    {
        List<OtherManagerRecord> found = [];
        // A disabled package's helper that still runs is the manager's running process: the shell-start
        // check asks it to close and setup reports the restart, so the package itself needs no change.
        foreach (var family in manager.PackageFamilies)
        {
            foreach (var package in new PackageManager().FindPackagesForUser(string.Empty, family)
                         .Where(package => !package.Status.Disabled))
            {
                found.Add(new OtherManagerRecord
                {
                    ManagerId = manager.Id, Kind = OtherManagers.PackageKind, Name = package.Id.FullName
                });
            }
        }
        if (manager.DedicatedKeyboardDeviceId is { } deviceId)
        {
            // An in-use leaf stays active until restart after an accepted disable; treating it as back
            // would resend the disable on every five-minute re-check.
            foreach (var node in DeviceNodes.Enumerate(KeyboardClass)
                         .Where(node => HasDeviceId(node.InstanceId, deviceId) && node.ProblemCode != 22
                                        && !DeviceNodes.IsRestartPending(node.InstanceId)))
            {
                found.Add(new OtherManagerRecord
                {
                    ManagerId = manager.Id, Kind = OtherManagers.DeviceKind, Name = node.InstanceId
                });
            }
        }
        return found;
    }

    public bool Disable(OtherManagerRecord target) => Change(target, restore: false);

    public bool Restore(OtherManagerRecord target) => Change(target, restore: true);

    private bool Change(OtherManagerRecord target, bool restore)
    {
        try
        {
            var manager = OtherManagers.Known.SingleOrDefault(item => item.Id == target.ManagerId);
            if (manager is null)
            {
                return false;
            }
            // The record proves WSGM made the change, so restoring it never depends on identifying the
            // machine again; a failed read or a BIOS update must not strand a disabled package or keyboard.
            if (!restore && manager.DeviceDefinitionId is { } definition && !MatchesDevice(definition))
            {
                return false;
            }
            if (target.Kind == OtherManagers.PackageKind)
            {
                PackageManager packages = new();
                var package = packages.FindPackageForUser(string.Empty, target.Name);
                if (package is null)
                {
                    return restore;
                }
                if (!manager.PackageFamilies.Contains(package.Id.FamilyName, StringComparer.Ordinal))
                {
                    return false;
                }
                if (restore && target.PreviousStart == 0)
                {
                    packages.ClearPackageStatus(target.Name, DeploymentPackageStatus.Disabled);
                }
                else
                {
                    packages.SetPackageStatus(target.Name, DeploymentPackageStatus.Disabled);
                }
                return true;
            }
            if (target.Kind != OtherManagers.DeviceKind || manager.DedicatedKeyboardDeviceId is not { } deviceId
                || !HasDeviceId(target.Name, deviceId))
            {
                return false;
            }
            if (!DeviceNodes.IsRegistered(target.Name))
            {
                return restore;
            }
            // pnputil takes the instance ID directly after the command; /force placed before it made
            // every disable exit 1, so the takeover kept finding the keyboard leaf enabled.
            var arguments = restore
                ? $"/enable-device \"{target.Name}\""
                : $"/disable-device \"{target.Name}\" /force";
            var result = ConsoleTool.RunAsync(ConsoleTool.System32("pnputil.exe"), arguments)
                .GetAwaiter().GetResult();
            if (result.ExitCode == 3010)
            {
                RestartRequired = true;
                Log.Warn("Other managers: dedicated controller keyboard change requires a Windows restart.");
            }
            return result.ExitCode is 0 or 3010;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Other managers: {target.Kind} {(restore ? "restore" : "takeover")} failed: {ex.Message}");
            return false;
        }
    }

    internal static bool HasDeviceId(string instanceId, string deviceId)
    {
        return instanceId.StartsWith(deviceId + "\\", StringComparison.OrdinalIgnoreCase)
               && !instanceId.Contains('"');
    }
}
