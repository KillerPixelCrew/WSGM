using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using WSGM.Device.Sdk.Glyphs;

namespace WSGM.Core;

/// <summary>Reason a physical-device glyph surface must retain or return to native Steam artwork.</summary>
internal enum PhysicalGlyphFallbackReason
{
    /// <summary>A matching reviewed physical profile was selected.</summary>
    None,
    /// <summary>Device integration is disabled, so no device artwork is eligible.</summary>
    DeviceIntegrationDisabled,
    /// <summary>The user explicitly selected Steam's native glyphs.</summary>
    NativeSteamSelected,
    /// <summary>No profile matches the active exact device definition.</summary>
    ExactDeviceMismatch,
    /// <summary>The input source does not identify the integrated handheld controls.</summary>
    SourceNotHandheld,
    /// <summary>The selected profile does not declare this physical control.</summary>
    ControlAbsent,
    /// <summary>Declared artwork could not be found.</summary>
    ArtworkMissing,
    /// <summary>The consuming surface rejected the supplied artwork.</summary>
    RenderRejected
}

/// <summary>Selected physical profile and fallback diagnostics for all glyph consumers.</summary>
/// <param name="Profile">Shared immutable imported profile, or null to use native Steam presentation.</param>
/// <param name="FallbackReason">Reason no physical profile was selected, or None on success.</param>
/// <param name="FellBackFromMissingManualProfile">Whether a missing or incompatible manual selection required automatic fallback.</param>
internal sealed record PhysicalGlyphSelectionResult(
    ImportedGlyphProfile? Profile,
    PhysicalGlyphFallbackReason FallbackReason,
    bool FellBackFromMissingManualProfile);

/// <summary>Owns immutable package profiles and applies the closed physical-glyph selection policy.</summary>
internal sealed class PhysicalGlyphCatalog : IDisposable
{
    private readonly Lock _gate = new();

    private string? _activeDeviceId;
    private bool _disposed;
    private Dictionary<string, ImportedGlyphProfile> _profiles = new(StringComparer.Ordinal);

    /// <summary>Clears profile references and event subscribers after consumers have stopped using the catalog.</summary>
    /// <remarks>Repeated disposal is harmless; the owner must serialize teardown with updates and selection.</remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            _profiles.Clear();
        }

        Changed = null;
    }

    /// <summary>Raised synchronously on the updating thread after device/profile state changes; subscribers must marshal UI work.</summary>
    internal event Action? Changed;

    /// <summary>Records which device definition the active plugin matched.</summary>
    /// <param name="deviceDefinitionId">The matched definition, or null when none is active.</param>
    /// <remarks>
    ///     Held here rather than by the caller because selection depends on it exactly as it depends on
    ///     the profiles, and both can arrive in either order: the definition comes from a lifecycle
    ///     notification and the profiles from the installed package. Whichever lands second has to
    ///     re-raise <see cref="Changed" />, or every surface keeps the answer computed before the pair
    ///     was complete.
    /// </remarks>
    internal void SetActiveDevice(string? deviceDefinitionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (string.Equals(_activeDeviceId, deviceDefinitionId, StringComparison.Ordinal))
            {
                return;
            }

            _activeDeviceId = deviceDefinitionId;
        }

        Changed?.Invoke();
    }

    /// <summary>Atomically replaces the profile lookup after validating unique ordinal profile identities.</summary>
    /// <param name="profiles">Imported immutable profiles to retain by reference; the enumeration is copied before publication.</param>
    /// <exception cref="ArgumentNullException">The enumeration is null.</exception>
    /// <exception cref="ArgumentException">Two profiles have the same identity.</exception>
    /// <exception cref="ObjectDisposedException">The catalog has been disposed.</exception>
    internal void ReplacePackageProfiles(IEnumerable<ImportedGlyphProfile> profiles)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(profiles);
        var snapshot = profiles
            .OrderBy(profile => profile.Manifest.ProfileId, StringComparer.Ordinal)
            .ToArray();
        Dictionary<string, ImportedGlyphProfile> replacement = new(StringComparer.Ordinal);
        foreach (var profile in snapshot)
        {
            if (!replacement.TryAdd(profile.Manifest.ProfileId, profile))
            {
                throw new ArgumentException(
                    $"Profile '{profile.Manifest.ProfileId}' appears more than once.",
                    nameof(profiles));
            }
        }

        lock (_gate)
        {
            _profiles = replacement;
        }

        Changed?.Invoke();
    }

    /// <summary>Selects a reviewed exact-device profile or an explicit native-Steam fallback.</summary>
    /// <param name="deviceIntegrationEnabled">Whether physical device integration is active.</param>
    /// <param name="selectionMode">Native, automatic, or manually reviewed profile preference.</param>
    /// <param name="manualProfileId">Requested manual identity, or null; unavailable choices fall back to automatic selection.</param>
    /// <returns>The selected shared immutable profile and fallback diagnostics; automatic ties use ordinal profile ID order.</returns>
    /// <exception cref="ObjectDisposedException">The catalog has been disposed.</exception>
    internal PhysicalGlyphSelectionResult SelectProfile(
        bool deviceIntegrationEnabled,
        DeviceGlyphSelection selectionMode,
        string? manualProfileId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            var activeDeviceId = _activeDeviceId;

            // Every glyph surface funnels through here, so log the decisive inputs once whenever
            // the selection changes and make every fallback remotely diagnosable.
            Log.Change(
                "glyph.selection",
                $"Glyph selection: integration={deviceIntegrationEnabled}, mode={selectionMode}, "
                + $"device={activeDeviceId ?? "<none>"}, profiles={_profiles.Count}, "
                + $"manual={manualProfileId ?? "<none>"}");
            if (!deviceIntegrationEnabled)
            {
                return Fallback(PhysicalGlyphFallbackReason.DeviceIntegrationDisabled);
            }

            if (selectionMode is DeviceGlyphSelection.NativeSteam)
            {
                return Fallback(PhysicalGlyphFallbackReason.NativeSteamSelected);
            }

            var missingManual = false;
            if (selectionMode is DeviceGlyphSelection.ManualReviewedProfile)
            {
                if (manualProfileId is { Length: > 0 }
                    && activeDeviceId is { Length: > 0 }
                    && _profiles.TryGetValue(manualProfileId, out var manual)
                    && manual.Manifest.ExactDeviceIds.Contains(activeDeviceId, StringComparer.Ordinal))
                {
                    return new PhysicalGlyphSelectionResult(
                        manual,
                        PhysicalGlyphFallbackReason.None,
                        false);
                }

                // A missing manual profile falls back to Automatic and reports the missing
                // selection; it never guesses another manual profile.
                missingManual = true;
            }

            if (activeDeviceId is not { Length: > 0 })
            {
                return new PhysicalGlyphSelectionResult(
                    null,
                    PhysicalGlyphFallbackReason.ExactDeviceMismatch,
                    missingManual);
            }

            // Automatic selection is the package's own profile for the matched device. Naming the
            // device is the whole discriminator; a package wanting a different profile for the same
            // device uses the manual selection above.
            var automatic = _profiles.Values
                .Where(profile =>
                    profile.Manifest.ExactDeviceIds.Contains(activeDeviceId, StringComparer.Ordinal))
                .OrderBy(profile => profile.Manifest.ProfileId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (automatic is null)
            {
                return new PhysicalGlyphSelectionResult(
                    null,
                    PhysicalGlyphFallbackReason.ExactDeviceMismatch,
                    missingManual);
            }

            return new PhysicalGlyphSelectionResult(
                automatic,
                PhysicalGlyphFallbackReason.None,
                missingManual);
        }
    }

    private static PhysicalGlyphSelectionResult Fallback(PhysicalGlyphFallbackReason reason)
    {
        return new PhysicalGlyphSelectionResult(null, reason, false);
    }
}
