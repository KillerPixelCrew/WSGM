using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Windows.ApplicationModel;
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
    private string? _definition;
    private bool _identityRead;

    public bool RestartRequired { get; private set; }

    public bool MatchesDevice(string definitionId)
    {
        if (!_identityRead)
        {
            _definition = HandheldDevice.Detect(HandheldDevice.ReadIdentity())?.Id;
            _identityRead = true;
        }
        return _definition == definitionId;
    }

    public IReadOnlyList<OtherManagerRecord> Detect(OtherManager manager)
    {
        List<OtherManagerRecord> found = [];
        if (manager.PackageFamilies.Count > 0)
        {
            PackageManager packages = new();
            foreach (var package in packages.FindPackagesForUser(string.Empty)
                         .Where(package => manager.PackageFamilies.Contains(package.Id.FamilyName, StringComparer.Ordinal)))
            {
                if (!package.Status.Disabled || PackageIsRunning(package))
                {
                    found.Add(new OtherManagerRecord
                    {
                        ManagerId = manager.Id, Kind = "package", Name = package.Id.FullName,
                        PreviousStart = package.Status.Disabled ? 1 : 0
                    });
                }
            }
        }
        if (manager.DedicatedKeyboardDeviceId is { } deviceId)
        {
            foreach (var node in DeviceNodes.Enumerate(KeyboardClass)
                         .Where(node => HasDeviceId(node.InstanceId, deviceId) && node.ProblemCode != 22))
            {
                found.Add(new OtherManagerRecord { ManagerId = manager.Id, Kind = "device", Name = node.InstanceId });
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
            if (manager.DeviceDefinitionId is { } definition && !MatchesDevice(definition))
            {
                return false;
            }
            if (target.Kind == "package")
            {
                PackageManager packages = new();
                var package = packages.FindPackagesForUser(string.Empty)
                    .SingleOrDefault(item => item.Id.FullName == target.Name);
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
            if (target.Kind != "device" || manager.DedicatedKeyboardDeviceId is not { } deviceId
                || !HasDeviceId(target.Name, deviceId))
            {
                return false;
            }
            if (!DeviceNodes.IsRegistered(target.Name))
            {
                return restore;
            }
            var operation = restore ? "/enable-device" : "/disable-device /force";
            var result = ConsoleTool.RunAsync(ConsoleTool.System32("pnputil.exe"), $"{operation} \"{target.Name}\"")
                .GetAwaiter().GetResult();
            RestartRequired |= result.ExitCode == 3010;
            if (result.ExitCode == 3010)
            {
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

    private static bool PackageIsRunning(Package package)
    {
        var executable = package.Id.Name switch
        {
            "ULICTekInc.PredatorSenseforHandHeld" => Path.Combine("Handheld.Desktop", "Handheld.Desktop.exe"),
            "ULICTekInc.PredatorSenseforHandHeldWidget" => "Handheld.Widget.exe",
            _ => null
        };
        if (executable is null)
        {
            return false;
        }
        var expected = Path.Combine(package.InstalledLocation.Path, executable);
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (InvalidOperationException)
                {
                    // Exited while detecting it.
                }
            }
        }
        return false;
    }
}
