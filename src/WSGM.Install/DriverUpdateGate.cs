using System;
using System.IO;

namespace WSGM.Install;

/// <summary>Where the driver-update gate stands for this boot.</summary>
public enum DriverUpdateGateState
{
    /// <summary>No update is staged; sign-in proceeds normally.</summary>
    None,

    /// <summary>Setup staged an update and is waiting for a boot that WSGM stays out of.</summary>
    Pending,

    /// <summary>Sign-in honoured the gate this boot, so the clean boot setup asked for is here now.</summary>
    Consumed
}

/// <summary>
///     A one-boot handshake that keeps WSGM away from a staged controller-driver update.
/// </summary>
/// <remarks>
///     usbip-win2 cannot be replaced on a boot where a device has already been attached to it: the
///     installer restarts the USB hubs, the driver's own teardown blocks behind the attachment, and
///     the upgrade hangs with no way out but a hard reset. That is upstream's
///     <see href="https://github.com/vadimgrn/usbip-win2/pull/188">#188</see>, and it bit the
///     reference Claw twice on 2026-09-27 because WSGM attaches its virtual pad seconds after
///     sign-in, long before anyone can start setup.
///     <para>
///         So the update takes two runs. The first stages the gate and asks for a restart; the
///         sign-in service sees <see cref="DriverUpdateGateState.Pending" /> on the next boot, leaves
///         the desktop alone and marks it <see cref="DriverUpdateGateState.Consumed" />; the second
///         run finds that mark, knows nothing has attached, installs, and clears the gate.
///     </para>
///     <para>
///         The consumed state is what makes an abandoned update self-healing. A user who never comes
///         back gets WSGM again on the boot after next, because sign-in clears a gate it has already
///         honoured rather than honouring it twice. The cost of the whole mechanism is at most one
///         sign-in without WSGM.
///     </para>
/// </remarks>
public static class DriverUpdateGate
{
    private const string PendingMarker = "pending";

    private const string ConsumedMarker = "consumed";

    /// <summary>The gate file, beside the machine-wide setup log both writers already own.</summary>
    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WSGM",
        "driver-update.gate");

    /// <summary>Reads the gate, treating anything unreadable or unrecognised as absent.</summary>
    /// <returns>The current state.</returns>
    public static DriverUpdateGateState Read()
    {
        return Read(Path);
    }

    /// <summary>Stages an update, so the next sign-in leaves the desktop alone.</summary>
    /// <returns><see langword="true" /> when the gate was written.</returns>
    public static bool Stage()
    {
        return Stage(Path);
    }

    /// <summary>Marks a staged gate as honoured. Call once per boot, before deciding anything.</summary>
    /// <returns><see langword="true" /> when this call was the one that honoured it.</returns>
    public static bool Consume()
    {
        return Consume(Path);
    }

    /// <summary>Removes the gate, whatever state it is in.</summary>
    public static void Clear()
    {
        Clear(Path);
    }

    /// <summary>Reads a gate at an explicit path.</summary>
    /// <param name="path">The gate file.</param>
    /// <returns>The current state.</returns>
    public static DriverUpdateGateState Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return DriverUpdateGateState.None;
            }

            return File.ReadAllText(path).Trim() switch
            {
                PendingMarker => DriverUpdateGateState.Pending,
                ConsumedMarker => DriverUpdateGateState.Consumed,
                _ => DriverUpdateGateState.None
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A gate that cannot be read is not a reason to hold a sign-in back.
            return DriverUpdateGateState.None;
        }
    }

    /// <summary>Stages an update at an explicit path.</summary>
    /// <param name="path">The gate file.</param>
    /// <returns><see langword="true" /> when the gate was written.</returns>
    public static bool Stage(string path)
    {
        return Write(path, PendingMarker);
    }

    /// <summary>Honours a staged gate at an explicit path.</summary>
    /// <param name="path">The gate file.</param>
    /// <returns><see langword="true" /> when this call was the one that honoured it.</returns>
    public static bool Consume(string path)
    {
        return Read(path) is DriverUpdateGateState.Pending && Write(path, ConsumedMarker);
    }

    /// <summary>Removes a gate at an explicit path.</summary>
    /// <param name="path">The gate file.</param>
    public static void Clear(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Nothing here is worth failing an install or a sign-in over: a gate that survives is
            // cleared by the next sign-in that finds it already consumed.
        }
    }

    private static bool Write(string path, string marker)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, marker);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
