using Microsoft.Win32;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using WSGM.Plugin.IntelGpu.Controls;
using WSGM.Plugin.IntelGpu.Display;
using WSGM.Plugin.IntelGpu.Graphics;
using WSGM.Plugin.IntelGpu.Igcl;
using WSGM.Plugin.IntelGpu.Profiles;

namespace WSGM.Plugin.IntelGpu;

/// <summary>
///     Everything one IGCL session offers: the controls, their sections and the per-application
///     writers, built once when the session opens.
/// </summary>
internal sealed class IntelModel
{
    private const string FrameCategory = "frames";
    private const string QualityCategory = "quality";
    private const string SystemCategory = "system";
    private const string StatusCategory = "status";
    private const string RefreshCategory = "refresh";
    private const string PictureCategory = "picture";
    private const string ColorCategory = "color";
    private const string PowerCategory = "power";

    private readonly Dictionary<string, IntelControl> _controls;
    private readonly Dictionary<string, INativeProfileTarget> _targets;

    private IntelModel(
        IReadOnlyList<IntelControl> controls,
        IReadOnlyList<CapabilitySection> sections,
        Dictionary<string, INativeProfileTarget> targets)
    {
        Controls = controls;
        Sections = sections;
        _targets = targets;
        _controls = controls.ToDictionary(control => Key(control.CapabilityId, control.InstanceId), StringComparer.Ordinal);
    }

    /// <summary>Every published control, in publication order.</summary>
    public IReadOnlyList<IntelControl> Controls { get; }

    /// <summary>The declared sections.</summary>
    public IReadOnlyList<CapabilitySection> Sections { get; }

    /// <summary>The routing key of one capability instance.</summary>
    /// <param name="capabilityId">The capability id.</param>
    /// <param name="instanceId">The instance id.</param>
    /// <returns>The key.</returns>
    public static string Key(string capabilityId, string? instanceId)
    {
        return $"{capabilityId}|{instanceId}";
    }

    public IntelControl? Find(string capabilityId, string? instanceId)
    {
        return _controls.GetValueOrDefault(Key(capabilityId, instanceId));
    }

    public INativeProfileTarget? FindTarget(string capabilityId, string? instanceId)
    {
        return _targets.GetValueOrDefault(Key(capabilityId, instanceId));
    }

    /// <summary>Builds the model for an open session.</summary>
    /// <param name="session">The session.</param>
    /// <param name="memory">The shared-memory transport.</param>
    /// <param name="colors">The colour record.</param>
    /// <param name="log">Receives every decision.</param>
    /// <returns>The model; empty when the driver offers nothing.</returns>
    public static IntelModel Build(
        IgclSession session,
        IntelGraphicsMemoryTransport memory,
        ColorStore colors,
        IntelLog log)
    {
        List<IntelControl> controls = [];
        List<CapabilitySection> sections = [];
        Dictionary<string, INativeProfileTarget> targets = new(StringComparer.Ordinal);
        var several = session.Adapters.Count > 1;
        var memoryAdapter = MemoryAdapter(session.Adapters, memory);
        foreach (var adapter in session.Adapters)
        {
            var instance = AdapterInstance(adapter);
            var sectionId = several ? $"graphics-{adapter.Index}" : "graphics";
            var before = controls.Count;
            BuildAdapter(session, adapter, instance, sectionId, controls, targets, log);
            if (ReferenceEquals(adapter, memoryAdapter))
            {
                controls.Add(new SharedMemoryControl(memory, instance, new Placement(sectionId, SystemCategory, 900)));
            }

            if (controls.Count == before)
            {
                continue;
            }

            sections.Add(new CapabilitySection
            {
                SectionId = sectionId,
                Key = SettingSectionKey.Custom,
                CustomTitle = several ? Descriptors.Label(adapter.Name.Length > 0 ? adapter.Name : "Graphics") : "Graphics",
                CustomDescription = "Intel graphics driver settings",
                Icon = SectionIcon.Wrench,
                SortOrder = sections.Count,
                Categories =
                [
                    Category(FrameCategory, "Frame delivery", 0),
                    Category(QualityCategory, "Image quality", 1),
                    Category(SystemCategory, "Driver", 2),
                    Category(StatusCategory, "Live status", 3)
                ]
            });
        }

        var semanticVrrTaken = false;
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        // The built-in panel first, so the variable refresh role goes to it when it has variable refresh and
        // its section gets the lowest sort order among the displays.
        var ordered = session.Outputs
            .Select(output => (Output: output, Identity: DisplayIdentityResolver.Resolve(output, log)))
            .OrderByDescending(entry => entry.Identity.Internal)
            .ToArray();
        foreach (var (output, identity) in ordered)
        {
            if (sections.Count >= CapabilitySection.MaxSections)
            {
                log.Warn("display", $"{identity.Name}: the section limit is reached; not published.");
                continue;
            }

            var name = identity.Name;
            for (var suffix = 2; !names.Add(name); suffix++)
            {
                name = Descriptors.Label($"{identity.Name} {suffix}");
            }

            var sectionId = $"display-{ValueMapping.Fingerprint(identity.InstanceId)}";
            var before = controls.Count;
            BuildDisplay(session, output, identity, sectionId, ref semanticVrrTaken, colors, controls, log);
            if (controls.Count == before)
            {
                continue;
            }

            sections.Add(new CapabilitySection
            {
                SectionId = sectionId,
                Key = SettingSectionKey.Custom,
                CustomTitle = name,
                CustomDescription = "Intel display settings",
                Icon = SectionIcon.Display,
                SortOrder = sections.Count,
                Categories =
                [
                    Category(RefreshCategory, "Refresh", 0),
                    Category(PictureCategory, "Picture", 1),
                    Category(ColorCategory, "Colour", 2),
                    Category(PowerCategory, "Power savings", 3)
                ]
            });
        }

        return new IntelModel(controls, sections, targets);
    }

    /// <summary>The stable instance id of an adapter.</summary>
    /// <param name="adapter">The adapter.</param>
    /// <returns>
    ///     <c>pci-8086-&lt;device&gt;-&lt;bus&gt;-&lt;device&gt;-&lt;function&gt;</c>, or with the enumeration
    ///     index when the driver does not report the bus address. The LUID is not used: it changes every
    ///     boot.
    /// </returns>
    internal static string AdapterInstance(IgclAdapter adapter)
    {
        return adapter.HasBusAddress
            ? $"pci-{adapter.PciVendorId:x4}-{adapter.PciDeviceId:x4}-{adapter.Bus:x2}-{adapter.Device:x2}-{adapter.Function:x}"
            : $"pci-{adapter.PciVendorId:x4}-{adapter.PciDeviceId:x4}-{adapter.Index}";
    }

    private static IgclAdapter? MemoryAdapter(IReadOnlyList<IgclAdapter> adapters, IntelGraphicsMemoryTransport memory)
    {
        if (!memory.IsAvailable)
        {
            return null;
        }

        if (memory.MatchingDeviceId is { } matching)
        {
            var match = adapters.FirstOrDefault(adapter =>
                matching.Contains($"dev_{adapter.PciDeviceId:x4}", StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        // The split belongs to the integrated GPU; a lone adapter is that one.
        return adapters.FirstOrDefault(adapter => adapter.Integrated) ?? (adapters.Count == 1 ? adapters[0] : null);
    }

    private static void BuildAdapter(
        IgclSession session,
        IgclAdapter adapter,
        string instance,
        string sectionId,
        List<IntelControl> controls,
        Dictionary<string, INativeProfileTarget> targets,
        IntelLog log)
    {
        var registryKeys = ThreeDKeysLocator.Find(
            Registry.LocalMachine,
            IntelGraphicsMemoryTransport.AdapterClassKey,
            adapter.PciDeviceId);
        var table = session.Read3dCapabilities(adapter);

        // Intel's sample (3D_Feature_Sample_App.cpp, CtlTestFrameGeneration) notes that a per-application
        // value applies only once feature 15 is set to per-application for that executable, so every
        // per-application write of this adapter carries the switch.
        PerApplicationSwitch? perApplication = null;
        foreach (var details in table)
        {
            if (details.FeatureType == ThreeDFeatureCatalog.GlobalOrPerApp
                && details.ValueType == (int)IgclValueType.Enum
                && details.PerAppSupport != 0)
            {
                perApplication = new PerApplicationSwitch(
                    new ThreeDFeature(session, adapter, details,
                        ThreeDFeatureCatalog.Describe(details.FeatureType), FeatureShape.Scalar),
                    registryKeys,
                    $"{instance}|{details.FeatureType}");
            }
        }

        foreach (var details in table)
        {
            var info = ThreeDFeatureCatalog.Describe(details.FeatureType);
            log.Info(
                "graphics",
                $"Adapter {adapter.Index} feature {details.FeatureType} ({info.Label}): type {details.ValueType}, "
                + $"per-app {details.PerAppSupport}, misc 0x{details.FeatureMiscSupport:x}, "
                + $"mask 0x{details.Value.EnumSupportedTypes:x}.");
            if (ThreeDFeatureCatalog.SkipReason(details.FeatureType) is { } reason)
            {
                log.Info("graphics", $"{info.Label} is not published: {reason}.");
                continue;
            }

            if (details.FeatureType is ThreeDFeatureCatalog.AppProfiles or ThreeDFeatureCatalog.LiveState
                && details.ValueType != (int)IgclValueType.Custom)
            {
                // The header defines these only as structures; any other reported type is not one this
                // package can read without guessing, and a guessed type is what crashed the driver.
                log.Info("graphics", $"{info.Label} reports value type {details.ValueType}, not custom; not published.");
                continue;
            }

            FeatureShape shape;
            if (details.ValueType == (int)IgclValueType.Custom)
            {
                shape = details.FeatureType switch
                {
                    ThreeDFeatureCatalog.EnduranceGaming => FeatureShape.Endurance,
                    ThreeDFeatureCatalog.AdaptiveSyncPlus => FeatureShape.AdaptiveSync,
                    ThreeDFeatureCatalog.AppProfiles => FeatureShape.AppProfile,
                    ThreeDFeatureCatalog.LiveState => FeatureShape.LiveState,
                    _ => (FeatureShape)(-1)
                };
                if (!Enum.IsDefined(shape))
                {
                    log.Info("graphics", $"{info.Label} uses a custom structure this package does not know.");
                    continue;
                }
            }
            else if (ThreeDFeatureCatalog.IsScalar(details.ValueType))
            {
                shape = FeatureShape.Scalar;
            }
            else
            {
                log.Info("graphics", $"{info.Label} reports value type {details.ValueType}; not published.");
                continue;
            }

            var category = details.FeatureType switch
            {
                ThreeDFeatureCatalog.FramePacing or ThreeDFeatureCatalog.EnduranceGaming
                    or ThreeDFeatureCatalog.FrameLimit or ThreeDFeatureCatalog.GamingFlipModes
                    or ThreeDFeatureCatalog.AdaptiveSyncPlus or ThreeDFeatureCatalog.LowLatency
                    or ThreeDFeatureCatalog.FrameGeneration or ThreeDFeatureCatalog.VrrWindowedBlt => FrameCategory,
                ThreeDFeatureCatalog.Anisotropic or ThreeDFeatureCatalog.Cmaa
                    or ThreeDFeatureCatalog.TextureFilteringQuality or ThreeDFeatureCatalog.AdaptiveTessellation
                    or ThreeDFeatureCatalog.SharpeningFilter or ThreeDFeatureCatalog.Msaa => QualityCategory,
                ThreeDFeatureCatalog.LiveState => StatusCategory,
                _ => SystemCategory
            };
            var placement = new Placement(sectionId, category, details.FeatureType * 10);
            foreach (var feature in Features(session, adapter, details, info, shape, log))
            {
                var built = ThreeDFeatureControl.Build(feature, instance, placement, log);
                controls.AddRange(built);
                placement = placement with { Order = placement.Order + 1 };
                var writable = built.Where(control => control.Descriptor.SupportsWrite).ToArray();
                if (!feature.PerApplication || writable.Length == 0)
                {
                    continue;
                }

                var group = shape == FeatureShape.AppProfile
                    ? $"{instance}|{details.FeatureType}|{feature.TierType}"
                    : $"{instance}|{details.FeatureType}";
                NativeFeatureTarget target = new(feature, writable, registryKeys, group, perApplication);
                foreach (var control in writable)
                {
                    targets[Key(control.CapabilityId, control.InstanceId)] = target;
                }
            }
        }

        if (RetroScalingControl.TryCreate(session, adapter, instance, new Placement(sectionId, SystemCategory, 800))
            is { } retro)
        {
            controls.Add(retro);
        }
    }

    /// <summary>The driver values one reported feature stands for, with their capability structures read.</summary>
    /// <remarks>
    ///     Every feature is one value, except the game profiles (feature 11): each tier type in
    ///     <c>ctl_3d_app_profiles_caps_t.SupportedTierTypes</c> is its own value, asked for through the
    ///     structure's <c>TierType</c> input, so each becomes its own feature with its own supported tiers.
    /// </remarks>
    private static IEnumerable<ThreeDFeature> Features(
        IgclSession session,
        IgclAdapter adapter,
        Ctl3dFeatureDetails details,
        ThreeDFeatureInfo info,
        FeatureShape shape,
        IntelLog log)
    {
        switch (shape)
        {
            case FeatureShape.Endurance:
            {
                ThreeDFeature feature = new(session, adapter, details, info, shape);
                if (session.TryReadCustomCaps<CtlEnduranceGamingCaps>(adapter, details, out var endurance))
                {
                    feature.EnduranceCaps = endurance;
                }

                yield return feature;
                yield break;
            }
            case FeatureShape.AdaptiveSync:
            {
                ThreeDFeature feature = new(session, adapter, details, info, shape);
                if (session.TryReadCustomCaps<CtlAdaptiveSyncCaps>(adapter, details, out var adaptive))
                {
                    feature.AdaptiveSyncCaps = adaptive;
                }

                yield return feature;
                yield break;
            }
            case FeatureShape.AppProfile:
            {
                var tierTypes = session.TryReadCustomCaps<Ctl3dAppProfilesCaps>(adapter, details, out var caps)
                    ? caps.SupportedTierTypes
                    : 0;
                log.Info("graphics", $"{info.Label}: tier types 0x{tierTypes:x}.");
                foreach (var (tierType, id, _) in ThreeDFeatureCatalog.TierTypes(tierTypes))
                {
                    ThreeDFeature feature = new(session, adapter, details, info, shape) { TierType = tierType };
                    var result = feature.ReadAppProfile(null, out var profile);
                    if (result is not (IgclResult.Success or IgclResult.DataNotFound))
                    {
                        log.Info("graphics", $"{id} is not published: the driver answered {IgclResult.Describe(result)}.");
                        continue;
                    }

                    feature.SupportedTierProfiles = profile.SupportedTierProfiles;
                    feature.DefaultEnabledTierProfiles = profile.DefaultEnabledTierProfiles;
                    log.Info(
                        "graphics",
                        $"{id}: supported 0x{profile.SupportedTierProfiles:x}, default "
                        + $"0x{profile.DefaultEnabledTierProfiles:x}, enabled 0x{profile.EnabledTierProfiles:x} "
                        + $"({IgclResult.Describe(result)}).");
                    yield return feature;
                }

                yield break;
            }
            default:
                yield return new ThreeDFeature(session, adapter, details, info, shape);
                yield break;
        }
    }

    private static void BuildDisplay(
        IgclSession session,
        IgclOutput output,
        DisplayIdentity identity,
        string sectionId,
        ref bool semanticVrrTaken,
        ColorStore colors,
        List<IntelControl> controls,
        IntelLog log)
    {
        var instance = identity.InstanceId;
        if (ArcSyncDisplay.TryCreate(session, output, log, identity.Name) is { } arcSync)
        {
            // One display carries the variable refresh role, the one Steam's own toggle follows: the
            // built-in panel when it has variable refresh, otherwise the first display that does.
            var semantic = !semanticVrrTaken;
            semanticVrrTaken = true;
            controls.Add(new VariableRefreshControl(arcSync, instance, semantic,
                new Placement(sectionId, RefreshCategory, 0)));
            controls.Add(new ArcSyncProfileControl(arcSync, instance, new Placement(sectionId, RefreshCategory, 1)));
            controls.AddRange(ArcSyncParameterControl.Build(arcSync, instance,
                new Placement(sectionId, RefreshCategory, 2)));
        }

        controls.AddRange(ScalingControl.Build(session, output, instance, new Placement(sectionId, PictureCategory, 0)));
        controls.AddRange(SharpnessControl.Build(session, output, instance,
            new Placement(sectionId, PictureCategory, 10)));
        if (WireFormatControl.TryCreate(session, output, instance, new Placement(sectionId, PictureCategory, 20))
            is { } wire)
        {
            controls.Add(wire);
        }

        controls.AddRange(DisplaySettingControl.Build(session, output, instance,
            new Placement(sectionId, PictureCategory, 30)));
        if (ColorPipeline.TryCreate(session, output, instance, colors, log) is { } pipeline)
        {
            controls.AddRange(ColorControl.Build(pipeline, instance, new Placement(sectionId, ColorCategory, 0)));
        }

        var power = PowerSavingControl.SupportedFeatures(session, output);
        if (power != 0)
        {
            controls.AddRange(PowerSavingControl.Build(session, output, power, instance,
                new Placement(sectionId, PowerCategory, 0), log));
            if ((power & PowerSavingControl.FeatureLace) != 0)
            {
                controls.AddRange(LaceControl.Build(session, output, instance,
                    new Placement(sectionId, PowerCategory, 50)));
            }
        }
    }

    private static CapabilityCategory Category(string id, string title, int order)
    {
        return new CapabilityCategory
        {
            CategoryId = id,
            Key = SettingSectionKey.Custom,
            CustomTitle = title,
            SortOrder = order
        };
    }

    /// <summary>The per-application writer of one 3D feature.</summary>
    private sealed class NativeFeatureTarget : INativeProfileTarget
    {
        private readonly Dictionary<string, ThreeDFeatureControl> _controls;
        private readonly ThreeDFeature _feature;

        public NativeFeatureTarget(
            ThreeDFeature feature,
            IReadOnlyList<ThreeDFeatureControl> controls,
            IReadOnlyList<string> registryKeys,
            string groupKey,
            INativeApplicationSwitch? applicationSwitch)
        {
            _feature = feature;
            _controls = controls.ToDictionary(control => control.CapabilityId, StringComparer.Ordinal);
            RegistryKeys = registryKeys;
            GroupKey = groupKey;
            ApplicationSwitch = applicationSwitch;
        }

        public string GroupKey { get; }

        public IReadOnlyList<string> RegistryKeys { get; }

        public INativeApplicationSwitch? ApplicationSwitch { get; }

        public string? WriteForApplication(
            string executable,
            IReadOnlyList<(string CapabilityId, CapabilityValue Value)> values)
        {
            // Fields no override names keep the global value, which is what the game would get anyway.
            var raw = _feature.Read(null, out var global) == IgclResult.Success ? global : _feature.DefaultValue();
            foreach (var (capabilityId, value) in values)
            {
                if (!_controls.TryGetValue(capabilityId, out var control) || !control.Validate(value, out var error))
                {
                    return $"The value for {capabilityId} is not one the driver offers.";
                }

                raw = control.Encode(raw, value);
            }

            var result = _feature.Write(executable, raw);
            return result == IgclResult.Success
                ? null
                : $"The driver answered {IgclResult.Describe(result)} to {_feature.Info.Label} for {executable}.";
        }
    }

    /// <summary>
    ///     Feature 15, <c>CTL_3D_FEATURE_GLOBAL_OR_PER_APP</c>, of one adapter: turns a game's own values
    ///     on in the driver.
    /// </summary>
    /// <remarks>
    ///     <c>igcl_api.h</c> lines 1838-1845 document the values and Intel's sample writes
    ///     <c>CTL_3D_GLOBAL_OR_PER_APP_TYPES_PER_APP</c> with the executable's name, as an enum. Its note
    ///     on the frame generation test says a per-application value applies only once that is set.
    /// </remarks>
    private sealed class PerApplicationSwitch : INativeApplicationSwitch
    {
        private readonly ThreeDFeature _feature;

        public PerApplicationSwitch(ThreeDFeature feature, IReadOnlyList<string> registryKeys, string groupKey)
        {
            _feature = feature;
            RegistryKeys = registryKeys;
            GroupKey = groupKey;
        }

        public string GroupKey { get; }

        public IReadOnlyList<string> RegistryKeys { get; }

        public string? EnableFor(string executable)
        {
            RawFeatureValue raw = default;
            raw.Scalar.EnumValue = ThreeDFeatureCatalog.PerApplicationSettings;
            var result = _feature.Write(executable, raw);
            return result == IgclResult.Success
                ? null
                : $"The driver answered {IgclResult.Describe(result)} to per-application settings for {executable}.";
        }
    }
}
