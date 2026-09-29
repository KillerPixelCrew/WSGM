using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace WSGM.Plugin.IntelGpu.Graphics;

/// <summary>What the Intel driver currently reports for the shared-memory split.</summary>
/// <param name="Percent">The pinning limit, as a whole percentage of system memory.</param>
/// <param name="ReportedAdapterBytes">
///     The adapter memory size the driver publishes, or zero when it is unreadable. This follows the
///     percentage but only across a restart, so a fresh write and this value legitimately disagree
///     until the machine reboots.
/// </param>
// ReSharper disable once NotAccessedPositionalProperty.Global
internal readonly record struct IntelGraphicsMemoryState(int Percent, ulong ReportedAdapterBytes);

/// <summary>
///     Intel's Shared GPU Memory Override, as the driver stores it.
/// </summary>
/// <remarks>
///     The system/video split on an Intel integrated GPU is a driver setting rather than a firmware
///     carve-out, and it is not in the Graphics Control Library: <c>ControlLib.dll</c> exposes four
///     memory entry points and every one of them is a get. What Intel Graphics Software drives is
///     <c>GpuSystemMemoryPinninglimit</c>, a percentage under the display adapter's <c>GMM</c> key,
///     which the driver reads when it sets up its memory manager. That is why the change needs a
///     restart and why nothing here can verify the effect, only the setting. It is the only value this
///     package writes to the registry.
///     <para>
///         Claw evidence, 2026-09-10, driver <c>32.0.101.8992</c>: the value read 57, Intel documents 57%
///         as the feature's default, installed memory was 32 GB with <c>ullTotalPhys</c> at
///         33,866,657,792 bytes, and the adapter reported exactly 19,327,352,832 bytes, 57.07% of it. The
///         percentage and the reported adapter size agree to three digits, which is what ties this key to
///         the feature. The adapter's own <c>(16GB)</c> name string is the nominal half of installed memory
///         and does not track the setting.
///     </para>
///     <para>
///         The accepted range is 13-87 percent, which is what Intel Graphics Software itself offers. Intel
///         publishes the 57% default and a 10 GB system-memory requirement but no formula for the bounds,
///         so they are taken from the shipping control rather than derived.
///     </para>
///     <para>
///         There is no companion flag saying whether the split has been changed: the default is the literal
///         value 57. Confirmed on the Claw on 2026-09-10 by watching Intel Graphics Software both ways:
///         setting 44 wrote 44, and pressing reset wrote 57 back rather than deleting the value. Nothing
///         else moved either time. So an absent value is the default too, and this transport reports 57
///         for it rather than treating an untouched machine as one without the feature.
///     </para>
/// </remarks>
internal sealed partial class IntelGraphicsMemoryTransport
{
    /// <summary>The display adapter class, whose numbered subkeys are the installed adapters.</summary>
    public const string AdapterClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>Lowest percentage Intel Graphics Software offers.</summary>
    public const int MinimumPercent = 13;

    /// <summary>Highest percentage Intel Graphics Software offers.</summary>
    public const int MaximumPercent = 87;

    /// <summary>Intel's documented default, and what an untouched driver stores.</summary>
    public const int DefaultPercent = 57;

    /// <summary>The graphics memory manager subkey that carries the pinning limit.</summary>
    private const string MemoryManagerSubkey = "GMM";

    /// <summary>Intel's own name for the setting.</summary>
    private const string PinningLimitValue = "GpuSystemMemoryPinninglimit";

    /// <summary>The 64-bit adapter memory size the driver publishes for itself.</summary>
    private const string ReportedSizeValue = "HardwareInformation.qwMemorySize";

    /// <summary>Intel's documented system-memory floor for the feature, in bytes.</summary>
    private const ulong MinimumSystemMemoryBytes = 10UL * 1024 * 1024 * 1024;

    /// <summary>The first Intel driver that shipped Shared GPU Memory Override.</summary>
    private static readonly Version FirstSupportedDriver = new(32, 0, 101, 6974);

    private readonly string? _adapterPath;
    private readonly string _classPath;
    private readonly IntelLog _log;
    private readonly RegistryKey _root;
    private readonly ulong _totalPhysicalBytes;

    /// <summary>Resolves the adapter that carries the setting, without writing anything.</summary>
    /// <param name="log">Receives the decisions.</param>
    public IntelGraphicsMemoryTransport(IntelLog log)
        : this(Registry.LocalMachine, AdapterClassKey, TotalPhysicalBytes(), log)
    {
    }

    /// <summary>Resolves the adapter below a supplied root, for tests.</summary>
    /// <param name="root">The hive to search.</param>
    /// <param name="classPath">The display adapter class key path below it.</param>
    /// <param name="totalPhysicalBytes">Total physical memory, or zero when unknown.</param>
    /// <param name="log">Receives the decisions.</param>
    /// <remarks>
    ///     The seam exists because the real key is machine-wide and under HKLM, which no test may write.
    ///     It changes where the transport looks, never what it accepts.
    /// </remarks>
    internal IntelGraphicsMemoryTransport(
        RegistryKey root,
        string classPath,
        ulong totalPhysicalBytes,
        IntelLog? log = null)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _classPath = classPath ?? throw new ArgumentNullException(nameof(classPath));
        _totalPhysicalBytes = totalPhysicalBytes;
        _log = log ?? IntelLog.None;
        _adapterPath = ResolveAdapter();
    }

    /// <summary>Whether this machine offers the setting at all.</summary>
    public bool IsAvailable => _adapterPath is not null;

    /// <summary>
    ///     The resolved adapter's <c>MatchingDeviceId</c>, for example <c>pci\ven_8086&amp;dev_4688</c>, so
    ///     the plugin can attach the row to the IGCL adapter it belongs to.
    /// </summary>
    public string? MatchingDeviceId { get; private set; }

    /// <summary>Reads the current split, or null when it cannot be read.</summary>
    /// <returns>The stored percentage and the size the driver currently reports.</returns>
    public IntelGraphicsMemoryState? Read()
    {
        if (_adapterPath is null)
        {
            return null;
        }

        try
        {
            using var adapter = _root.OpenSubKey(_adapterPath);
            if (adapter is null)
            {
                return null;
            }

            // An absent value is the default, not a missing feature: Intel Graphics Software stores the
            // literal 57 both when it ships and when the user presses reset.
            using var memory = adapter.OpenSubKey(MemoryManagerSubkey);
            var percent = memory?.GetValue(PinningLimitValue) is int stored ? stored : DefaultPercent;
            if (percent is < MinimumPercent or > MaximumPercent)
            {
                return null;
            }

            ulong reported = adapter.GetValue(ReportedSizeValue) switch
            {
                long size and > 0 => (ulong)size,
                int size and > 0 => (uint)size,
                _ => 0
            };
            return new IntelGraphicsMemoryState(percent, reported);
        }
        catch (Exception error) when (IsRegistryFailure(error))
        {
            _log.Warn("intel-memory", $"Reading the shared-memory split failed: {error.Message}");
            return null;
        }
    }

    /// <summary>Writes a new split and reads it back.</summary>
    /// <param name="percent">The requested percentage, which must be within the offered range.</param>
    /// <returns>
    ///     Null when the write itself failed, otherwise whether the stored value reads back as the
    ///     requested one.
    /// </returns>
    /// <remarks>
    ///     Only the setting is verified. The driver applies it when it next initializes, so the
    ///     capability says the value takes effect after a restart. Nothing is journalled for restore:
    ///     this is a persistent user choice, and putting it back on a normal stop would undo it.
    /// </remarks>
    public bool? TryWrite(int percent)
    {
        if (_adapterPath is null || percent is < MinimumPercent or > MaximumPercent)
        {
            return null;
        }

        try
        {
            using var adapter = _root.OpenSubKey(_adapterPath, true);

            // Created when absent, because absent is the default rather than a refusal and Intel's
            // own software writes into the same place.
            using var memory = adapter?.CreateSubKey(MemoryManagerSubkey, true);
            if (memory is null)
            {
                _log.Warn("intel-memory", "The graphics memory manager key is not writable.");
                return null;
            }

            memory.SetValue(PinningLimitValue, percent, RegistryValueKind.DWord);
            var applied = memory.GetValue(PinningLimitValue) is int stored && stored == percent;
            _log.Info(
                "intel-memory",
                applied
                    ? $"Shared GPU memory limit set to {percent}% ({DescribeBytes(BytesForPercent(percent))}); "
                      + "it takes effect at the next restart."
                    : $"Shared GPU memory limit did not read back as {percent}%.");
            return applied;
        }
        catch (Exception error) when (IsRegistryFailure(error))
        {
            _log.Warn("intel-memory", $"Writing the shared-memory split failed: {error.Message}");
            return null;
        }
    }

    /// <summary>How much memory a percentage corresponds to on this machine.</summary>
    /// <param name="percent">A percentage within the offered range.</param>
    /// <returns>The byte count, or zero when total memory is unknown.</returns>
    internal ulong BytesForPercent(int percent)
    {
        return _totalPhysicalBytes == 0 ? 0 : _totalPhysicalBytes / 100 * (ulong)percent;
    }

    /// <summary>Renders a byte count for a trace line.</summary>
    /// <param name="bytes">The byte count, or zero when it is unknown.</param>
    /// <returns>A short gibibyte figure, or "unknown" when there is nothing to render.</returns>
    private static string DescribeBytes(ulong bytes)
    {
        return bytes == 0
            ? "unknown"
            : string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1024 * 1024 * 1024):0.0} GiB");
    }

    /// <summary>
    ///     Finds the one Intel adapter that stores the setting.
    /// </summary>
    /// <returns>Its registry path below the root, or null when there is not exactly one.</returns>
    /// <remarks>
    ///     The adapter index is not fixed (<c>0001</c> on the Claw, <c>0000</c> elsewhere), so it is
    ///     matched rather than hard-coded. Two matching adapters is ambiguous rather than a reason to pick
    ///     one.
    /// </remarks>
    private string? ResolveAdapter()
    {
        if (_totalPhysicalBytes < MinimumSystemMemoryBytes)
        {
            return null;
        }

        try
        {
            using var adapters = _root.OpenSubKey(_classPath);
            if (adapters is null)
            {
                return null;
            }

            List<string> supported = [];
            List<string> storing = [];
            foreach (var name in adapters.GetSubKeyNames())
            {
                if (name.Length != 4 || !int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    continue;
                }

                switch (Classify(adapters, name))
                {
                    case AdapterMatch.StoresLimit:
                        storing.Add(name);
                        supported.Add(name);
                        break;
                    case AdapterMatch.Supported:
                        supported.Add(name);
                        break;
                    case AdapterMatch.None:
                    default:
                        break;
                }
            }

            // An adapter that already carries the value is the better match, because the value is only
            // ever written under the one the driver reads it from. Falling back to a lone supported Intel
            // adapter is what makes an untouched machine work at all.
            var candidates = storing.Count > 0 ? storing : supported;
            switch (candidates.Count)
            {
                case 1:
                {
                    using var chosen = adapters.OpenSubKey(candidates[0]);
                    MatchingDeviceId = chosen?.GetValue("MatchingDeviceId") as string;
                    return $@"{_classPath}\{candidates[0]}";
                }
                case > 1:
                    _log.Warn(
                        "intel-memory",
                        "More than one adapter could hold the shared-memory split; leaving it alone.");
                    break;
            }

            return null;
        }
        catch (Exception error) when (IsRegistryFailure(error))
        {
            _log.Warn("intel-memory", $"Enumerating display adapters failed: {error.Message}");
            return null;
        }
    }

    /// <summary>Classifies one adapter subkey.</summary>
    /// <param name="adapters">The open display adapter class key.</param>
    /// <param name="name">The numbered subkey to inspect.</param>
    /// <returns>How well the adapter matches.</returns>
    /// <remarks>
    ///     Failures are contained to the one subkey rather than the enumeration. Measured on the Claw on
    ///     2026-09-10: the class holds an unreadable <c>0000</c> alongside the Intel adapter, so letting an
    ///     access failure escape here would have removed the feature on a machine that has it.
    /// </remarks>
    private static AdapterMatch Classify(RegistryKey adapters, string name)
    {
        try
        {
            using var adapter = adapters.OpenSubKey(name);
            if (adapter is null || !IsSupportedIntelDriver(adapter))
            {
                return AdapterMatch.None;
            }

            using var memory = adapter.OpenSubKey(MemoryManagerSubkey);
            return memory?.GetValue(PinningLimitValue) is int ? AdapterMatch.StoresLimit : AdapterMatch.Supported;
        }
        catch (Exception error) when (IsRegistryFailure(error))
        {
            return AdapterMatch.None;
        }
    }

    /// <summary>Whether an adapter subkey is an Intel driver new enough to have the feature.</summary>
    /// <param name="adapter">An open adapter subkey.</param>
    /// <returns><see langword="true" /> when the driver is Intel and at or past the first release.</returns>
    private static bool IsSupportedIntelDriver(RegistryKey adapter)
    {
        if (adapter.GetValue("ProviderName") is not string provider
            || !provider.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return adapter.GetValue("DriverVersion") is string version
               && Version.TryParse(version, out var parsed)
               && parsed >= FirstSupportedDriver;
    }

    /// <summary>Total physical memory as Windows reports it, in bytes.</summary>
    /// <returns>The byte count, or zero when the query fails.</returns>
    /// <remarks>
    ///     The driver's percentage is against this figure rather than the installed SPD total: 57% of
    ///     the Claw's 33,866,657,792 bytes is the 19,327,352,832 it reports, while 57% of the
    ///     34,359,738,368 bytes of installed DIMMs is not.
    /// </remarks>
    private static ulong TotalPhysicalBytes()
    {
        MemoryStatusEx status = default;
        status.Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        return GlobalMemoryStatusEx(ref status) ? status.TotalPhys : 0;
    }

    private static bool IsRegistryFailure(Exception error)
    {
        return error
            is SecurityException
            or UnauthorizedAccessException
            or IOException
            or ObjectDisposedException;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    /// <summary>How well one adapter subkey matches the adapter that owns the setting.</summary>
    private enum AdapterMatch
    {
        /// <summary>Not an Intel driver new enough to have the feature.</summary>
        None,

        /// <summary>A supported Intel driver that has no stored value, which means the default.</summary>
        Supported,

        /// <summary>A supported Intel driver that already carries the stored value.</summary>
        StoresLimit
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}
