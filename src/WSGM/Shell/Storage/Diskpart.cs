using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>
///     The disk-level operations the SD-card format flow runs: the three diskpart scripts, the elevated
///     diskpart run, the volume and drive-letter waits that follow it, the volume reads that pin later
///     writes to the formatted volume, and the free-space retrim. Stateless; every member runs on the
///     caller's worker thread.
/// </summary>
internal static class Diskpart
{
    /// <summary>
    ///     How long the format waits for the freshly created partition's
    ///     volume to be surfaced by the volume manager before the format run is
    ///     attempted anyway.
    /// </summary>
    internal const int VolumeWaitMs = 20_000;

    /// <summary>
    ///     The first diskpart script for one target: erase and repartition.
    ///     `clean` (never `clean all`) wipes any prior layout — GPT+ext4 Deck cards
    ///     included — and MBR is the correct default for removable SD media.
    /// </summary>
    /// <param name="diskNumber">The physical disk number.</param>
    /// <returns>CRLF-separated select-disk, clean and create-primary-partition commands; this helper does not execute them.</returns>
    internal static string BuildPartitionScript(int diskNumber)
    {
        return $"select disk {diskNumber.ToString(CultureInfo.InvariantCulture)}\r\n"
               + "clean\r\n"
               + "create partition primary\r\n";
    }

    /// <summary>
    ///     The second diskpart script for one target: quick NTFS format with
    ///     128K allocation units (the proven game-library tuning).
    ///     A SEPARATE run from <see cref="BuildPartitionScript" />, issued
    ///     only after <see cref="WaitForVolumeAsync" /> — the device-observed volume race
    ///     that forbids merging the scripts is recorded in <c>Shell\AGENTS.md</c>.
    ///     The letter is deliberately NOT part of this script: by the time it runs,
    ///     Windows automount has normally already given the new volume its letter,
    ///     and <see cref="BuildAssignScript" /> is only run when it has not
    ///     handed out the one the card must keep.
    /// </summary>
    /// <param name="diskNumber">The physical disk number.</param>
    /// <param name="label">
    ///     The volume label (already sanitized); quoted so a name
    ///     with spaces stays one token.
    /// </param>
    /// <returns>
    ///     CRLF-separated disk/partition selection and one quick NTFS format command; the caller validates identity
    ///     before execution.
    /// </returns>
    internal static string BuildFormatScript(int diskNumber, string label = SdFormatManager.DefaultLabel)
    {
        return $"select disk {diskNumber.ToString(CultureInfo.InvariantCulture)}\r\n"
               + "select partition 1\r\n"
               + $"format fs=ntfs quick unit=128k label=\"{label}\"\r\n";
    }

    /// <summary>
    ///     The third, conditional diskpart script: give the formatted volume
    ///     its drive letter when automount did not.
    ///     The letter is PINNED to the card's current one when it has it: a bare
    ///     `assign` hands out the next free letter, which device-observed moved a
    ///     card from E: to D: across a format and collided with another library's
    ///     path. A card reader's letter must stay put across reformats and swaps.
    ///     A letterless card (raw / ext4 Deck card) gets a bare `assign`.
    /// </summary>
    /// <param name="diskNumber">The physical disk number.</param>
    /// <param name="preferredLetter">The letter to reassign, or '\0' for none.</param>
    /// <returns>
    ///     Commands selecting partition 1 and assigning the requested uppercase A-Z letter, or the next free letter for
    ///     any other value.
    /// </returns>
    internal static string BuildAssignScript(int diskNumber, char preferredLetter)
    {
        return $"select disk {diskNumber.ToString(CultureInfo.InvariantCulture)}\r\n"
               + "select partition 1\r\n"
               + (preferredLetter is >= 'A' and <= 'Z'
                   ? $"assign letter={preferredLetter}\r\n"
                   : "assign\r\n");
    }

    /// <summary>
    ///     Writes the script beside the log (an elevated diskpart consumes
    ///     it — never %TEMP%, same rule as the de-elevation task XML), runs
    ///     diskpart, deletes the script.
    /// </summary>
    /// <param name="store">The owner's script directory.</param>
    /// <param name="script">The full diskpart script text.</param>
    /// <param name="lifetime">Cancels the process wait during shutdown.</param>
    /// <returns>The diskpart run's outcome, exit code and output.</returns>
    internal static async Task<ConsoleToolResult> RunAsync(ConfigStore store, string script,
        CancellationToken lifetime)
    {
        lifetime.ThrowIfCancellationRequested();
        Log.Info($"Format: diskpart script:\n{script.TrimEnd()}");
        var scriptPath = Path.Combine(store.Context.Root, $"format-disk-{Guid.NewGuid():N}.dp.txt");
        await using (var stream = new FileStream(scriptPath, FileMode.CreateNew, FileAccess.Write,
                         FileShare.None, 4096, FileOptions.WriteThrough))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            await writer.WriteAsync(script);
            await writer.FlushAsync();
            stream.Flush(true);
        }

        try
        {
            // Absolute System32 paths: this flow is elevated, and a bare exe name is
            // searched in the application directory (per-user install, user-writable)
            // before System32.
            var acl = await ConsoleTool.RunAsync(
                ConsoleTool.System32("icacls.exe"),
                $"\"{scriptPath}\" /setintegritylevel H", 10_000, lifetime);
            if (acl.Outcome != ConsoleToolRunOutcome.Succeeded)
            {
                throw new IOException($"Could not protect diskpart script ({acl.ExitCode}): {acl.Output}");
            }

            var result = await ConsoleTool.RunAsync(
                ConsoleTool.System32("diskpart.exe"), $"/s \"{scriptPath}\"", 600_000, lifetime).ConfigureAwait(false);
            return result;
        }
        finally
        {
            try
            {
                File.Delete(scriptPath);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    ///     Issues TRIM (retrim) for the volume's free space via
    ///     <c>Optimize-Volume -ReTrim</c> so the flash controller marks the freshly
    ///     wiped blocks erasable — the proven SD write-speed win. Optimize-Volume is
    ///     used over <c>defrag /L</c> because it retrims REMOVABLE media too (plain
    ///     defrag skips it). Best-effort: a reader that does not pass TRIM just makes
    ///     the cmdlet fail, which is logged and the format continues. Runs elevated
    ///     (the format flow already is).
    /// </summary>
    /// <param name="letter">The mounted volume's drive letter.</param>
    /// <param name="cancellationToken">Cancels the process wait during shutdown.</param>
    /// <returns>True when the retrim was issued; false when the reader or volume refused it.</returns>
    /// <remarks>
    ///     Steam's storage page has a Trim button of its own, which runs this same retrim outside a
    ///     format. It is not gated on the format switch: trimming free space erases nothing.
    /// </remarks>
    internal static async Task<bool> RetrimAsync(char letter, CancellationToken cancellationToken = default)
    {
        try
        {
            // Absolute path for the same reason the diskpart run uses one: an elevated
            // caller must never search the user-writable application directory.
            var powershell = Path.Combine(
                Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var result = await ConsoleTool.RunAsync(
                powershell,
                "-NoProfile -NonInteractive -Command \"Optimize-Volume -DriveLetter "
                + letter + " -ReTrim -ErrorAction Stop\"",
                300_000, cancellationToken);
            var output = result.Output;
            var exitCode = result.ExitCode ?? -1;
            if (result.Outcome == ConsoleToolRunOutcome.Succeeded)
            {
                Log.Info($"Format: retrimmed {letter}: (TRIM issued for free space).");
                return true;
            }

            Log.Info($"Format: retrim of {letter}: not applied (exit {exitCode}); "
                     + $"the reader may not pass TRIM. Output:\n{output.Trim()}");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"Format: retrim of {letter}: failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    ///     Polls the volume manager's device interfaces until one of them
    ///     maps back to the disk — i.e. the partition just created has a volume for
    ///     diskpart's `format` to focus. Letter-agnostic on purpose: the volume has
    ///     none yet. Worker thread; <see cref="VolumeWaitMs" /> cap.
    /// </summary>
    /// <param name="diskNumber">The physical disk number.</param>
    /// <param name="cancellationToken">Cancels enumeration waits during shutdown.</param>
    /// <returns>Milliseconds until the volume was seen, or -1 on timeout.</returns>
    internal static async Task<int> WaitForVolumeAsync(int diskNumber, CancellationToken cancellationToken)
    {
        var started = Environment.TickCount64;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var path in NativeStorage.ListVolumeInterfaces())
            {
                using var volume = NativeStorage.OpenVolumeForQueryPath(path);
                if (!volume.IsInvalid
                    && NativeStorage.TryGetDeviceNumber(volume, out var type, out var disk)
                    && type == NativeStorage.FileDeviceDisk && disk == diskNumber)
                {
                    return (int)(Environment.TickCount64 - started);
                }
            }

            if (Environment.TickCount64 - started >= VolumeWaitMs)
            {
                return -1;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Polls for the freshly assigned drive letter by matching mounted
    ///     volumes back to the disk number. Worker thread; ~15 s cap by default
    ///     (typically 1-3 s after diskpart's assign).
    /// </summary>
    /// <param name="diskNumber">The physical disk number.</param>
    /// <param name="attempts">How many 500 ms polls to make before giving up.</param>
    /// <param name="cancellationToken">Cancels enumeration waits during shutdown.</param>
    /// <returns>The letter of the first ready volume on the disk, or null when none appeared.</returns>
    internal static async Task<char?> WaitForLetterAsync(int diskNumber, int attempts,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var volume in NativeStorage.MountedVolumes()
                         .Where(candidate => candidate.Disk == diskNumber && candidate.Ready))
            {
                return volume.Letter;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>Resolves a drive letter to its volume GUID root and the physical disk under it.</summary>
    /// <param name="letter">The drive letter to resolve.</param>
    /// <returns>The volume GUID root and disk number, or (null, -1) when the letter maps to no disk volume.</returns>
    internal static (string? Root, int Disk) ReadVolume(char letter)
    {
        if (!NativeStorage.TryGetVolumeGuidPath(letter, out var root) || root is null)
        {
            return (null, -1);
        }

        using var volume = NativeStorage.OpenVolumeForQueryPath(root);
        return !volume.IsInvalid && NativeStorage.TryGetDeviceNumber(volume, out var type, out var disk)
                                 && type == NativeStorage.FileDeviceDisk
            ? (root, disk)
            : (null, -1);
    }

    /// <summary>
    ///     The drive letters currently mounted on one physical disk, ascending.
    ///     Read fresh instead of from the row's enumeration snapshot, and covering EVERY
    ///     volume of a multi-partition disk: `clean` erases all of them, so a Steam
    ///     library on any one of them has to be found before the erase — not only the
    ///     one on the pinned letter. Worker thread.
    /// </summary>
    /// <param name="diskNumber">The physical disk number.</param>
    /// <returns>The mounted letters in ascending order, empty when the disk has none.</returns>
    internal static List<char> LettersOnDisk(int diskNumber)
    {
        var letters = NativeStorage.MountedVolumes()
            .Where(volume => volume.DeviceType == NativeStorage.FileDeviceDisk && volume.Disk == diskNumber)
            .Select(volume => volume.Letter)
            .ToList();
        letters.Sort();
        return letters;
    }
}
