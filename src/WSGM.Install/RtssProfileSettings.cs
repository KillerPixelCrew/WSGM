using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WSGM.Install;

/// <summary>RTSS's global injection policy, shared by setup and the resident performance owner.</summary>
public static partial class RtssProfileSettings
{
    private static readonly string[] Keys = ["ReflexSleep", "ReflexSetLatencyMarker"];

    /// <summary>Creates missing Reflex settings and disables both injection paths before RTSS starts.</summary>
    /// <param name="profile">The Global profile path inside the verified RTSS installation.</param>
    /// <returns>The names of settings that were created or changed.</returns>
    /// <remarks>
    ///     RTSS uses Windows profile-file semantics. These APIs preserve other sections and the file's
    ///     existing ANSI/Unicode format, and create a fresh Global file when RTSS has never run.
    /// </remarks>
    /// <exception cref="IOException">A profile setting did not retain the disabled value.</exception>
    /// <exception cref="Win32Exception">Windows refused a profile write.</exception>
    public static IReadOnlyList<string> DisableReflexInjection(string profile)
    {
        profile = Path.GetFullPath(profile);
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        var attributes = File.Exists(profile) ? File.GetAttributes(profile) : FileAttributes.Normal;
        var readOnly = attributes.HasFlag(FileAttributes.ReadOnly);
        if (readOnly)
        {
            File.SetAttributes(profile, attributes & ~FileAttributes.ReadOnly);
        }
        try
        {
            var changed = new List<string>();
            foreach (var key in Keys)
            {
                if (Read(profile, key) == "0")
                {
                    continue;
                }
                if (!WritePrivateProfileString("Framerate", key, "0", profile))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), $"RTSS {key} could not be disabled in {profile}.");
                }
                if (Read(profile, key) != "0")
                {
                    throw new IOException($"RTSS {key} did not retain its disabled value in {profile}.");
                }
                changed.Add(key);
            }
            return changed;
        }
        finally
        {
            if (readOnly && File.Exists(profile))
            {
                File.SetAttributes(profile, File.GetAttributes(profile) | FileAttributes.ReadOnly);
            }
        }
    }

    private static string Read(string profile, string key)
    {
        var value = new StringBuilder(capacity: 32);
        _ = GetPrivateProfileString("Framerate", key, string.Empty, value, value.Capacity, profile);
        return value.ToString().Trim();
    }

    [DllImport("kernel32.dll", EntryPoint = "GetPrivateProfileStringW", CharSet = CharSet.Unicode)]
    private static extern uint GetPrivateProfileString(string section, string key, string defaultValue,
        StringBuilder value, int size, string profile);

    [LibraryImport("kernel32.dll", EntryPoint = "WritePrivateProfileStringW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WritePrivateProfileString(string section, string key, string value, string profile);
}
