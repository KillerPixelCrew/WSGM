using System.Runtime.InteropServices;
using System.Text;

namespace WSGM.Plugin.IntelGpu.Igcl;

/// <summary>One Intel adapter as IGCL reports it.</summary>
/// <param name="Handle">The IGCL adapter handle, valid for this session only.</param>
/// <param name="Index">Enumeration order within the session.</param>
/// <param name="PciVendorId">PCI vendor id.</param>
/// <param name="PciDeviceId">PCI device id.</param>
/// <param name="Bus">PCI bus, or zero when the driver did not report the address.</param>
/// <param name="Device">PCI device number.</param>
/// <param name="Function">PCI function.</param>
/// <param name="HasBusAddress">Whether the driver answered the version that carries the address.</param>
/// <param name="Luid">The adapter LUID, which changes every boot and is only a session key.</param>
/// <param name="Name">The driver's adapter name.</param>
/// <param name="Integrated">Whether the driver flags it as integrated graphics.</param>
/// <param name="DriverVersion">The driver version, packed as IGCL reports it.</param>
internal sealed record IgclAdapter(
    nint Handle,
    int Index,
    uint PciVendorId,
    uint PciDeviceId,
    byte Bus,
    byte Device,
    byte Function,
    bool HasBusAddress,
    long Luid,
    string Name,
    bool Integrated,
    ulong DriverVersion);

/// <summary>One active display output on an Intel adapter.</summary>
/// <param name="Handle">The IGCL output handle, valid for this session only.</param>
/// <param name="Adapter">The adapter driving it.</param>
/// <param name="Index">Output order within the adapter.</param>
/// <param name="Properties">The driver's display properties.</param>
/// <param name="Internal">
///     Whether the encoder drives the built-in panel (<c>CTL_ENCODER_CONFIG_FLAG_INTERNAL_DISPLAY</c>), or
///     null when the driver does not answer <c>ctlGetAdaperDisplayEncoderProperties</c>.
/// </param>
internal sealed record IgclOutput(
    nint Handle,
    IgclAdapter Adapter,
    int Index,
    CtlDisplayProperties Properties,
    bool? Internal)
{
    /// <summary>The Windows display target id.</summary>
    public uint TargetId => Properties.EncoderId.WindowsTargetId;
}

/// <summary>
///     One IGCL session: <c>ctlInit</c> to <c>ctlClose</c>, with the adapters and active outputs it
///     enumerated.
/// </summary>
/// <remarks>
///     Every call goes through the plugin's single lane, so nothing here locks. A session that reports
///     <see cref="IgclResult.DeviceLost" /> is dead: the plugin closes it and opens a new one on the next
///     observation, which is what a driver update or a display driver reset requires.
/// </remarks>
internal sealed unsafe class IgclSession : IDisposable
{
    /// <summary>IGCL 1.1, the version verified on the reference machines.</summary>
    private const uint ImplementationVersion = (1 << 16) | 1;

    private const uint IntelVendorId = 0x8086;
    private const int MaxDevices = 8;
    private const int MaxOutputs = 32;
    private const uint DisplayActive = 1 << 0;
    private const uint DisplayAttached = 1 << 1;

    /// <summary><c>CTL_ENCODER_CONFIG_FLAG_INTERNAL_DISPLAY</c>.</summary>
    private const uint EncoderInternalDisplay = 1 << 0;
    private readonly IntelLog _log;

    private nint _handle;

    private IgclSession(IgclApi api, nint handle, IntelLog log)
    {
        Api = api;
        _handle = handle;
        _log = log;
    }

    /// <summary>The bound entry points.</summary>
    public IgclApi Api { get; }

    /// <summary>The Intel adapters, in enumeration order.</summary>
    public IReadOnlyList<IgclAdapter> Adapters { get; private set; } = [];

    /// <summary>The active, attached display outputs of every Intel adapter.</summary>
    public IReadOnlyList<IgclOutput> Outputs { get; private set; } = [];

    /// <summary>The IGCL version the driver implements.</summary>
    public uint SupportedVersion { get; private init; }

    /// <summary>Set once a call reported the session gone.</summary>
    public bool Lost { get; private set; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_handle == 0)
        {
            return;
        }

        try
        {
            _ = Api.Close(_handle);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _log.Warn("igcl", $"ctlClose threw: {error.Message}");
        }

        _handle = 0;
        Adapters = [];
        Outputs = [];
    }

    /// <summary>Records a result, marking the session lost when it says so.</summary>
    /// <param name="result">Any IGCL result.</param>
    /// <returns>The same result.</returns>
    public int Observe(int result)
    {
        if (!Lost && IgclResult.IsSessionLost(result))
        {
            Lost = true;
            _log.Warn("igcl", $"The driver reported the session gone ({IgclResult.Describe(result)}); reopening.");
        }

        return result;
    }

    /// <summary>Initialises IGCL and enumerates the Intel adapters and their active outputs.</summary>
    /// <param name="api">The bound library.</param>
    /// <param name="log">Receives the decisions.</param>
    /// <returns>The session, or null when <c>ctlInit</c> or the enumeration failed.</returns>
    public static IgclSession? TryOpen(IgclApi api, IntelLog log)
    {
        CtlInitArgs args = default;
        args.Size = (uint)sizeof(CtlInitArgs);
        args.AppVersion = ImplementationVersion;
        nint handle = 0;
        var result = api.Init(&args, &handle);
        if (result != IgclResult.Success || handle == 0)
        {
            log.Warn("igcl", $"ctlInit refused with {IgclResult.Describe(result)}.");
            return null;
        }

        IgclSession session = new(api, handle, log) { SupportedVersion = args.SupportedVersion };
        if (session.TryEnumerate())
        {
            return session;
        }

        session.Dispose();
        return null;
    }

    /// <remarks>
    ///     The count call with a zero count and a null array is not optional. Measured on 2026-09-29 on
    ///     driver 32.0.101.7088: skipping it and passing an array straight away left every later call
    ///     answering <c>CTL_RESULT_ERROR_NOT_INITIALIZED</c>.
    /// </remarks>
    private bool TryEnumerate()
    {
        uint count = 0;
        var result = Api.EnumerateDevices(_handle, &count, null);
        if (result != IgclResult.Success || count == 0)
        {
            _log.Info("igcl", $"No adapters enumerated ({IgclResult.Describe(result)}, count {count}).");
            return false;
        }

        count = Math.Min(count, MaxDevices);
        var devices = stackalloc nint[MaxDevices];
        result = Api.EnumerateDevices(_handle, &count, devices);
        if (result != IgclResult.Success)
        {
            _log.Warn("igcl", $"Adapter handles could not be fetched ({IgclResult.Describe(result)}).");
            return false;
        }

        List<IgclAdapter> adapters = [];
        List<IgclOutput> outputs = [];
        for (var index = 0; index < count; index++)
        {
            if (devices[index] == 0 || ReadAdapter(devices[index], index) is not { } adapter)
            {
                continue;
            }

            if (adapter.PciVendorId != IntelVendorId)
            {
                _log.Info("igcl", $"Adapter {index} is vendor {adapter.PciVendorId:x4}; not Intel, skipped.");
                continue;
            }

            adapters.Add(adapter);
            outputs.AddRange(EnumerateOutputs(adapter));
        }

        Adapters = adapters;
        Outputs = outputs;
        return adapters.Count > 0;
    }

    private IgclAdapter? ReadAdapter(nint handle, int index)
    {
        if (Api.GetDeviceProperties is null)
        {
            _log.Warn("igcl", "ctlGetDeviceProperties is missing; adapters cannot be identified.");
            return null;
        }

        long luid = 0;
        CtlDeviceAdapterProperties properties = default;
        properties.Size = (uint)sizeof(CtlDeviceAdapterProperties);

        // Version 2 carries the PCI bus address that keeps the adapter's identity stable across boots.
        // A driver that refuses it is asked again at version 0, which still names the device.
        properties.Version = 2;
        properties.DeviceId = (nint)(&luid);
        properties.DeviceIdSize = sizeof(long);
        var result = Api.GetDeviceProperties(handle, &properties);
        var hasAddress = result == IgclResult.Success;
        if (!hasAddress)
        {
            properties = default;
            properties.Size = (uint)sizeof(CtlDeviceAdapterProperties);
            properties.DeviceId = (nint)(&luid);
            properties.DeviceIdSize = sizeof(long);
            result = Api.GetDeviceProperties(handle, &properties);
        }

        if (result != IgclResult.Success)
        {
            _log.Warn("igcl", $"Adapter {index} properties refused with {IgclResult.Describe(result)}.");
            return null;
        }

        var name = Encoding.ASCII.GetString(new ReadOnlySpan<byte>(properties.Name, 100)).TrimEnd('\0').Trim();
        return new IgclAdapter(
            handle,
            index,
            properties.PciVendorId,
            properties.PciDeviceId,
            properties.Bus,
            properties.Device,
            properties.Function,
            hasAddress && (properties.Bus | properties.Device | properties.Function) != 0,
            luid,
            name,
            (properties.GraphicsAdapterProperties & 1) != 0,
            properties.DriverVersion);
    }

    private List<IgclOutput> EnumerateOutputs(IgclAdapter adapter)
    {
        List<IgclOutput> outputs = [];
        if (Api.EnumerateDisplayOutputs is null || Api.GetDisplayProperties is null)
        {
            return outputs;
        }

        uint count = 0;
        if (Api.EnumerateDisplayOutputs(adapter.Handle, &count, null) != IgclResult.Success || count == 0)
        {
            return outputs;
        }

        count = Math.Min(count, MaxOutputs);
        var handles = stackalloc nint[MaxOutputs];
        if (Api.EnumerateDisplayOutputs(adapter.Handle, &count, handles) != IgclResult.Success)
        {
            return outputs;
        }

        var inactive = 0;
        for (var index = 0; index < count; index++)
        {
            CtlDisplayProperties properties = default;
            properties.Size = (uint)sizeof(CtlDisplayProperties);
            if (handles[index] == 0
                || Api.GetDisplayProperties(handles[index], &properties) != IgclResult.Success
                || (properties.DisplayConfigFlags & (DisplayActive | DisplayAttached))
                != (DisplayActive | DisplayAttached))
            {
                // Unattached connectors are most of any enumeration: the Claw reports twelve outputs
                // of which one is real.
                inactive++;
                continue;
            }

            outputs.Add(new IgclOutput(handles[index], adapter, index, properties, ReadInternal(handles[index])));
        }

        _log.Info(
            "igcl",
            $"Adapter {adapter.Index} ({adapter.Name}): {outputs.Count} active outputs, {inactive} inactive.");
        return outputs;
    }

    /// <summary>Asks the driver whether an output drives the built-in panel.</summary>
    /// <param name="output">The output handle.</param>
    /// <returns>The encoder's internal-display flag, or null when the driver does not say.</returns>
    private bool? ReadInternal(nint output)
    {
        if (Api.GetEncoderProperties is null)
        {
            return null;
        }

        CtlDisplayEncoderProperties properties = default;
        properties.Size = (uint)sizeof(CtlDisplayEncoderProperties);
        return Api.GetEncoderProperties(output, &properties) == IgclResult.Success
            ? (properties.EncoderConfigFlags & EncoderInternalDisplay) != 0
            : null;
    }

    /// <summary>Reads the supported 3D feature table of one adapter.</summary>
    /// <param name="adapter">The adapter.</param>
    /// <returns>The feature details, or an empty list when the driver reports none.</returns>
    /// <remarks>
    ///     The two-call shape from Intel's own sample: the count with a null array, then the array of
    ///     72-byte elements. Measured on 2026-09-29: the caps header is 24 bytes and each element 72, with
    ///     the value union at offset 8, and the laptop's driver listed features 4, 9, 11, 13, 15 and 17.
    /// </remarks>
    public IReadOnlyList<Ctl3dFeatureDetails> Read3dCapabilities(IgclAdapter adapter)
    {
        if (Api.GetSupported3dCapabilities is null || Api.GetSet3dFeature is null)
        {
            return [];
        }

        Ctl3dFeatureCaps caps = default;
        caps.Size = (uint)sizeof(Ctl3dFeatureCaps);
        var result = Observe(Api.GetSupported3dCapabilities(adapter.Handle, &caps));
        if (result != IgclResult.Success || caps.NumSupportedFeatures is 0 or > 64)
        {
            _log.Info(
                "igcl",
                $"Adapter {adapter.Index} reports no 3D features ({IgclResult.Describe(result)}, "
                + $"count {caps.NumSupportedFeatures}).");
            return [];
        }

        var details = new Ctl3dFeatureDetails[caps.NumSupportedFeatures];
        fixed (Ctl3dFeatureDetails* buffer = details)
        {
            caps.FeatureDetails = (nint)buffer;
            result = Observe(Api.GetSupported3dCapabilities(adapter.Handle, &caps));
        }

        if (result == IgclResult.Success)
        {
            return details.Take((int)Math.Min(caps.NumSupportedFeatures, (uint)details.Length)).ToArray();
        }

        _log.Warn("igcl", $"Adapter {adapter.Index} feature table refused with {IgclResult.Describe(result)}.");
        return [];
    }

    /// <summary>Asks for one custom feature's capability structure, the way Intel's sample does.</summary>
    /// <typeparam name="T">The custom caps structure.</typeparam>
    /// <param name="adapter">The adapter.</param>
    /// <param name="feature">The feature's details from the table.</param>
    /// <param name="caps">The capability structure, when this returns true.</param>
    /// <returns><see langword="true" /> when the driver filled it.</returns>
    /// <remarks>
    ///     Only called for a feature the table reports as custom-typed with a nonzero custom size, and
    ///     the buffer is at least the size the driver asked for, so the driver never writes past it.
    /// </remarks>
    public bool TryReadCustomCaps<T>(IgclAdapter adapter, Ctl3dFeatureDetails feature, out T caps)
        where T : unmanaged
    {
        caps = default;
        if (Api.GetSupported3dCapabilities is null
            || feature.ValueType != (int)IgclValueType.Custom
            || feature.CustomValueSize <= 0
            || feature.CustomValueSize > 4096)
        {
            return false;
        }

        var length = Math.Max(feature.CustomValueSize, sizeof(T));
        var buffer = (byte*)NativeMemory.AllocZeroed((nuint)length);
        try
        {
            var single = feature;
            single.CustomValue = (nint)buffer;
            Ctl3dFeatureCaps request = default;
            request.Size = (uint)sizeof(Ctl3dFeatureCaps);
            request.NumSupportedFeatures = 1;
            request.FeatureDetails = (nint)(&single);
            if (Observe(Api.GetSupported3dCapabilities(adapter.Handle, &request)) != IgclResult.Success)
            {
                return false;
            }

            caps = *(T*)buffer;
            return true;
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    /// <summary>Gets or sets one 3D feature, globally or for one application.</summary>
    /// <param name="adapter">The adapter.</param>
    /// <param name="request">The request; its Size and application name are filled here.</param>
    /// <param name="application">The executable file name, or null for the global value.</param>
    /// <returns>The driver's result.</returns>
    public int GetSet3dFeature(IgclAdapter adapter, ref Ctl3dFeatureGetSet request, string? application)
    {
        if (Api.GetSet3dFeature is null)
        {
            return IgclResult.NotImplemented;
        }

        request.Size = (uint)sizeof(Ctl3dFeatureGetSet);
        if (string.IsNullOrEmpty(application))
        {
            request.ApplicationName = 0;
            request.ApplicationNameLength = 0;
            fixed (Ctl3dFeatureGetSet* pointer = &request)
            {
                return Observe(Api.GetSet3dFeature(adapter.Handle, pointer));
            }
        }

        var name = Encoding.ASCII.GetBytes(application + "\0");
        fixed (byte* text = name)
        {
            request.ApplicationName = (nint)text;
            request.ApplicationNameLength = (sbyte)(name.Length - 1);
            try
            {
                fixed (Ctl3dFeatureGetSet* pointer = &request)
                {
                    return Observe(Api.GetSet3dFeature(adapter.Handle, pointer));
                }
            }
            finally
            {
                request.ApplicationName = 0;
            }
        }
    }
}
