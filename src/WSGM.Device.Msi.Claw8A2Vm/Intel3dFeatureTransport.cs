using System;
using System.Runtime.InteropServices;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Device.Msi.Claw8A2Vm;

/// <summary>Whether Endurance Gaming runs, and which frame target it aims at when it does.</summary>
/// <remarks>The values are Intel's own; they are what the driver stores and reads back.</remarks>
internal enum EnduranceGamingControl
{
    /// <summary>Never engages.</summary>
    Off = 0,

    /// <summary>Always engages.</summary>
    On = 1,

    /// <summary>The driver decides, which in practice means on battery.</summary>
    Auto = 2,
}

/// <summary>The frame target Endurance Gaming holds to while it is engaged.</summary>
internal enum EnduranceGamingMode
{
    /// <summary>Better performance; Intel documents this as around 60 FPS.</summary>
    Performance = 0,

    /// <summary>Balanced; around 40 FPS.</summary>
    Balanced = 1,

    /// <summary>Maximum battery; around 30 FPS.</summary>
    Battery = 2,
}

/// <summary>How the driver presents frames, which is what "driver-level VSync" actually is.</summary>
/// <remarks>
/// Intel has no <c>CTL_3D_FEATURE_VSYNC</c>. What it has is <c>CTL_3D_FEATURE_GAMING_FLIP_MODES</c>,
/// a flag set whose members are the presentation modes, and forcing VSync on is one of them. That is
/// why this is a choice rather than a boolean: the same control also offers leaving the decision to
/// the application, Intel's Smooth Sync and a capped-FPS mode, and a toggle could express none of
/// them. The values are Intel's <c>ctl_gaming_flip_mode_flag_t</c> bits, not indices.
/// <para>
/// Measured on the reference unit on 2026-09-10 by reading
/// <c>ctlGetSupported3DCapabilities</c>: the adapter reports twelve supported features, and feature
/// 9 is enum-typed with a supported mask of <c>0x2d</c> — application default, VSync on, Smooth Sync
/// and capped FPS. **VSync off is deliberately not in that mask**, because leaving it off is what
/// the application default already means; the driver offers forcing it on, not forcing it off.
/// </para>
/// </remarks>
internal enum GamingFlipMode
{
    /// <summary>The driver does not override the application's own choice.</summary>
    ApplicationDefault = 1 << 0,

    /// <summary>Tearing allowed; frames present as soon as they are ready.</summary>
    /// <remarks>Not offered by the reference driver; kept because the flag exists in Intel's header.</remarks>
    VsyncOff = 1 << 1,

    /// <summary>Frames wait for the display's refresh.</summary>
    VsyncOn = 1 << 2,

    /// <summary>Intel's Smooth Sync: tearing is dithered rather than sharp.</summary>
    SmoothSync = 1 << 3,

    /// <summary>Speed Frame.</summary>
    /// <remarks>Not offered by the reference driver.</remarks>
    SpeedFrame = 1 << 4,

    /// <summary>Capped FPS.</summary>
    CappedFps = 1 << 5,
}

/// <summary>What the driver reports for Endurance Gaming right now.</summary>
/// <param name="Control">Whether it is off, on, or left to the driver.</param>
/// <param name="Mode">The frame target it holds to when engaged.</param>
internal readonly record struct EnduranceGamingState(
    EnduranceGamingControl Control,
    EnduranceGamingMode Mode
);

/// <summary>
/// The Intel 3D features this device's adapter exposes, over the Graphics Control Library.
/// </summary>
/// <remarks>
/// The same library <see cref="ArcSyncTransport"/> already drives, reached the same way: the driver
/// ships <c>ControlLib.dll</c> into <c>System32</c>, so it is loaded by name and its absence simply
/// means unsupported. Nothing is vendored and no wrapper is built — Handheld Companion resolves
/// these through its own compiled <c>IGCL_Wrapper.dll</c>, which is a convenience over the same C
/// API rather than a requirement.
/// <para>
/// Each feature's support is decided by a read of that feature rather than by walking
/// <c>ctlGetSupported3DCapabilities</c>. A successful get is the only claim worth making, it is the
/// state the capability wants to publish anyway, and it avoids marshalling a capability array whose
/// union sizing is one more layout to get wrong for no extra certainty. Probing each separately
/// also means a driver that answers for one and not another still offers the one it has.
/// </para>
/// <para>
/// Layouts come from Intel's published <c>igcl_api.h</c>, not from inference. The one trap is that
/// <c>bool</c> is a single byte in C: a managed <c>bool</c> here would marshal as four and shift
/// every field after it, which is the same hazard <see cref="ArcSyncTransport"/> documents.
/// </para>
/// </remarks>
internal sealed unsafe class Intel3dFeatureTransport : IDisposable
{
    private const int ResultSuccess = 0;

    /// <summary>CTL_3D_FEATURE_ENDURANCE_GAMING.</summary>
    private const int FeatureEnduranceGaming = 1;

    /// <summary>CTL_3D_FEATURE_GAMING_FLIP_MODES: the driver's own frame-presentation mode.</summary>
    private const int FeatureGamingFlipModes = 9;

    /// <summary>CTL_3D_FEATURE_PREBUILT_SHADER_DOWNLOAD.</summary>
    private const int FeaturePrebuiltShaderDownload = 18;

    /// <summary>CTL_PROPERTY_VALUE_TYPE_BOOL: the value travels in the union.</summary>
    private const int ValueTypeBool = 0;

    /// <summary>CTL_PROPERTY_VALUE_TYPE_ENUM: an enable byte and a 32-bit value in the union.</summary>
    private const int ValueTypeEnum = 4;

    /// <summary>CTL_PROPERTY_VALUE_TYPE_CUSTOM: the value travels through the custom pointer.</summary>
    private const int ValueTypeCustom = 5;

    /// <summary>IGCL 1.1, the version this transport asks for.</summary>
    private const uint ImplVersion = (1 << 16) | 1;

    private const int MaxDevices = 8;

    /// <summary>Intel's highest defined 3D feature id, so a bogus stride is rejected.</summary>
    private const int MaxFeatureId = 24;

    /// <summary>More features than Intel defines means the table was not understood.</summary>
    private const uint MaxSupportedFeatures = 64;

    /// <summary>Upper bound on one capability element, and the per-element slack allocated.</summary>
    private const int MaxFeatureDetailStride = 128;

    private nint _library;
    private nint _api;
    private nint _adapter;

    private delegate* unmanaged[Cdecl]<CtlInitArgs*, nint*, int> _init;
    private delegate* unmanaged[Cdecl]<nint, int> _close;
    private delegate* unmanaged[Cdecl]<nint, uint*, nint*, int> _enumerateDevices;
    private delegate* unmanaged[Cdecl]<nint, Ctl3dFeatureGetSet*, int> _getSet3dFeature;

    /// <summary>Optional: an older driver without it still gets every read and write above.</summary>
    private delegate* unmanaged[Cdecl]<nint, Ctl3dFeatureCaps*, int> _getSupported3dCapabilities;

    /// <summary>The managed mirrors' sizes, so a drifted layout fails a test rather than the driver.</summary>
    /// <remarks>
    /// Every IGCL call passes the caller's own sizeof in a Size field and the driver refuses a
    /// mismatch. That refusal is indistinguishable from "this machine has no Endurance Gaming", so
    /// drift here removes the feature silently rather than loudly.
    /// </remarks>
    internal static (int GetSet, int EnduranceGaming) NativeStructureSizes =>
        (sizeof(Ctl3dFeatureGetSet), sizeof(EnduranceGaming));

    /// <summary>Opens the library and selects the adapter Endurance Gaming answers for.</summary>
    /// <returns><see langword="true"/> when the feature can be read on this machine.</returns>
    public bool TryOpen()
    {
        if (!NativeLibrary.TryLoad("ControlLib.dll", out _library))
        {
            PluginTrace.Info("intel3d", "ControlLib.dll not present; Endurance Gaming unavailable.");
            return false;
        }

        if (!TryBind())
        {
            PluginTrace.Warn("intel3d", "ControlLib.dll is missing an expected entry point.");
            return false;
        }

        CtlInitArgs args = default;
        args.Size = (uint)sizeof(CtlInitArgs);
        args.AppVersion = ImplVersion;
        nint api = 0;
        int result = _init(&args, &api);
        if (result != ResultSuccess)
        {
            PluginTrace.Warn("intel3d", $"ctlInit refused with 0x{result:x}.");
            return false;
        }

        _api = api;
        return TrySelectAdapter();
    }

    /// <summary>Reads the current Endurance Gaming state.</summary>
    /// <returns>The state, or null when the adapter does not answer for the feature.</returns>
    public EnduranceGamingState? Read()
    {
        if (_adapter == 0)
        {
            return null;
        }

        EnduranceGaming value = default;
        Ctl3dFeatureGetSet request = default;
        request.Size = (uint)sizeof(Ctl3dFeatureGetSet);
        request.FeatureType = FeatureEnduranceGaming;
        request.ValueType = ValueTypeCustom;
        request.CustomValueSize = sizeof(EnduranceGaming);
        request.CustomValue = (nint)(&value);

        int result = _getSet3dFeature(_adapter, &request);
        if (result != ResultSuccess)
        {
            PluginTrace.Info("intel3d", $"Endurance Gaming read returned 0x{result:x}; unsupported here.");
            return null;
        }

        return new EnduranceGamingState((EnduranceGamingControl)value.Control, (EnduranceGamingMode)value.Mode);
    }

    /// <summary>Applies a control and mode, then confirms the driver reports them back.</summary>
    /// <param name="control">Whether Endurance Gaming should be off, on, or left to the driver.</param>
    /// <param name="mode">The frame target it should hold to.</param>
    /// <returns><see langword="true"/> only when the read-back matches what was asked for.</returns>
    /// <remarks>
    /// The write is issued once and never retried. An unconfirmed write leaves the driver in a state
    /// this transport cannot describe, and writing again on top of that is a guess.
    /// </remarks>
    public bool TryWrite(EnduranceGamingControl control, EnduranceGamingMode mode)
    {
        if (_adapter == 0)
        {
            return false;
        }

        EnduranceGaming value = new() { Control = (uint)control, Mode = (uint)mode };
        Ctl3dFeatureGetSet request = default;
        request.Size = (uint)sizeof(Ctl3dFeatureGetSet);
        request.FeatureType = FeatureEnduranceGaming;
        request.ValueType = ValueTypeCustom;
        request.Set = 1;
        request.CustomValueSize = sizeof(EnduranceGaming);
        request.CustomValue = (nint)(&value);

        int result = _getSet3dFeature(_adapter, &request);
        if (result != ResultSuccess)
        {
            PluginTrace.Warn("intel3d", $"Endurance Gaming write failed with 0x{result:x}.");
            return false;
        }

        EnduranceGamingState? applied = Read();
        if (applied is { } state && state.Control == control && state.Mode == mode)
        {
            return true;
        }

        PluginTrace.Warn(
            "intel3d",
            $"Endurance Gaming write was not confirmed; asked {control}/{mode}, read {applied?.ToString() ?? "nothing"}.");
        return false;
    }

    /// <summary>Reads the driver's current frame-presentation mode.</summary>
    /// <returns>The mode, or null when the driver does not offer the feature.</returns>
    /// <remarks>
    /// An enum-typed feature: <c>ctl_property_enum_t</c> is an enable byte followed by a 32-bit
    /// value, which lands on the union's two words exactly as the bool feature's single byte does.
    /// </remarks>
    public GamingFlipMode? ReadGamingFlipMode()
    {
        if (_adapter == 0)
        {
            return null;
        }

        Ctl3dFeatureGetSet request = default;
        request.Size = (uint)sizeof(Ctl3dFeatureGetSet);
        request.FeatureType = FeatureGamingFlipModes;
        request.ValueType = ValueTypeEnum;

        int result = _getSet3dFeature(_adapter, &request);
        if (result != ResultSuccess)
        {
            PluginTrace.Info("intel3d", $"Gaming flip mode read returned 0x{result:x}; unsupported here.");
            return null;
        }

        // Zero is the driver's resting answer, not a failure: measured on the reference unit, an
        // adapter with no override selected reports enable=1 and value=0. That is exactly what
        // application default means, so it is reported as such rather than as "unreadable" — which
        // is what an earlier version of this did, and it made a working feature look absent.
        uint value = request.Value.Second;
        if (value == 0)
        {
            return GamingFlipMode.ApplicationDefault;
        }

        // More than one bit would be a set rather than a selection, which is not a state this can
        // name. Reporting nothing beats picking one and calling it the answer.
        if ((value & (value - 1)) != 0 || !Enum.IsDefined((GamingFlipMode)value))
        {
            PluginTrace.Info("intel3d", $"Gaming flip mode read an unrecognised value 0x{value:x}.");
            return null;
        }

        return (GamingFlipMode)value;
    }

    /// <summary>Reads which flip modes this adapter's driver actually offers.</summary>
    /// <returns>Intel's supported-mode mask, or null when the capability array cannot be read.</returns>
    /// <remarks>
    /// The capability array is the one IGCL structure whose element layout this package would
    /// otherwise have to assert, and asserting it wrongly reads garbage. So the stride is derived
    /// from the data rather than declared: the first field of each element is the feature id, and
    /// the only stride that yields <c>NumSupportedFeatures</c> distinct ids in range is the right
    /// one. Measured on the reference unit on 2026-09-10 as 72 bytes, with feature 9 enum-typed and
    /// a supported mask of <c>0x2d</c>. A driver whose layout does not resolve returns null and the
    /// caller offers Intel's documented modes instead, where a refused write still fails visibly.
    /// </remarks>
    public uint? ReadSupportedFlipModes()
    {
        if (_adapter == 0 || _getSupported3dCapabilities is null)
        {
            return null;
        }

        Ctl3dFeatureCaps caps = default;
        caps.Size = (uint)sizeof(Ctl3dFeatureCaps);
        if (_getSupported3dCapabilities(_adapter, &caps) != ResultSuccess
            || caps.NumSupportedFeatures is 0 or > MaxSupportedFeatures)
        {
            return null;
        }

        int elements = (int)caps.NumSupportedFeatures;
        int bytes = elements * MaxFeatureDetailStride;
        nint buffer = Marshal.AllocHGlobal(bytes);
        try
        {
            new Span<byte>((void*)buffer, bytes).Clear();
            caps.FeatureDetails = buffer;
            if (_getSupported3dCapabilities(_adapter, &caps) != ResultSuccess)
            {
                return null;
            }

            var raw = new ReadOnlySpan<byte>((void*)buffer, bytes);
            for (int stride = 16; stride <= MaxFeatureDetailStride; stride += 4)
            {
                if (TryReadMask(raw, elements, stride, out uint mask))
                {
                    return mask;
                }
            }

            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Whether one candidate stride yields a coherent feature table, and its flip mask.</summary>
    /// <param name="raw">The capability buffer.</param>
    /// <param name="elements">How many features the driver reported.</param>
    /// <param name="stride">The candidate element size.</param>
    /// <param name="mask">The gaming-flip supported mask, when this returns true.</param>
    /// <returns><see langword="true"/> when the stride produces distinct in-range ids including feature 9.</returns>
    private static bool TryReadMask(ReadOnlySpan<byte> raw, int elements, int stride, out uint mask)
    {
        mask = 0;
        uint seen = 0;
        bool found = false;
        for (int index = 0; index < elements; index++)
        {
            int offset = index * stride;
            if (offset + 16 > raw.Length)
            {
                return false;
            }

            int feature = BitConverter.ToInt32(raw.Slice(offset, 4));
            if (feature is < 0 or > MaxFeatureId || (seen & (1u << feature)) != 0)
            {
                return false;
            }

            seen |= 1u << feature;
            if (feature != FeatureGamingFlipModes)
            {
                continue;
            }

            if (BitConverter.ToInt32(raw.Slice(offset + 4, 4)) != ValueTypeEnum)
            {
                return false;
            }

            mask = BitConverter.ToUInt32(raw.Slice(offset + 8, 4));
            found = mask != 0;
        }

        return found;
    }

    /// <summary>Selects a frame-presentation mode, then confirms the driver reports it back.</summary>
    /// <param name="mode">The mode to select.</param>
    /// <returns><see langword="true"/> only when the read-back matches what was asked for.</returns>
    /// <remarks>Issued once and never retried, for the same reason the other writes are not.</remarks>
    public bool TryWriteGamingFlipMode(GamingFlipMode mode)
    {
        if (_adapter == 0)
        {
            return false;
        }

        Ctl3dFeatureGetSet request = default;
        request.Size = (uint)sizeof(Ctl3dFeatureGetSet);
        request.FeatureType = FeatureGamingFlipModes;
        request.ValueType = ValueTypeEnum;
        request.Set = 1;
        request.Value.First = 1;
        request.Value.Second = (uint)mode;

        int result = _getSet3dFeature(_adapter, &request);
        if (result != ResultSuccess)
        {
            PluginTrace.Warn("intel3d", $"Gaming flip mode write failed with 0x{result:x}.");
            return false;
        }

        if (ReadGamingFlipMode() == mode)
        {
            return true;
        }

        PluginTrace.Warn("intel3d", $"Gaming flip mode write to {mode} was not confirmed.");
        return false;
    }

    /// <summary>Reads whether the driver downloads prebuilt shaders for games.</summary>
    /// <returns>The current setting, or null when the driver does not offer it.</returns>
    /// <remarks>
    /// A bool-typed feature, so the value rides in the property union rather than through the custom
    /// pointer. Intel documents the feature that way and the driver answers it that way.
    /// </remarks>
    public bool? ReadShaderDownload()
    {
        if (_adapter == 0)
        {
            return null;
        }

        Ctl3dFeatureGetSet request = default;
        request.Size = (uint)sizeof(Ctl3dFeatureGetSet);
        request.FeatureType = FeaturePrebuiltShaderDownload;
        request.ValueType = ValueTypeBool;

        int result = _getSet3dFeature(_adapter, &request);
        if (result != ResultSuccess)
        {
            PluginTrace.Info("intel3d", $"Shader download read returned 0x{result:x}; unsupported here.");
            return null;
        }

        // ctl_property_boolean_t is a single C bool, so only the union's first byte carries it.
        return (request.Value.First & 0xFF) != 0;
    }

    /// <summary>Turns prebuilt shader download on or off, then confirms the read-back.</summary>
    /// <param name="enabled">Whether the driver should download prebuilt shaders.</param>
    /// <returns><see langword="true"/> only when the driver reports the requested value afterwards.</returns>
    public bool TryWriteShaderDownload(bool enabled)
    {
        if (_adapter == 0)
        {
            return false;
        }

        Ctl3dFeatureGetSet request = default;
        request.Size = (uint)sizeof(Ctl3dFeatureGetSet);
        request.FeatureType = FeaturePrebuiltShaderDownload;
        request.ValueType = ValueTypeBool;
        request.Set = 1;
        request.Value.First = enabled ? 1u : 0u;

        int result = _getSet3dFeature(_adapter, &request);
        if (result != ResultSuccess)
        {
            PluginTrace.Warn("intel3d", $"Shader download write failed with 0x{result:x}.");
            return false;
        }

        if (ReadShaderDownload() == enabled)
        {
            return true;
        }

        PluginTrace.Warn("intel3d", $"Shader download write to {enabled} was not confirmed.");
        return false;
    }

    public void Dispose()
    {
        if (_api != 0)
        {
            _ = _close(_api);
            _api = 0;
        }

        _adapter = 0;
        if (_library != 0)
        {
            NativeLibrary.Free(_library);
            _library = 0;
        }
    }

    private bool TryBind()
    {
        // Addresses first, cast at the end: a function-pointer type cannot be a generic argument,
        // so it cannot be threaded through one shared helper. Same shape as ArcSyncTransport.
        if (!TryGet("ctlInit", out nint init)
            || !TryGet("ctlClose", out nint close)
            || !TryGet("ctlEnumerateDevices", out nint enumerateDevices)
            || !TryGet("ctlGetSet3DFeature", out nint getSet3dFeature))
        {
            return false;
        }

        _init = (delegate* unmanaged[Cdecl]<CtlInitArgs*, nint*, int>)init;
        _close = (delegate* unmanaged[Cdecl]<nint, int>)close;
        _enumerateDevices = (delegate* unmanaged[Cdecl]<nint, uint*, nint*, int>)enumerateDevices;
        _getSet3dFeature = (delegate* unmanaged[Cdecl]<nint, Ctl3dFeatureGetSet*, int>)getSet3dFeature;

        // Optional rather than required: it only narrows the offered flip modes, and a driver
        // without it should still get the feature with Intel's documented set.
        _getSupported3dCapabilities = NativeLibrary.TryGetExport(
            _library, "ctlGetSupported3DCapabilities", out nint capabilities)
            ? (delegate* unmanaged[Cdecl]<nint, Ctl3dFeatureCaps*, int>)capabilities
            : null;
        return true;

        bool TryGet(string name, out nint address)
        {
            if (NativeLibrary.TryGetExport(_library, name, out address))
            {
                return true;
            }

            PluginTrace.Warn("intel3d", $"Entry point {name} is missing.");
            return false;
        }
    }

    /// <remarks>
    /// Two-call enumeration, the same as the display outputs: the count is asked for with a null
    /// buffer and only then fetched. The adapter is chosen by which one answers for the feature
    /// rather than by index, because a machine can enumerate more than one and only the integrated
    /// Intel part carries Endurance Gaming.
    /// </remarks>
    private bool TrySelectAdapter()
    {
        uint count = 0;
        if (_enumerateDevices(_api, &count, null) != ResultSuccess || count == 0)
        {
            PluginTrace.Warn("intel3d", "No graphics adapters enumerated.");
            return false;
        }

        count = Math.Min(count, MaxDevices);
        nint* devices = stackalloc nint[MaxDevices];
        if (_enumerateDevices(_api, &count, devices) != ResultSuccess)
        {
            PluginTrace.Warn("intel3d", "Adapter handles could not be fetched.");
            return false;
        }

        for (uint index = 0; index < count; index++)
        {
            _adapter = devices[index];
            if (Read() is not null)
            {
                return true;
            }
        }

        _adapter = 0;
        PluginTrace.Info("intel3d", $"No adapter of {count} answered for Endurance Gaming.");
        return false;
    }

    /// <summary>ctl_3d_feature_caps_t: the count, then the caller's buffer for the details.</summary>
    /// <remarks>Only the header is asserted. The element layout is derived from the data.</remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct Ctl3dFeatureCaps
    {
        public uint Size;
        public byte Version;
        public uint NumSupportedFeatures;
        public nint FeatureDetails;
    }

    /// <summary>ctl_endurance_gaming_t: two enums, four bytes each.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct EnduranceGaming
    {
        public uint Control;
        public uint Mode;
    }

    /// <summary>
    /// ctl_property_t. A union of the bool/float/int/enum/uint property structs, the largest of
    /// which is one byte plus a four-byte value, so eight bytes at four-byte alignment. Endurance
    /// Gaming travels through the custom pointer instead, so this only has to occupy the right room.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyValue
    {
        public uint First;
        public uint Second;
    }

    /// <summary>ctl_3d_feature_getset_t, field for field from Intel's igcl_api.h.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Ctl3dFeatureGetSet
    {
        public uint Size;
        public byte Version;
        public int FeatureType;
        public nint ApplicationName;
        public sbyte ApplicationNameLength;

        /// <summary>One byte in C, so a managed <c>bool</c> here would be four and shift the rest.</summary>
        public byte Set;
        public int ValueType;
        public PropertyValue Value;
        public int CustomValueSize;
        public nint CustomValue;
    }

    /// <summary>ctl_init_args_t, as <see cref="ArcSyncTransport"/> already carries it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CtlInitArgs
    {
        public uint Size;
        public byte Version;
        public uint AppVersion;
        public uint Flags;
        public uint SupportedVersion;
        public fixed byte ApplicationUid[16];
    }
}
