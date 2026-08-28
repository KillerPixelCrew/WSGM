using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Device.Contracts.Glyphs;

namespace WSGM.Core;

internal enum PhysicalGlyphSelectionMode
{
    Automatic,
    NativeSteam,
    ManualReviewed,
}

internal enum PhysicalGlyphFallbackReason
{
    None,
    DeviceIntegrationDisabled,
    NativeSteamSelected,
    ProfileMissing,
    ProfileUnverified,
    ExactDeviceMismatch,
    SourceNotHandheld,
    ControlAbsent,
    ArtworkMissing,
    RenderRejected,
}

internal sealed record PhysicalGlyphSelectionResult(
    ImportedGlyphProfile? Profile,
    PhysicalGlyphFallbackReason FallbackReason,
    bool FellBackFromMissingManualProfile);

/// <summary>Owns immutable package profiles and applies the closed physical-glyph selection policy.</summary>
internal sealed class PhysicalGlyphCatalog : IDisposable
{
    private readonly object _gate = new();
    private Dictionary<string, ImportedGlyphProfile> _profiles = new(StringComparer.Ordinal);
    private bool _disposed;

    internal event Action? Changed;

    internal void ReplacePackageProfiles(IEnumerable<ImportedGlyphProfile> profiles)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(profiles);
        ImportedGlyphProfile[] snapshot = profiles
            .OrderBy(profile => profile.Manifest.ProfileId, StringComparer.Ordinal)
            .ToArray();
        Dictionary<string, ImportedGlyphProfile> replacement = new(StringComparer.Ordinal);
        foreach (ImportedGlyphProfile profile in snapshot)
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

    internal PhysicalGlyphSelectionResult SelectProfile(
        bool deviceIntegrationEnabled,
        PhysicalGlyphSelectionMode selectionMode,
        string? activeDeviceId,
        string? advertisedProfileId,
        string? manualProfileId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (!deviceIntegrationEnabled)
            {
                return Fallback(PhysicalGlyphFallbackReason.DeviceIntegrationDisabled);
            }
            if (selectionMode is PhysicalGlyphSelectionMode.NativeSteam)
            {
                return Fallback(PhysicalGlyphFallbackReason.NativeSteamSelected);
            }

            bool missingManual = false;
            if (selectionMode is PhysicalGlyphSelectionMode.ManualReviewed)
            {
                if (manualProfileId is { Length: > 0 }
                    && _profiles.TryGetValue(manualProfileId, out ImportedGlyphProfile? manual)
                    && manual.Manifest.Verification is not GlyphProfileVerification.Unverified)
                {
                    return new PhysicalGlyphSelectionResult(
                        manual,
                        PhysicalGlyphFallbackReason.None,
                        false);
                }

                // P0-017: a missing manual profile falls back to Automatic and reports the
                // missing selection; it never guesses another manual profile.
                missingManual = true;
            }

            if (advertisedProfileId is not { Length: > 0 }
                || !_profiles.TryGetValue(advertisedProfileId, out ImportedGlyphProfile? automatic))
            {
                return new PhysicalGlyphSelectionResult(
                    null,
                    PhysicalGlyphFallbackReason.ProfileMissing,
                    missingManual);
            }
            if (automatic.Manifest.Verification is not GlyphProfileVerification.ExactDeviceVerified)
            {
                return new PhysicalGlyphSelectionResult(
                    null,
                    PhysicalGlyphFallbackReason.ProfileUnverified,
                    missingManual);
            }
            if (activeDeviceId is not { Length: > 0 }
                || !automatic.Manifest.ExactDeviceIds.Contains(activeDeviceId, StringComparer.Ordinal))
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

    private static PhysicalGlyphSelectionResult Fallback(PhysicalGlyphFallbackReason reason) =>
        new(null, reason, false);
}
