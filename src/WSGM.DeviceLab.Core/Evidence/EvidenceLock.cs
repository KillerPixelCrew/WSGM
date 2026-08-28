using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace WSGM.DeviceLab.Core.Evidence;

/// <summary>
/// The pinned set of claims and module versions a generated project was built from.
/// </summary>
/// <remarks>
/// The lock exists so a protocol constant cannot change quietly. Generated code cites claim IDs; the
/// lock pins what those IDs meant at generation time. Regenerating after a firmware resweep produces
/// a different lock, and the diff is the review — without it, a changed offset would arrive as an
/// unremarkable line in a large generated file.
/// </remarks>
public sealed record EvidenceLock
{
    /// <summary>Schema version of this lock.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Device definition the lock belongs to.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Generator that produced the project, and its version.</summary>
    public required string GeneratorVersion { get; init; }

    /// <summary>Pinned claims, in canonical order.</summary>
    public IReadOnlyList<PinnedClaim> Claims { get; init; } = [];

    /// <summary>Pinned module versions, in canonical order.</summary>
    public IReadOnlyList<PinnedModule> Modules { get; init; } = [];
}

/// <summary>One claim as pinned at generation time.</summary>
/// <param name="ClaimId">The claim's stable identifier.</param>
/// <param name="State">Its state when the project was generated.</param>
/// <param name="ContentHash">Hash of the claim's semantically significant fields.</param>
public sealed record PinnedClaim(string ClaimId, ClaimState State, string ContentHash);

/// <summary>One module as pinned at generation time.</summary>
/// <param name="ModuleId">The module identifier.</param>
/// <param name="Version">The pinned version.</param>
public sealed record PinnedModule(string ModuleId, int Version);

/// <summary>
/// Produces deterministic evidence locks and the semantic diffs that gate their changes.
/// </summary>
public static class EvidenceLockBuilder
{
    /// <summary>Schema version this builder emits.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Builds a lock from the claims and modules a generated project used.
    /// </summary>
    /// <param name="deviceId">Device definition the lock belongs to.</param>
    /// <param name="generatorVersion">Generator identity and version.</param>
    /// <param name="claims">Claims the project cites.</param>
    /// <param name="modules">Modules the project composes.</param>
    /// <returns>A lock whose contents are ordered canonically.</returns>
    /// <remarks>
    /// Entries are sorted by identifier rather than left in input order. Two runs over the same
    /// evidence then produce identical locks, so a diff shows what actually changed rather than how
    /// the inputs happened to be enumerated.
    /// </remarks>
    public static EvidenceLock Build(
        string deviceId,
        string generatorVersion,
        IReadOnlyList<EvidenceClaim> claims,
        IReadOnlyList<(string ModuleId, int Version)> modules)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(generatorVersion);
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(modules);

        return new EvidenceLock
        {
            SchemaVersion = CurrentSchemaVersion,
            DeviceId = deviceId,
            GeneratorVersion = generatorVersion,
            Claims = [.. claims
                .Select(c => new PinnedClaim(c.ClaimId, c.State, HashClaim(c)))
                .OrderBy(c => c.ClaimId, StringComparer.Ordinal)],
            Modules = [.. modules
                .Select(m => new PinnedModule(m.ModuleId, m.Version))
                .OrderBy(m => m.ModuleId, StringComparer.Ordinal)],
        };
    }

    /// <summary>
    /// Hashes the fields of a claim that change what generated code does.
    /// </summary>
    /// <param name="claim">The claim to hash.</param>
    /// <returns>A lowercase hexadecimal SHA-256 digest.</returns>
    /// <remarks>
    /// Deliberately covers only the semantically significant fields: transport, selector, offset,
    /// mask, width, endianness, scale, unit, and range. Editing a claim's prose, adding a supporting
    /// observation, or recording a new limitation must not invalidate a lock, because none of those
    /// change a single byte the generated code writes. Changing an offset must.
    /// </remarks>
    public static string HashClaim(EvidenceClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);

        StringBuilder builder = new();
        Append(builder, claim.Transport);
        Append(builder, claim.Endpoint);
        Append(builder, claim.Selector);
        Append(builder, claim.Offset?.ToString(CultureInfo.InvariantCulture));
        Append(builder, claim.Mask?.ToString(CultureInfo.InvariantCulture));
        Append(builder, claim.WidthBits?.ToString(CultureInfo.InvariantCulture));
        Append(builder, claim.Endian.ToString());
        Append(builder, claim.Scale?.ToString("R", CultureInfo.InvariantCulture));
        Append(builder, claim.Unit);
        Append(builder, claim.RangeMinimum?.ToString("R", CultureInfo.InvariantCulture));
        Append(builder, claim.RangeMaximum?.ToString("R", CultureInfo.InvariantCulture));
        Append(builder, claim.Scope.BaseboardProduct);
        Append(builder, claim.Scope.EcFirmwareVersion);
        Append(builder, claim.Scope.ControllerFirmware);

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>
    /// Compares two locks and reports what a reviewer must look at.
    /// </summary>
    /// <param name="previous">The lock the project was generated from.</param>
    /// <param name="current">The lock the current evidence produces.</param>
    /// <returns>Every semantic difference, empty when nothing that matters changed.</returns>
    public static IReadOnlyList<EvidenceLockChange> Diff(EvidenceLock previous, EvidenceLock current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        List<EvidenceLockChange> changes = [];

        Dictionary<string, PinnedClaim> before = previous.Claims.ToDictionary(
            c => c.ClaimId, StringComparer.Ordinal);
        Dictionary<string, PinnedClaim> after = current.Claims.ToDictionary(
            c => c.ClaimId, StringComparer.Ordinal);

        foreach ((string id, PinnedClaim claim) in after.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!before.TryGetValue(id, out PinnedClaim? old))
            {
                changes.Add(new EvidenceLockChange(id, EvidenceChangeKind.ClaimAdded,
                    $"Claim '{id}' is new at state {claim.State}."));
                continue;
            }

            if (!string.Equals(old.ContentHash, claim.ContentHash, StringComparison.Ordinal))
            {
                // The dangerous change: a constant moved under a claim ID that generated code already
                // cites. Nothing about the generated file's shape would reveal it.
                changes.Add(new EvidenceLockChange(id, EvidenceChangeKind.ConstantChanged,
                    $"Claim '{id}' changed a protocol constant. Regeneration requires review."));
            }

            if (old.State != claim.State)
            {
                bool weakened = claim.State < old.State || claim.State is ClaimState.Rejected;
                changes.Add(new EvidenceLockChange(id,
                    weakened ? EvidenceChangeKind.ClaimWeakened : EvidenceChangeKind.ClaimStrengthened,
                    $"Claim '{id}' moved from {old.State} to {claim.State}."));
            }
        }

        foreach (string id in before.Keys.Except(after.Keys, StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal))
        {
            changes.Add(new EvidenceLockChange(id, EvidenceChangeKind.ClaimRemoved,
                $"Claim '{id}' is gone. Code citing it must be regenerated or removed."));
        }

        Dictionary<string, int> modulesBefore = previous.Modules.ToDictionary(
            m => m.ModuleId, m => m.Version, StringComparer.Ordinal);

        foreach (PinnedModule module in current.Modules)
        {
            if (modulesBefore.TryGetValue(module.ModuleId, out int oldVersion)
                && oldVersion != module.Version)
            {
                changes.Add(new EvidenceLockChange(module.ModuleId, EvidenceChangeKind.ModuleVersionChanged,
                    $"Module '{module.ModuleId}' moved from version {oldVersion} to {module.Version}."));
            }
        }

        return changes;
    }

    /// <summary>
    /// Whether a set of changes may be accepted without a human reviewing them.
    /// </summary>
    /// <param name="changes">Changes from <see cref="Diff"/>.</param>
    /// <returns><see langword="true"/> only when nothing that alters behaviour changed.</returns>
    /// <remarks>
    /// Strengthening a claim is the only change that passes unattended, because it cannot make
    /// generated code do something new — it can only make an existing capability eligible for more,
    /// and that eligibility is granted elsewhere with its own gate.
    /// </remarks>
    public static bool MayAcceptWithoutReview(IReadOnlyList<EvidenceLockChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return changes.All(c => c.Kind is EvidenceChangeKind.ClaimStrengthened);
    }

    private static void Append(StringBuilder builder, string? value) =>
        builder.Append(value ?? string.Empty).Append('');
}

/// <summary>One difference between two evidence locks.</summary>
/// <param name="Id">The claim or module identifier.</param>
/// <param name="Kind">What kind of change it is.</param>
/// <param name="Description">What a reviewer needs to know.</param>
public sealed record EvidenceLockChange(string Id, EvidenceChangeKind Kind, string Description);

/// <summary>What kind of evidence change occurred.</summary>
public enum EvidenceChangeKind
{
    /// <summary>A claim appeared.</summary>
    ClaimAdded,

    /// <summary>A claim disappeared.</summary>
    ClaimRemoved,

    /// <summary>A claim's protocol constant changed under the same identifier.</summary>
    ConstantChanged,

    /// <summary>A claim moved to a stronger state.</summary>
    ClaimStrengthened,

    /// <summary>A claim moved to a weaker state, or was rejected.</summary>
    ClaimWeakened,

    /// <summary>A composed module's pinned version changed.</summary>
    ModuleVersionChanged,
}
