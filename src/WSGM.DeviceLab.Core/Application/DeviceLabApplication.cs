using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WSGM.DeviceLab.Core.Capture;
using WSGM.DeviceLab.Core.Catalog;
using WSGM.DeviceLab.Core.Fixtures;
using WSGM.DeviceLab.Core.Inventory;
using WSGM.DeviceLab.Core.Packaging;
using WSGM.DeviceLab.Core.Preflight;
using WSGM.DeviceLab.Core.Probes;
using WSGM.DeviceLab.Core.Scaffolding;

namespace WSGM.DeviceLab.Core.Application;

/// <summary>Offline candidate assessments and their eligible reviewed read probes.</summary>
public sealed record DeviceLabCandidateResult
{
    /// <summary>Exact logical device ID used for device-scoped matching.</summary>
    public required string TargetDeviceId { get; init; }

    /// <summary>Every candidate, including explained hard rejections.</summary>
    public IReadOnlyList<CandidateAssessment> Candidates { get; init; } = [];

    /// <summary>Reviewed read-only probes belonging to positively ranked modules.</summary>
    public IReadOnlyList<ReadProbeMetadata> ReadOnlyProbes { get; init; } = [];

    /// <summary>Offline matching and probe enumeration never authorize mutation.</summary>
    public bool MutationAuthorized => false;
}

/// <summary>Correlation findings and the limits that constrain their meaning.</summary>
public sealed record DeviceLabCorrelationResult
{
    /// <summary>Correlation-only findings linked to raw events.</summary>
    public IReadOnlyList<PassiveCorrelationFinding> Findings { get; init; } = [];

    /// <summary>Platform limitations retained alongside the findings.</summary>
    public IReadOnlyList<string> Limitations { get; init; } = [];

    /// <summary>Correlation can never authorize a write.</summary>
    public bool MutationAuthorized => false;
}

/// <summary>Admission, safety preflight, and disposable-host result for one reviewed read probe.</summary>
public sealed record DeviceLabReadProbeExecutionResult
{
    /// <summary>Selected immutable probe metadata.</summary>
    public required ReadProbeMetadata Probe { get; init; }

    /// <summary>Safety decision taken before ProbeHost could open the resource.</summary>
    public required DeviceLabPreflightDecision Preflight { get; init; }

    /// <summary>Typed disposable-host result, or null when preflight refused execution.</summary>
    public ReadProbeRunResult? Run { get; init; }

    /// <summary>A read-probe workflow never grants mutation authority.</summary>
    public bool MutationAuthorized => false;
}

/// <summary>
/// Shared Device Lab application facade used by the GUI and CLI command surfaces.
/// </summary>
/// <remarks>Creates a facade rooted in the current checkout and installed ProbeHost.</remarks>
/// <param name="repositoryRoot">Repository root, or <see langword="null"/> outside a checkout.</param>
/// <param name="probeHostPath">Path to the locally installed reviewed ProbeHost.</param>
public sealed class DeviceLabApplication(string? repositoryRoot, string probeHostPath)
{
    private const int MaximumInventoryBytes = 32 * 1024 * 1024;
    private readonly string? _repositoryRoot = repositoryRoot;
    private readonly string _probeHostPath = Path.GetFullPath(probeHostPath);

    /// <summary>Runs safe environment and output-path diagnostics.</summary>
    /// <param name="outputDirectory">Explicit output directory under review.</param>
    /// <param name="capturedAt">Timestamp to record.</param>
    /// <returns>Structured doctor report.</returns>
    public DeviceLabDoctorReport Doctor(string outputDirectory, DateTimeOffset capturedAt) =>
        DeviceLabDoctor.Run(outputDirectory, capturedAt, _repositoryRoot);

    /// <summary>Collects and persists one private or sanitized read-only inventory.</summary>
    /// <param name="outputDirectory">Explicit output directory.</param>
    /// <param name="shareable">Whether identifiers are redacted.</param>
    /// <param name="capturedAt">Timestamp to record.</param>
    /// <returns>Structured inventory workflow result.</returns>
    public DeviceLabInventoryResult Inventory(
        string outputDirectory,
        bool shareable,
        DateTimeOffset capturedAt) => DeviceLabInventoryWorkflow.Run(
            new DeviceLabInventoryRequest { OutputDirectory = outputDirectory, Shareable = shareable },
            capturedAt,
            _repositoryRoot);

    /// <summary>Ranks known modules and lists matching reviewed read probes without opening hardware.</summary>
    /// <param name="inventoryPath">Canonical inventory JSON.</param>
    /// <param name="targetDeviceId">Optional exact logical device ID.</param>
    /// <returns>Independent rank, evidence, eligibility, and read-probe outputs.</returns>
    public DeviceLabCandidateResult Candidates(string inventoryPath, string? targetDeviceId = null)
    {
        MachineInventory inventory = ReadInventory(inventoryPath);
        string target = string.IsNullOrWhiteSpace(targetDeviceId) ? DeviceId(inventory) : targetDeviceId;
        IReadOnlyList<CatalogEntry> catalog = BuiltInKnownImplementationCatalog.Create(_probeHostPath);
        IReadOnlyList<CandidateAssessment> candidates = CandidateMatcher.Rank(inventory, catalog, target);
        HashSet<string> accepted = candidates
            .Where(candidate => candidate.ReuseRank > 0)
            .Select(candidate => candidate.ModuleId)
            .ToHashSet(StringComparer.Ordinal);
        return new DeviceLabCandidateResult
        {
            TargetDeviceId = target,
            Candidates = candidates,
            ReadOnlyProbes = [.. catalog
                .Where(entry => accepted.Contains(entry.Module.Id))
                .SelectMany(entry => entry.ReadProbes)
                .OrderBy(probe => probe.Id, StringComparer.Ordinal)],
        };
    }

    /// <summary>Reads and validates the exact inert recipe bytes an operator must review.</summary>
    /// <param name="recipePath">Imported recipe JSON.</param>
    /// <returns>Closed observation steps and their approval hash.</returns>
    public ObserveOnlyRecipeReview ReviewCaptureRecipe(string recipePath) =>
        ObserveOnlyCaptureWorkflow.Review(recipePath);

    /// <summary>Runs one positively matched, reviewed, locally hash-pinned read probe.</summary>
    /// <param name="inventoryPath">Inventory used for exact candidate gates.</param>
    /// <param name="probeId">Reviewed built-in probe ID.</param>
    /// <param name="outputDirectory">Explicit safe root for the disposable session.</param>
    /// <param name="cancellationToken">Whole-probe cancellation.</param>
    /// <returns>Independent preflight and typed execution results.</returns>
    public async Task<DeviceLabReadProbeExecutionResult> RunReadProbeAsync(
        string inventoryPath,
        string probeId,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(probeId);
        MachineInventory inventory = ReadInventory(inventoryPath);
        DeviceLabCandidateResult candidateResult = Candidates(inventoryPath);
        ReadProbeMetadata probe = candidateResult.ReadOnlyProbes.SingleOrDefault(item =>
            string.Equals(item.Id, probeId, StringComparison.Ordinal))
            ?? throw new InvalidDataException("The named probe is not a positively matched reviewed read probe.");
        bool hostInstalled = File.Exists(_probeHostPath);
        string installedHash = hostInstalled
            ? ReadProbeHostSupervisor.HashFile(_probeHostPath)
            : new string('0', 64);
        DeviceLabDoctorReport doctor = Doctor(outputDirectory, DateTimeOffset.UtcNow);
        DeviceLabOwnerInspection owner = DeviceLabOwnerInspector.Inspect();
        bool elevated = doctor.Checks.Any(check =>
            check.Code == "permissions.elevation" && check.Status is DeviceLabDoctorStatus.Pass);
        bool continuousIntegration = IsTruthy(Environment.GetEnvironmentVariable("CI"))
            || IsTruthy(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));
        bool exactFamilyMatched = ProbeFamilyMatches(probe.FamilyId, candidateResult.TargetDeviceId);
        bool exactEndpointMatched = ProbeEndpointMatches(probe.EndpointId, inventory);
        DeviceLabPreflightDecision preflight = DeviceLabSafetyPreflight.Evaluate(
            new DeviceLabOperationRequirements
            {
                OperationId = probe.Id,
                ResourceId = probe.ResourceId,
                Access = DeviceLabOperationAccess.ReviewedReadProbe,
                Origin = probe.Origin,
                IsLocallyInstalled = hostInstalled,
                IsHashPinned = string.Equals(installedHash, probe.ImplementationSha256, StringComparison.OrdinalIgnoreCase),
                ExactFamilyMatched = exactFamilyMatched,
                ExactEndpointMatched = exactEndpointMatched,
                RequiresElevation = probe.RequiresElevation,
            },
            new DeviceLabSafetySnapshot
            {
                Doctor = doctor,
                DeviceIntegrationEnabled = owner.DeviceIntegrationEnabled,
                OwnerDiscovery = owner.State,
                ActiveDevice = owner.Snapshot,
                PowerThermal = WindowsPreflightInspection.CollectPowerThermal(),
                IsElevated = elevated,
                IsUserInteractive = Environment.UserInteractive,
                IsContinuousIntegration = continuousIntegration,
                ExternalComponents = [],
            });
        if (preflight.Status is DeviceLabDoctorStatus.Blocked
            || preflight.Route is not DeviceLabAccessRoute.DirectReadOnly)
        {
            return new DeviceLabReadProbeExecutionResult { Probe = probe, Preflight = preflight };
        }

        string sessionDirectory = Path.Combine(
            Path.GetFullPath(outputDirectory),
            $"probe-{SafeFileName(probe.Id)}-{Guid.NewGuid():N}");
        ReadProbeRunResult run = await ReadProbeHostSupervisor.RunAsync(
            probe,
            new ReadProbeAdmissionContext
            {
                FamilyId = probe.FamilyId,
                EndpointId = probe.EndpointId,
                IsLocallyInstalled = hostInstalled,
                InstalledSha256 = installedHash,
                DeveloperModeEnabled = false,
                ExplicitDeveloperAction = false,
                AutomaticSweep = true,
            },
            preflight,
            _probeHostPath,
            sessionDirectory,
            new SystemReadProbeProcessLauncher(),
            cancellationToken).ConfigureAwait(false);
        return new DeviceLabReadProbeExecutionResult { Probe = probe, Preflight = preflight, Run = run };
    }

    /// <summary>Prepares a private observe-only session and a not-yet-written privacy preview.</summary>
    /// <param name="request">Explicit recipe, path, and operator gates.</param>
    /// <param name="capturedAt">Session timestamp.</param>
    /// <param name="cancellationToken">Whole-session cancellation.</param>
    /// <returns>Prepared export or a closed refusal.</returns>
    public Task<ObserveOnlyCaptureResult> PrepareCaptureAsync(
        ObserveOnlyCaptureRequest request,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken) => ObserveOnlyCaptureWorkflow.PrepareAsync(
            request,
            capturedAt,
            _repositoryRoot,
            cancellationToken);

    /// <summary>Exports a prepared sanitized capture after separate privacy approval.</summary>
    /// <param name="plan">Prepared capture export.</param>
    /// <param name="exportPreviewConfirmed">Whether the actual preview was accepted.</param>
    /// <returns>Export result.</returns>
    public CaptureExportResult ExportCapture(CaptureExportPlan plan, bool exportPreviewConfirmed) =>
        ObserveOnlyCaptureWorkflow.Export(plan, exportPreviewConfirmed, _repositoryRoot);

    /// <summary>Verifies and summarizes one sanitized capture.</summary>
    /// <param name="capturePath">Shareable capture path.</param>
    /// <returns>Inspection linked to verified bundle entries.</returns>
    public CaptureInspection Inspect(string capturePath)
    {
        CaptureBundleReadResult read = ReadCapture(capturePath);
        return CaptureWorkbench.Inspect(read.Bundle!);
    }

    /// <summary>Compares verified capture content hashes.</summary>
    /// <param name="leftPath">Left capture.</param>
    /// <param name="rightPath">Right capture.</param>
    /// <returns>Entry additions, removals, and changes.</returns>
    public IReadOnlyList<CaptureEntryDifference> Diff(string leftPath, string rightPath)
    {
        CaptureBundleReadResult left = ReadCapture(leftPath);
        CaptureBundleReadResult right = ReadCapture(rightPath);
        return CaptureWorkbench.Diff(left.EntryHashes, right.EntryHashes);
    }

    /// <summary>Runs correlation-only analysis over a verified capture.</summary>
    /// <param name="capturePath">Shareable capture.</param>
    /// <param name="actionId">Operator action marker ID.</param>
    /// <param name="sourceIds">Expected source lanes.</param>
    /// <returns>Findings that retain evidence links and limitations.</returns>
    public DeviceLabCorrelationResult Correlate(
        string capturePath,
        string actionId,
        IReadOnlySet<string> sourceIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        ArgumentNullException.ThrowIfNull(sourceIds);
        CaptureBundleReadResult read = ReadCapture(capturePath);
        IReadOnlyList<CaptureStreamEvent> events = [.. read.Bundle!.Streams
            .SelectMany(stream => stream.Events)
            .OrderBy(captureEvent => captureEvent.GlobalSequence)];
        return new DeviceLabCorrelationResult
        {
            Findings = PassiveCorrelationAnalyzer.Analyze(new PassiveCorrelationRequest
            {
                AnalysisId = $"correlate-{actionId}",
                ActionId = actionId,
                ExpectedSourceIds = sourceIds,
                Events = events,
                ContextWindowTicks = Math.Max(1, read.Bundle.Manifest.QpcFrequency * 2),
            }),
            Limitations = PassiveCaptureLimitations.All,
        };
    }

    /// <summary>Extracts a deterministic simulator-only fixture from a verified capture.</summary>
    /// <param name="capturePath">Shareable capture.</param>
    /// <param name="fixtureId">New fixture ID.</param>
    /// <param name="outputDirectory">New explicit output directory.</param>
    /// <returns>Fixture extraction result.</returns>
    public FixtureManifest ExtractFixture(
        string capturePath,
        string fixtureId,
        string outputDirectory)
    {
        byte[] bytes = File.ReadAllBytes(capturePath);
        using MemoryStream input = new(bytes, writable: false);
        CaptureBundleReadResult read = CaptureBundleReader.Read(input);
        EnsureCapture(read);
        return FixtureExtractionWorkflow.Extract(
            read.Bundle!,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            fixtureId,
            outputDirectory,
            Boundaries());
    }

    /// <summary>Generates a conservative read-only scaffold from verified capture evidence.</summary>
    /// <param name="capturePath">Shareable capture.</param>
    /// <param name="outputDirectory">New scaffold directory.</param>
    /// <param name="publisher">Unverified contributor label.</param>
    /// <returns>Deterministic scaffold plan.</returns>
    public ScaffoldGenerationPlan Scaffold(
        string capturePath,
        string outputDirectory,
        string publisher) => ScaffoldFromCaptureWorkflow.Run(
            capturePath,
            outputDirectory,
            publisher,
            Boundaries());

    /// <summary>Runs offline package validation without loading plugin code.</summary>
    /// <param name="packageDirectory">Package source directory.</param>
    /// <returns>Validation report that grants no trust or runtime authority.</returns>
    public PluginPackageValidationReport ValidateOffline(string packageDirectory) =>
        PluginPackageWorkflow.ValidateOffline(packageDirectory);

    /// <summary>Validates and deterministically packs a plugin without granting trust.</summary>
    /// <param name="packageDirectory">Package source directory.</param>
    /// <param name="outputPath">New package archive path.</param>
    /// <returns>The validation report that authorized only archive creation.</returns>
    public PluginPackageValidationReport Pack(string packageDirectory, string outputPath) =>
        PluginPackageWorkflow.Pack(packageDirectory, outputPath, Boundaries());

    /// <summary>Imports reviewed glyph sources into deterministic WSGM-owned safe assets.</summary>
    /// <param name="packageDirectory">Package source containing canonical profiles and notices.</param>
    /// <param name="outputDirectory">New directory receiving generated package-layout files.</param>
    /// <returns>Deterministic generation report that grants no package authority.</returns>
    public GlyphPackageGenerationReport GenerateGlyphs(
        string packageDirectory,
        string outputDirectory) => GlyphPackageGenerationWorkflow.Generate(
            packageDirectory,
            outputDirectory,
            Boundaries());

    private DeviceLabPathBoundaries Boundaries() => DeviceLabPathBoundaries.ForCurrentUser(_repositoryRoot);

    private static MachineInventory ReadInventory(string path)
    {
        FileInfo file = new(path);
        if (!file.Exists || file.Length is <= 0 or > MaximumInventoryBytes)
        {
            throw new InvalidDataException("Inventory is absent, empty, or oversized.");
        }

        MachineInventory? inventory = JsonSerializer.Deserialize(
            File.ReadAllBytes(path),
            DeviceLabJsonContext.Default.MachineInventory);
        return inventory is not null && inventory.SchemaVersion == WindowsInventoryCollector.CurrentSchemaVersion
            ? inventory
            : throw new InvalidDataException("Inventory schema is unsupported.");
    }

    private static CaptureBundleReadResult ReadCapture(string path)
    {
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        CaptureBundleReadResult read = CaptureBundleReader.Read(input);
        EnsureCapture(read);
        return read;
    }

    private static void EnsureCapture(CaptureBundleReadResult read)
    {
        if (!read.Succeeded || read.Bundle is null)
        {
            throw new InvalidDataException($"Capture rejected ({read.Failure}): {read.Detail}");
        }
    }

    private static string DeviceId(MachineInventory inventory) =>
        string.Equals(inventory.Firmware.BaseboardProduct, "MS-1T52", StringComparison.OrdinalIgnoreCase)
            ? "ms-1t52"
            : $"observed-{(inventory.Firmware.BaseboardProduct ?? "unknown").ToLowerInvariant()}";

    private static bool IsTruthy(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

    private static string SafeFileName(string value) => string.Concat(value.Select(character =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '-'));

    private static bool ProbeFamilyMatches(string familyId, string targetDeviceId) => familyId switch
    {
        "msi.claw-a2vm.ms-1t52" => string.Equals(targetDeviceId, "ms-1t52", StringComparison.Ordinal),
        _ => false,
    };

    private static bool ProbeEndpointMatches(string endpointId, MachineInventory inventory)
    {
        int namespaceSeparator = endpointId.IndexOf(':');
        int methodSeparator = endpointId.IndexOf('.', namespaceSeparator + 1);
        if (namespaceSeparator <= 0 || methodSeparator <= namespaceSeparator + 1)
        {
            return false;
        }

        string wmiNamespace = endpointId[..namespaceSeparator].Replace('/', '\\');
        string className = endpointId[(namespaceSeparator + 1)..methodSeparator];
        int selectorSeparator = endpointId.IndexOf(':', methodSeparator + 1);
        string methodName = selectorSeparator < 0
            ? endpointId[(methodSeparator + 1)..]
            : endpointId[(methodSeparator + 1)..selectorSeparator];
        return inventory.WmiClasses.Any(item =>
            item.Access is WmiAccess.Available
            && string.Equals(item.Namespace, wmiNamespace, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ClassName, className, StringComparison.Ordinal)
            && item.MethodNames.Contains(methodName, StringComparer.Ordinal));
    }
}
