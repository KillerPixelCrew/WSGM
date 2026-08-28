using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Device.Contracts.Packaging;

namespace WSGM.Device.Contracts.Modules;

/// <summary>Why a module composition was rejected.</summary>
public enum ModuleCompositionCode
{
    /// <summary>A composed module is not present in the catalog at the pinned version.</summary>
    UnknownModule,

    /// <summary>A layout or policy module was composed by a device it was never verified on.</summary>
    ModuleNotVerifiedForDevice,

    /// <summary>A transport or protocol module declared device scope, which would make it model-specific.</summary>
    ReusableModuleDeclaresDeviceScope,

    /// <summary>A layout or policy module declared no device scope at all.</summary>
    DeviceSpecificModuleMissingScope,

    /// <summary>A required dependency is absent from the composition.</summary>
    MissingDependency,

    /// <summary>A dependency is present but outside the version range the module accepts.</summary>
    DependencyVersionOutOfRange,

    /// <summary>Two modules that declare each other incompatible were composed together.</summary>
    ConflictingModules,

    /// <summary>A module that may write persistently did not require a snapshot.</summary>
    PersistentWriteWithoutSnapshot,

    /// <summary>A capability is declared by the device but implemented by no composed module.</summary>
    CapabilityWithoutImplementation,
}

/// <summary>One reason a composition was rejected.</summary>
/// <param name="ModuleId">The module involved, or the device ID for composition-wide problems.</param>
/// <param name="Code">Stable reason code.</param>
/// <param name="Message">Human-readable explanation.</param>
public sealed record ModuleCompositionError(string ModuleId, ModuleCompositionCode Code, string Message);

/// <summary>
/// Validates that a device definition composes modules it is actually entitled to.
/// </summary>
/// <remarks>
/// This is where "reuse a transport, not a policy" stops being advice. Identity similarity nominates
/// a candidate module and evidence authorizes it; nothing here derives one from the other.
/// </remarks>
public static class ModuleCompositionValidator
{
    /// <summary>
    /// Validates one device definition's composition against the module catalog.
    /// </summary>
    /// <param name="device">The device definition whose composition is checked.</param>
    /// <param name="catalog">Known modules, keyed by identifier.</param>
    /// <returns>Every violation found. Empty means the composition is internally consistent.</returns>
    public static IReadOnlyList<ModuleCompositionError> Validate(
        DeviceDefinition device,
        IReadOnlyDictionary<string, ImplementationModule> catalog)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(catalog);

        List<ModuleCompositionError> errors = [];
        Dictionary<string, ImplementationModule> composed = new(StringComparer.OrdinalIgnoreCase);

        foreach (ModuleReference reference in device.Modules)
        {
            if (!catalog.TryGetValue(reference.Id, out ImplementationModule? module)
                || module.Version != reference.Version)
            {
                errors.Add(new ModuleCompositionError(reference.Id, ModuleCompositionCode.UnknownModule,
                    $"Module '{reference.Id}' version {reference.Version} is not in the catalog."));
                continue;
            }

            composed[reference.Id] = module;
            ValidateScope(errors, device, module);
            ValidatePersistence(errors, module);
        }

        ValidateDependencies(errors, composed);
        ValidateConflicts(errors, composed);
        ValidateCapabilityCoverage(errors, device, composed);

        return errors;
    }

    private static void ValidateScope(
        List<ModuleCompositionError> errors,
        DeviceDefinition device,
        ImplementationModule module)
    {
        bool deviceSpecific = module.Layer is ModuleLayer.Layout or ModuleLayer.Policy;

        if (!deviceSpecific)
        {
            // A transport that names devices has stopped being a transport: whatever made it
            // device-specific is a constant that belongs in a layout or policy module, where the
            // scope rule can reach it.
            if (module.VerifiedDeviceIds.Count > 0)
            {
                errors.Add(new ModuleCompositionError(module.Id,
                    ModuleCompositionCode.ReusableModuleDeclaresDeviceScope,
                    $"{module.Layer} module '{module.Id}' declares verified devices. Move the "
                        + "model-specific part into a Layout or Policy module."));
            }

            return;
        }

        if (module.VerifiedDeviceIds.Count == 0)
        {
            errors.Add(new ModuleCompositionError(module.Id,
                ModuleCompositionCode.DeviceSpecificModuleMissingScope,
                $"{module.Layer} module '{module.Id}' holds device-specific values and must name the "
                    + "devices it was verified on."));
            return;
        }

        if (!module.VerifiedDeviceIds.Contains(device.Id, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add(new ModuleCompositionError(module.Id,
                ModuleCompositionCode.ModuleNotVerifiedForDevice,
                $"{module.Layer} module '{module.Id}' was verified on "
                    + $"[{string.Join(", ", module.VerifiedDeviceIds)}], not on '{device.Id}'. Its "
                    + "ranges, offsets, and firmware policy do not carry across a board boundary."));
        }
    }

    private static void ValidatePersistence(
        List<ModuleCompositionError> errors,
        ImplementationModule module)
    {
        // Unknown counts as persistent. A module that has not established whether its writes survive
        // a power cycle is exactly the one that must journal before touching anything.
        bool mayPersist = module.Safety.Writes
            && module.Safety.Persistence is PersistenceClass.DevicePersistent or PersistenceClass.Unknown;

        if (mayPersist && !module.Recovery.SnapshotRequired)
        {
            errors.Add(new ModuleCompositionError(module.Id,
                ModuleCompositionCode.PersistentWriteWithoutSnapshot,
                $"Module '{module.Id}' declares persistence '{module.Safety.Persistence}' but does not "
                    + "require a snapshot. Without one there is nothing to restore."));
        }
    }

    private static void ValidateDependencies(
        List<ModuleCompositionError> errors,
        Dictionary<string, ImplementationModule> composed)
    {
        foreach (ImplementationModule module in composed.Values)
        {
            foreach (ModuleDependency dependency in module.Dependencies)
            {
                if (!composed.TryGetValue(dependency.Id, out ImplementationModule? present))
                {
                    errors.Add(new ModuleCompositionError(module.Id,
                        ModuleCompositionCode.MissingDependency,
                        $"Module '{module.Id}' requires '{dependency.Id}', which is not composed."));
                    continue;
                }

                if (present.Version < dependency.MinVersion || present.Version > dependency.MaxVersion)
                {
                    errors.Add(new ModuleCompositionError(module.Id,
                        ModuleCompositionCode.DependencyVersionOutOfRange,
                        $"Module '{module.Id}' requires '{dependency.Id}' "
                            + $"{dependency.MinVersion}-{dependency.MaxVersion}, but version "
                            + $"{present.Version} is composed."));
                }
            }
        }
    }

    private static void ValidateConflicts(
        List<ModuleCompositionError> errors,
        Dictionary<string, ImplementationModule> composed)
    {
        foreach (ImplementationModule module in composed.Values)
        {
            foreach (string conflict in module.Conflicts)
            {
                if (composed.ContainsKey(conflict))
                {
                    errors.Add(new ModuleCompositionError(module.Id,
                        ModuleCompositionCode.ConflictingModules,
                        $"Modules '{module.Id}' and '{conflict}' declare each other incompatible."));
                }
            }
        }
    }

    private static void ValidateCapabilityCoverage(
        List<ModuleCompositionError> errors,
        DeviceDefinition device,
        Dictionary<string, ImplementationModule> composed)
    {
        // A capability nobody implements would publish a control that can never succeed. Failing
        // here keeps that a packaging error rather than a hardware command that times out.
        HashSet<string> implemented = new(StringComparer.OrdinalIgnoreCase);
        foreach (ImplementationModule module in composed.Values)
        {
            implemented.UnionWith(module.Capabilities);
        }

        foreach (string capability in device.Capabilities)
        {
            if (!implemented.Contains(capability))
            {
                errors.Add(new ModuleCompositionError(device.Id,
                    ModuleCompositionCode.CapabilityWithoutImplementation,
                    $"Device '{device.Id}' declares capability '{capability}', which no composed "
                        + "module implements."));
            }
        }
    }
}
