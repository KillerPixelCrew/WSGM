using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>Production RTSS adapter over the installed 64-bit profile API.</summary>
internal sealed class RtssNativeAdapter : IRtssAdapter
{
    private const string FrameLimitProperty = "FramerateLimit";

    private const string OverlayEnabledProperty = "EnableOSD";

    // Levels 1..3 are presets and 4 is Custom. Level 0 clears only WSGM's slot:
    // setting RTSS EnableOSD=0 would also disable other applications' OSD feeders.
    private const int MaximumOverlayLevel = 4;
    private readonly RtssDiscovery _discovery;
    private readonly RtssOsdRenderer _osd;
    private RtssProfileApi? _api;
    private bool _disposed;
    private long _generation;
    private RtssProbe? _lastProbe;

    /// <summary>Creates the adapter and starts its owned OSD worker.</summary>
    /// <param name="discovery">Read-only discovery owner, or null for Windows discovery.</param>
    internal RtssNativeAdapter(RtssDiscovery? discovery = null)
    {
        _discovery = discovery ?? new RtssDiscovery();
        _osd = new RtssOsdRenderer();
        _osd.Start();
    }

    /// <inheritdoc />
    public void ClearOverlay()
    {
        _osd.SetLevel(0);
    }

    /// <inheritdoc />
    public void ApplyOsdCustomization(RtssOsdCustomSettings settings)
    {
        _osd.ApplyCustom(settings);
    }

    /// <inheritdoc />
    public RtssOsdMetrics SampleSensors()
    {
        return _osd.SampleSensors();
    }

    /// <inheritdoc />
    public void ApplyOsdPowerStatus(RtssOsdPowerStatus status)
    {
        _osd.ApplyPowerStatus(status);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Answered from the installation's <c>Profiles</c> directory rather than the profile API,
    ///     because the API's LoadProfile cannot distinguish "absent" from "present with defaults" —
    ///     and SaveProfile on an absent name is precisely the creation this check exists to avoid.
    /// </remarks>
    public bool ProfileExists(string rtssProfileName)
    {
        if (rtssProfileName.Length == 0)
        {
            return true;
        }

        var directory = _lastProbe?.ExecutablePath is { } executable
            ? Path.GetDirectoryName(executable)
            : null;
        return directory is not null
               && File.Exists(Path.Combine(directory, "Profiles", rtssProfileName + ".cfg"));
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Discovery runs on a worker because registry, file-version, and process inspection
    ///     can block even when the caller acquired its serialization gate synchronously.
    /// </remarks>
    public Task<RtssProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.Run(() => ProbeCore(cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<RtssReadback> ReadAsync(
        string rtssProfileName,
        long generation,
        CancellationToken cancellationToken)
    {
        RequireReady(generation, cancellationToken);
        return await Task.Run(
            () => ReadCore(rtssProfileName),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RtssApplyResult> ApplyAsync(
        RtssApplyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
        switch (request.Control)
        {
            case PerformanceControl.FrameLimit when request.Value is < 0 or > 1000:
                return new RtssApplyResult(false, "The frame-limit value is outside the verified RTSS range.");
            case PerformanceControl.OverlayLevel when request.Value is < 0 or > MaximumOverlayLevel:
                return new RtssApplyResult(false, "The overlay level is outside the supported range.");
        }

        RequireReady(request.Generation, cancellationToken);
        return await Task.Run(() => ApplyCore(request), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await _osd.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            ReleaseApi();
        }
    }

    private RtssProbe ProbeCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var probe = _discovery.Probe();
        if (probe.Availability != RtssAvailability.AdapterUnavailable
            || probe.ExecutablePath is null)
        {
            ReleaseApi();
            _lastProbe = probe;
            return probe;
        }

        try
        {
            EnsureApi(probe);
            var ready = probe with
            {
                Availability = RtssAvailability.Ready,
                Capabilities = new RtssCapabilities(
                    0,
                    1000,
                    new HashSet<int> { 0, 1, 2, 3, MaximumOverlayLevel }),
                Diagnostic = "RTSS profile API is ready."
            };
            _lastProbe = ready;
            return ready;
        }
        catch (Exception ex)
        {
            ReleaseApi();
            var degraded = probe with
            {
                Availability = RtssAvailability.Degraded,
                Diagnostic = $"RTSS profile API load failed: {ex.Message}"
            };
            _lastProbe = degraded;
            return degraded;
        }
    }

    private RtssReadback ReadCore(string rtssProfileName)
    {
        var api = _api
                  ?? throw new InvalidOperationException("RTSS profile API is not loaded.");
        api.LoadProfile(rtssProfileName);
        if (!api.TryGetUInt32(FrameLimitProperty, out var frameLimit)
            || frameLimit > int.MaxValue)
        {
            throw new InvalidDataException("RTSS did not return a valid frame-limit value.");
        }

        // The selector is WSGM-owned, but RTSS can close its presentation gates independently.
        // Reporting the cached selector alone hid that drift, including after an RTSS restart.
        var presentation = ReadOverlayPresentation(_osd.Level, rtssProfileName, profile =>
        {
            api.LoadProfile(profile);
            return api.TryGetUInt32(OverlayEnabledProperty, out var enabled) ? enabled : null;
        });

        return new RtssReadback(
            new PerformanceValues((int)frameLimit, _osd.Level),
            DateTimeOffset.UtcNow, presentation);
    }

    private RtssApplyResult ApplyCore(RtssApplyRequest request)
    {
        var api = _api
                  ?? throw new InvalidOperationException("RTSS profile API is not loaded.");
        if (request.Control is PerformanceControl.OverlayLevel)
        {
            var activationProfiles = OverlayActivationProfiles(
                request.Value,
                request.RtssProfileName);
            if (activationProfiles.Count > 0
                && !TryEnableOverlayPresentation(api, activationProfiles, out var refusal))
            {
                return new RtssApplyResult(false, refusal);
            }

            _osd.SetLevel(request.Value);
            return new RtssApplyResult(true, null);
        }

        api.LoadProfile(request.RtssProfileName);
        var (property, propertyValue) = request.Control switch
        {
            PerformanceControl.FrameLimit =>
                (FrameLimitProperty, checked((uint)request.Value)),
            _ => (string.Empty, 0u)
        };
        if (property.Length == 0 || !api.TrySetUInt32(property, propertyValue))
        {
            return new RtssApplyResult(false, "RTSS rejected the performance-profile value.");
        }

        api.SaveProfile(request.RtssProfileName);
        api.UpdateProfiles();
        return new RtssApplyResult(true, null);
    }

    private static bool TryEnableOverlayPresentation(
        RtssProfileApi api,
        IReadOnlyList<string> profiles,
        out string? refusal)
    {
        var changed = false;
        List<string> changedProfiles = [];
        foreach (var profile in profiles)
        {
            api.LoadProfile(profile);
            if (api.TryGetUInt32(OverlayEnabledProperty, out var enabled) && enabled == 1)
            {
                continue;
            }

            if (!api.TrySetUInt32(OverlayEnabledProperty, 1))
            {
                refusal = profile.Length == 0
                    ? "RTSS rejected enabling OSD presentation in the global profile."
                    : $"RTSS rejected enabling OSD presentation for '{profile}'.";
                return false;
            }

            api.SaveProfile(profile);
            changed = true;
            changedProfiles.Add(profile);
        }

        if (changed)
        {
            api.UpdateProfiles();
        }

        var requested = ProfileLabels(profiles);
        Log.Change(
            "rtss.overlay-presentation",
            changedProfiles.Count == 0
                ? $"RTSS OSD presentation already enabled: profiles={requested}."
                : $"RTSS OSD presentation enabled: profiles={requested}; "
                  + $"changed={ProfileLabels(changedProfiles)}.");

        refusal = null;
        return true;
    }

    private static string ProfileLabel(string profile)
    {
        return profile.Length == 0
            ? "<global>"
            : profile;
    }

    private static string ProfileLabels(IReadOnlyList<string> profiles)
    {
        var labels = new string[profiles.Count];
        for (var index = 0; index < profiles.Count; index++)
        {
            labels[index] = ProfileLabel(profiles[index]);
        }

        return string.Join(", ", labels);
    }

    /// <summary>Profiles whose RTSS presentation gate a nonzero WSGM overlay must open.</summary>
    /// <param name="overlayLevel">WSGM overlay selector; zero or below requires no presentation change.</param>
    /// <param name="requestedProfile">Active executable profile name, or empty for global only.</param>
    /// <returns>No profiles when off; otherwise the requested profile, if any, followed by global.</returns>
    /// <remarks>
    ///     Global covers applications without an explicit profile; the current executable is added
    ///     because an explicit per-app <c>EnableOSD=0</c> overrides global state. The service re-applies on
    ///     every application transition, so each profile is repaired when it becomes the active target.
    /// </remarks>
    internal static IReadOnlyList<string> OverlayActivationProfiles(
        int overlayLevel,
        string requestedProfile)
    {
        return overlayLevel <= 0
            ? []
            : string.IsNullOrEmpty(requestedProfile)
                ? [string.Empty]
                : [requestedProfile, string.Empty];
    }

    /// <summary>Combines the live selector with RTSS's global and application presentation gates.</summary>
    /// <param name="level">WSGM's current selector level.</param>
    /// <param name="profile">Active executable profile, or empty for global.</param>
    /// <param name="readPresentation">Reads EnableOSD for one profile; null means no readback.</param>
    /// <returns>False for a closed gate, true for open gates, or null when unknown or switched off.</returns>
    internal static bool? ReadOverlayPresentation(int level, string profile, Func<string, uint?> readPresentation)
    {
        bool? enabled = level > 0 ? true : null;
        foreach (var name in OverlayActivationProfiles(level, profile))
        {
            var value = readPresentation(name);
            if (value == 0)
            {
                return false;
            }

            if (value != 1)
            {
                enabled = null;
            }
        }

        return enabled;
    }

    // The service probes inside its adapter gate immediately before every read or write, so the
    // cached probe already names the process generation the caller saw; probing again here doubled
    // the registry, file and process work of every poll and command.
    private void RequireReady(
        long generation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_lastProbe is not { Availability: RtssAvailability.Ready } probe
            || probe.Generation != generation)
        {
            throw new InvalidOperationException(
                "RTSS availability or process generation changed before the operation.");
        }
    }

    private void EnsureApi(RtssProbe probe)
    {
        if (_api is not null && _generation == probe.Generation)
        {
            return;
        }

        ReleaseApi();
        var executable = probe.ExecutablePath
                         ?? throw new InvalidDataException("RTSS discovery returned no executable path.");
        var directory = Path.GetDirectoryName(executable)
                        ?? throw new InvalidDataException("RTSS executable has no installation directory.");
        var library = Path.Combine(directory, RtssDiscovery.ProfileApiName);
        _api = new RtssProfileApi(library);
        _generation = probe.Generation;
    }

    private void ReleaseApi()
    {
        _api?.Dispose();
        _api = null;
        _generation = 0;
        _lastProbe = null;
    }
}
