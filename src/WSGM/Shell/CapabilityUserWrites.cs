using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>What a capability value the user set does, by the descriptor's profile scope.</summary>
/// <param name="Command">Whether the value is written to the publisher now.</param>
/// <param name="Layer">The profile layer it is saved to.</param>
internal readonly record struct CapabilityUserWrite(bool Command, ProfileLayer Layer);

/// <summary>
///     Applies <see cref="CapabilityProfileScope" /> to a value the user set, for the device package and
///     every graphics package alike.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>
///             Switched: written now and saved to the layer in force, the running game's profile while it
///             is on and Global otherwise.
///         </item>
///         <item>Global only: written now and always saved to Global, never offered per game.</item>
///         <item>
///             Native per-application: while a game's profile is on, saved to that profile and not written,
///             because the driver applies it from its own per-application profile. Otherwise written and saved
///             to Global.
///         </item>
///     </list>
/// </remarks>
internal static class CapabilityUserWrites
{
    /// <summary>Decides what a user write does.</summary>
    /// <param name="scope">The descriptor's profile scope.</param>
    /// <param name="gameInForce">Whether an edit made now lands in the running game's profile.</param>
    /// <returns>Whether to command and which layer to save to.</returns>
    internal static CapabilityUserWrite Decide(CapabilityProfileScope scope, bool gameInForce)
    {
        return scope switch
        {
            CapabilityProfileScope.GlobalOnly => new CapabilityUserWrite(true, ProfileLayer.Global),
            CapabilityProfileScope.NativePerApplication when gameInForce =>
                new CapabilityUserWrite(false, ProfileLayer.Active),
            CapabilityProfileScope.NativePerApplication => new CapabilityUserWrite(true, ProfileLayer.Global),
            _ => new CapabilityUserWrite(true, ProfileLayer.Active)
        };
    }

    /// <summary>Saves a value the publisher already applied, to the layer its scope names.</summary>
    /// <param name="profiles">The profile owner.</param>
    /// <param name="identityKey">The device identity or the graphics publisher's key.</param>
    /// <param name="view">The capability as it was before the write.</param>
    /// <param name="value">The value the user set.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>Whether anything was saved.</returns>
    /// <remarks>A value that already matches the target layer's resolved value writes no configuration.</remarks>
    internal static async Task<bool> PersistAsync(
        ProfileService profiles,
        string identityKey,
        DeviceCapabilityView view,
        CapabilityValue value,
        CancellationToken cancellationToken)
    {
        var write = Decide(view.Descriptor.ProfileScope, profiles.Current.EditsGame);
        var current = write.Layer is ProfileLayer.Global
            ? view.Projection.GlobalDesiredValue
            : view.Projection.DesiredValue;
        if (current is not null && DeviceCoordinator.SameValue(current, value))
        {
            return false;
        }

        await profiles.SetDeviceAsync(identityKey, view.Descriptor.CapabilityId, view.Descriptor.InstanceId, value,
            write.Layer, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    ///     Saves a native per-application value to the running game's profile without writing it, and
    ///     reports the command as accepted.
    /// </summary>
    /// <param name="profiles">The profile owner.</param>
    /// <param name="identityKey">The device identity or the graphics publisher's key.</param>
    /// <param name="view">The capability.</param>
    /// <param name="value">The value the user set.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>
    ///     <see cref="CommandOutcome.Accepted" /> once saved: nothing reached the driver's live state, and
    ///     the game's per-application profile carries it from the next sync. A refusal when the value does
    ///     not fit the descriptor.
    /// </returns>
    internal static async Task<CapabilityCommandResult> StoreForApplicationAsync(
        ProfileService profiles,
        string identityKey,
        DeviceCapabilityView view,
        CapabilityValue value,
        CancellationToken cancellationToken)
    {
        string? error = null;
        if (!view.Descriptor.SupportsWrite
            || !DeviceCapabilityValidation.ValueMatches(value, view.Descriptor, out error))
        {
            return new CapabilityCommandResult
            {
                CommandId = Guid.NewGuid(),
                Outcome = CommandOutcome.Rejected,
                Reason = view.Descriptor.SupportsWrite
                    ? new CapabilityReason(CapabilityReasonCode.ValueOutOfRange,
                        error ?? "Capability value violates its descriptor.")
                    : new CapabilityReason(CapabilityReasonCode.Unsupported, "Capability is read-only."),
                CompletedAt = DateTimeOffset.UtcNow
            };
        }

        await PersistAsync(profiles, identityKey, view, value, cancellationToken).ConfigureAwait(false);
        return new CapabilityCommandResult
        {
            CommandId = Guid.NewGuid(),
            Outcome = CommandOutcome.Accepted,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }
}
