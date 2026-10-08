using System.Security.Cryptography;
using System.Text.Json;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using WSGM.Plugin.Gpu;
using WSGM.Plugin.IntelGpu.Controls;
using WSGM.Plugin.IntelGpu.Display;
using WSGM.Plugin.IntelGpu.Graphics;
using WSGM.Plugin.IntelGpu.Igcl;
using WSGM.Plugin.IntelGpu.Profiles;

namespace WSGM.Plugin.IntelGpu;

/// <summary>
///     Everything one IGCL session offers: the controls, their sections and the per-application
///     writers, built when the session opens and again when the active displays change.
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

    private static readonly CapabilityCategory[] AdapterCategories =
    [
        Category(FrameCategory, "Frame delivery", 0),
        Category(QualityCategory, "Image quality", 1),
        Category(SystemCategory, "Driver", 2),
        Category(StatusCategory, "Live status", 3)
    ];

    private static readonly CapabilityCategory[] DisplayCategories =
    [
        Category(RefreshCategory, "Refresh", 0),
        Category(PictureCategory, "Picture", 1),
        Category(ColorCategory, "Colour", 2),
        Category(PowerCategory, "Power savings", 3)
    ];

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
        _controls = controls.ToDictionary(control => control.Key, StringComparer.Ordinal);
        PublishedDescriptors = [.. controls.Select(control => control.Descriptor)];
        Fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Sections,
            Descriptors = PublishedDescriptors
        })));
    }

    /// <summary>Every published control, in publication order.</summary>
    public IReadOnlyList<IntelControl> Controls { get; }

    /// <summary>The declared sections.</summary>
    public IReadOnlyList<CapabilitySection> Sections { get; }

    /// <summary>The descriptors, in publication order.</summary>
    public IReadOnlyList<CapabilityDescriptor> PublishedDescriptors { get; }

    /// <summary>A hash of the sections and descriptors, so an unchanged set is not published again.</summary>
    public string Fingerprint { get; }

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

    /// <summary>Omits controls the driver explicitly reported as unsupported during discovery.</summary>
    /// <param name="unsupported">Routing keys whose startup support probes explicitly rejected their controls.</param>
    /// <returns>
    ///     This model when no control is removed, otherwise a new model omitting those controls and native-profile
    ///     targets while retaining section declarations.
    /// </returns>
    public IntelModel WithoutControls(IReadOnlySet<string> unsupported)
    {
        var controls = Controls.Where(control => !unsupported.Contains(control.Key)).ToArray();
        if (controls.Length == Controls.Count)
        {
            return this;
        }

        var targets = _targets.Where(target => !unsupported.Contains(target.Key))
            .ToDictionary(target => target.Key, target => target.Value, StringComparer.Ordinal);
        return new IntelModel(controls, Sections, targets);
    }

    public INativeProfileTarget? FindTarget(string capabilityId, string? instanceId)
    {
        return _targets.GetValueOrDefault(Key(capabilityId, instanceId));
    }

    /// <summary>Builds the model for an open session.</summary>
    /// <param name="session">The session.</param>
    /// <param name="memory">The shared-memory transport.</param>
    /// <param name="colors">The colour record.</param>
    /// <param name="adapterKeys">The display adapter class, enumerated once per session.</param>
    /// <param name="log">Receives every decision.</param>
    /// <returns>The model; empty when the driver offers nothing.</returns>
    /// <remarks>
    ///     The adapters come first, then the built-in panel, then the other displays in order.
    /// </remarks>
    public static IntelModel Build(
        IgclSession session,
        IntelGraphicsMemoryTransport memory,
        ColorStore colors,
        IReadOnlyList<AdapterClassEntry> adapterKeys,
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
            var title = several ? Descriptors.Label(adapter.Name, "Graphics") : "Graphics";
            List<IntelControl> built = [];
            Dictionary<string, INativeProfileTarget> builtTargets = new(StringComparer.Ordinal);
            BuildAdapter(session, adapter, instance, sectionId, adapterKeys, built, builtTargets, log);
            if (ReferenceEquals(adapter, memoryAdapter))
            {
                built.Add(new SharedMemoryControl(memory, instance, new Placement(sectionId, SystemCategory, 900)));
            }

            if (Section(sectionId, title, "Intel graphics driver settings", SectionIcon.Wrench, AdapterCategories,
                    built, controls, sections))
            {
                foreach (var (key, target) in builtTargets)
                {
                    targets[key] = target;
                }
            }
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
            var name = identity.Name;
            for (var suffix = 2; !names.Add(name); suffix++)
            {
                name = Descriptors.Label($"{identity.Name} {suffix}");
            }

            var sectionId = $"display-{ValueMapping.Fingerprint(identity.InstanceId)}";
            var vrrTaken = semanticVrrTaken;
            List<IntelControl> built = [];
            BuildDisplay(session, output, identity, sectionId, ref vrrTaken, colors, built, log);
            if (Section(sectionId, name, "Intel display settings", SectionIcon.Display, DisplayCategories, built,
                    controls, sections))
            {
                semanticVrrTaken = vrrTaken;
            }
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

    /// <summary>Adds a section and its controls when it has any.</summary>
    /// <returns><see langword="true" /> when the section was added.</returns>
    private static bool Section(
        string sectionId,
        string title,
        string description,
        SectionIcon icon,
        IReadOnlyList<CapabilityCategory> categories,
        List<IntelControl> built,
        List<IntelControl> controls,
        List<CapabilitySection> sections)
    {
        if (built.Count == 0)
        {
            return false;
        }

        controls.AddRange(built);
        sections.Add(new CapabilitySection
        {
            SectionId = sectionId,
            Key = SettingSectionKey.Custom,
            CustomTitle = title,
            CustomDescription = description,
            Icon = icon,
            SortOrder = sections.Count,
            Categories = categories
        });
        return true;
    }

    private static IgclAdapter? MemoryAdapter(IReadOnlyList<IgclAdapter> adapters, IntelGraphicsMemoryTransport memory)
    {
        if (!memory.IsAvailable)
        {
            return null;
        }

        foreach (var adapter in adapters)
        {
            if (AdapterClassKey.Matches(memory.MatchingDeviceId, adapter.PciDeviceId))
            {
                return adapter;
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
        IReadOnlyList<AdapterClassEntry> adapterKeys,
        List<IntelControl> controls,
        Dictionary<string, INativeProfileTarget> targets,
        IntelLog log)
    {
        var registryKeys = ThreeDKeysLocator.Find(adapterKeys, adapter.PciDeviceId);
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
                var info = ThreeDFeatureCatalog.Describe(details.FeatureType);
                perApplication = new PerApplicationSwitch(
                    new ThreeDFeature(session, adapter, details, info, FeatureShape.Scalar),
                    registryKeys,
                    $"{instance}|{details.FeatureType}",
                    ThreeDFeatureCatalog.CapabilityId(info));
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

            if (Shape(details) is not { } shape)
            {
                log.Info("graphics", $"{info.Label} reports value type {details.ValueType}, which this package "
                                     + "cannot read without guessing; not published.");
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
                placement = placement.Plus(1);
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
                    targets[control.Key] = target;
                }
            }
        }

        if (RetroScalingControl.TryCreate(session, adapter, instance, new Placement(sectionId, SystemCategory, 800))
            is { } retro)
        {
            controls.Add(retro);
        }
    }

    /// <summary>The shape a feature's value travels in, or null when this package cannot carry it.</summary>
    /// <remarks>
    ///     The game profiles and the live state are defined only as structures; any other reported type is
    ///     not one this package can read without guessing, and a guessed type is what crashed the driver.
    /// </remarks>
    private static FeatureShape? Shape(Ctl3dFeatureDetails details)
    {
        if (details.ValueType == (int)IgclValueType.Custom)
        {
            return details.FeatureType switch
            {
                ThreeDFeatureCatalog.EnduranceGaming => FeatureShape.Endurance,
                ThreeDFeatureCatalog.AdaptiveSyncPlus => FeatureShape.AdaptiveSync,
                ThreeDFeatureCatalog.AppProfiles => FeatureShape.AppProfile,
                ThreeDFeatureCatalog.LiveState => FeatureShape.LiveState,
                _ => null
            };
        }

        return details.FeatureType is ThreeDFeatureCatalog.AppProfiles or ThreeDFeatureCatalog.LiveState
               || !ThreeDFeatureCatalog.IsScalar(details.ValueType)
            ? null
            : FeatureShape.Scalar;
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
                foreach (var type in ThreeDFeatureCatalog.TierTypes(tierTypes))
                {
                    ThreeDFeature feature = new(session, adapter, details, info, shape) { TierType = type.Value };
                    var result = feature.ReadAppProfile(null, out var profile);
                    if (result is not (IgclResult.Success or IgclResult.DataNotFound))
                    {
                        log.Info("graphics",
                            $"{type.Id} is not published: the driver answered {IgclResult.Describe(result)}.");
                        continue;
                    }

                    feature.SupportedTierProfiles = profile.SupportedTierProfiles;
                    feature.DefaultEnabledTierProfiles = profile.DefaultEnabledTierProfiles;
                    log.Info(
                        "graphics",
                        $"{type.Id}: supported 0x{profile.SupportedTierProfiles:x}, default "
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
            var refresh = new Placement(sectionId, RefreshCategory, 0);
            controls.Add(new VariableRefreshControl(arcSync, instance, semantic, refresh));
            controls.Add(new ArcSyncProfileControl(arcSync, instance, refresh.Plus(1)));
            controls.AddRange(ArcSyncParameterControl.Build(arcSync, instance, refresh.Plus(2)));
        }

        var picture = new Placement(sectionId, PictureCategory, 0);
        controls.AddRange(ScalingControls.Build(session, output, instance, picture));
        controls.AddRange(SharpnessControls.Build(session, output, instance, picture.Plus(10)));
        if (WireFormatControl.TryCreate(session, output, instance, picture.Plus(20)) is { } wire)
        {
            controls.Add(wire);
        }

        controls.AddRange(DisplaySettingControls.Build(session, output, instance, picture.Plus(30)));
        if (ColorPipeline.TryCreate(session, output, instance, colors, log) is { } pipeline)
        {
            controls.AddRange(ColorControl.Build(pipeline, instance, new Placement(sectionId, ColorCategory, 0)));
        }

        var power = PowerSavingControls.SupportedFeatures(session, output);
        if (power == 0)
        {
            return;
        }

        var powerPlacement = new Placement(sectionId, PowerCategory, 0);
        controls.AddRange(PowerSavingControls.Build(session, output, power, instance, powerPlacement, log));
        if ((power & PowerSavingControls.FeatureLace) != 0)
        {
            controls.AddRange(LaceControls.Build(session, output, instance, powerPlacement.Plus(50)));
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
            IReadOnlyList<(string CapabilityId, CapabilityValue Value)> values, WriteAdmission admission)
        {
            // Fields no override names keep the global value, which is what the game would get anyway.
            var raw = _feature.Read(null, out var global) == IgclResult.Success ? global : _feature.DefaultValue();
            foreach (var (capabilityId, value) in values)
            {
                if (!_controls.TryGetValue(capabilityId, out var control) || !control.Validate(value, out _))
                {
                    return $"The value for {capabilityId} is not one the driver offers.";
                }

                raw = control.Encode(raw, value);
            }

            var result = _feature.Write(executable, raw, admission);
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

        public PerApplicationSwitch(
            ThreeDFeature feature,
            IReadOnlyList<string> registryKeys,
            string groupKey,
            string recordId)
        {
            _feature = feature;
            RegistryKeys = registryKeys;
            GroupKey = groupKey;
            RecordId = recordId;
        }

        public string RecordId { get; }

        public string GroupKey { get; }

        public IReadOnlyList<string> RegistryKeys { get; }

        public string? EnableFor(string executable, WriteAdmission admission)
        {
            RawFeatureValue raw = default;
            raw.Scalar.EnumValue = ThreeDFeatureCatalog.PerApplicationSettings;
            var result = _feature.Write(executable, raw, admission);
            return result == IgclResult.Success
                ? null
                : $"The driver answered {IgclResult.Describe(result)} to per-application settings for {executable}.";
        }
    }
}
