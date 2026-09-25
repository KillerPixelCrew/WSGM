// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Device.Asus.RogAlly;

/// <summary>One HID top-level collection, described without keeping it open.</summary>
internal sealed record AllyHidEndpoint
{
    public required string DevicePath { get; init; }

    public required string InstancePath { get; init; }

    public required ushort VendorId { get; init; }

    public required ushort ProductId { get; init; }

    public required ushort UsagePage { get; init; }

    public required ushort Usage { get; init; }

    public required ushort InputLength { get; init; }

    public required ushort OutputLength { get; init; }

    public required ushort FeatureLength { get; init; }

    public required string PhysicalLocation { get; init; }

    public string Describe()
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"{ProductId:X4}/{UsagePage:X4}:{Usage:X4} in{InputLength} out{OutputLength} feat{FeatureLength}");
    }
}

/// <summary>A present device node, for the HidHide transaction WSGM performs.</summary>
internal sealed record AllyDeviceNode(string InstancePath, Guid ClassGuid, string PhysicalLocation);

/// <summary>Enumerates the ASUS controller MCU's HID collections and device nodes.</summary>
internal static class AllyHidEnumerator
{
    /// <summary><c>GUID_DEVCLASS_HIDCLASS</c>.</summary>
    public static readonly Guid HidClass = new("745A17A0-74D3-11D0-B6FE-00A0C90F57DA");

    /// <summary><c>GUID_DEVCLASS_XNACOMPOSITE</c>, the XUSB (Xbox 360 protocol) controller class.</summary>
    public static readonly Guid XnaCompositeClass = new("D61CA365-5AF4-4486-998B-9DB4734C6CA3");

    /// <summary><c>GUID_DEVCLASS_XBOXCOMPOSITE</c>, the GIP (Xbox One protocol) controller class.</summary>
    public static readonly Guid XboxCompositeClass = new("05F5CFE2-4733-4950-A6BB-07AAD01A3A84");

    private static readonly NativeHid.DevPropKey LocationPathsKey = new()
    {
        FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        PropertyId = 37
    };

    public static IReadOnlyList<AllyHidEndpoint> Enumerate(IReadOnlyCollection<ushort> productIds)
    {
        NativeHid.HidD_GetHidGuid(out var hidGuid);
        var set = NativeHid.SetupDiGetClassDevs(ref hidGuid, null, 0,
            NativeHid.DIGCF_PRESENT | NativeHid.DIGCF_DEVICEINTERFACE);
        if (set == NativeHid.InvalidHandleValue)
        {
            return [];
        }

        List<AllyHidEndpoint> endpoints = [];
        try
        {
            for (uint index = 0; index < 1024; index++)
            {
                NativeHid.DeviceInterfaceData interfaceData = new()
                {
                    Size = (uint)Marshal.SizeOf<NativeHid.DeviceInterfaceData>()
                };
                if (!NativeHid.SetupDiEnumDeviceInterfaces(set, 0, ref hidGuid, index, ref interfaceData))
                {
                    if (Marshal.GetLastWin32Error() == NativeHid.ERROR_NO_MORE_ITEMS)
                    {
                        break;
                    }

                    continue;
                }

                _ = NativeHid.SetupDiGetDeviceInterfaceDetail(set, ref interfaceData, 0, 0, out var required, 0);
                if (required is 0 or > 64 * 1024)
                {
                    continue;
                }

                var detail = Marshal.AllocHGlobal(checked((int)required));
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    NativeHid.DeviceInfoData info = new() { Size = (uint)Marshal.SizeOf<NativeHid.DeviceInfoData>() };
                    if (!NativeHid.SetupDiGetDeviceInterfaceDetail(set, ref interfaceData, detail, required, out _,
                            ref info))
                    {
                        continue;
                    }

                    var path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                    if (path is not null && IsAsusPath(path, productIds)
                                         && TryDescribe(path, set, info, productIds, out var endpoint))
                    {
                        endpoints.Add(endpoint!);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            _ = NativeHid.SetupDiDestroyDeviceInfoList(set);
        }

        return endpoints;
    }

    /// <summary>Every present device node of the controller that WSGM must hide while it owns it.</summary>
    /// <remarks>
    ///     The XInput pad is an XUSB node (HC's XUSB arrival handler matches VID 0B05 with PID 1ABE or
    ///     1B4C, <c>ControllerManager.cs:2236-2243</c>) and possibly a GIP node on the Xbox models, plus
    ///     the XInput-compatible HID collection whose instance path carries <c>&amp;IG_</c>. The vendor,
    ///     keyboard and lighting collections are not hidden: the plugin itself reads them.
    /// </remarks>
    public static IReadOnlyList<AllyDeviceNode> ControllerNodes(IReadOnlyCollection<ushort> productIds)
    {
        var set = NativeHid.SetupDiGetClassDevsAll(0, null, 0, NativeHid.DIGCF_PRESENT | NativeHid.DIGCF_ALLCLASSES);
        if (set == NativeHid.InvalidHandleValue)
        {
            return [];
        }

        List<AllyDeviceNode> nodes = [];
        try
        {
            for (uint index = 0; index < 4096; index++)
            {
                NativeHid.DeviceInfoData info = new() { Size = (uint)Marshal.SizeOf<NativeHid.DeviceInfoData>() };
                if (!NativeHid.SetupDiEnumDeviceInfo(set, index, ref info))
                {
                    if (Marshal.GetLastWin32Error() == NativeHid.ERROR_NO_MORE_ITEMS)
                    {
                        break;
                    }

                    continue;
                }

                var instance = ReadInstancePath(set, info);
                if (!IsAsusPath(instance, productIds))
                {
                    continue;
                }

                var controllerClass = info.ClassGuid == XnaCompositeClass || info.ClassGuid == XboxCompositeClass;
                var xinputHid = info.ClassGuid == HidClass
                                && instance.Contains("&IG_", StringComparison.OrdinalIgnoreCase);
                if (controllerClass || xinputHid)
                {
                    nodes.Add(new AllyDeviceNode(instance, info.ClassGuid, ReadPhysicalLocation(info.DeviceInstance)));
                }
            }
        }
        finally
        {
            _ = NativeHid.SetupDiDestroyDeviceInfoList(set);
        }

        return nodes;
    }

    public static bool IsAsusPath(string path, IReadOnlyCollection<ushort> productIds)
    {
        return productIds.Any(pid => path.Contains(
            string.Create(CultureInfo.InvariantCulture, $"VID_{AllyModels.AsusVendorId:X4}&PID_{pid:X4}"),
            StringComparison.OrdinalIgnoreCase));
    }

    public static SafeFileHandle Open(AllyHidEndpoint endpoint, bool overlapped)
    {
        var handle = NativeHid.CreateFile(endpoint.DevicePath, NativeHid.GENERIC_READ | NativeHid.GENERIC_WRITE,
            NativeHid.FILE_SHARE_READ | NativeHid.FILE_SHARE_WRITE, 0, NativeHid.OPEN_EXISTING,
            overlapped ? NativeHid.FILE_FLAG_OVERLAPPED : 0, 0);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error, $"The HID collection {endpoint.Describe()} could not be opened.");
    }

    /// <summary>Whether the collection answers a feature read for one report ID.</summary>
    /// <remarks>
    ///     HC finds the Aura collection this way: the one whose feature report 0x5D reads
    ///     (<c>ROGAlly.cs:422-436</c>). A feature read changes nothing on the device.
    /// </remarks>
    public static bool AnswersFeature(AllyHidEndpoint endpoint, byte reportId)
    {
        if (endpoint.FeatureLength < 2)
        {
            return false;
        }

        try
        {
            using var handle = Open(endpoint, false);
            var buffer = new byte[endpoint.FeatureLength];
            buffer[0] = reportId;
            return NativeHid.HidD_GetFeature(handle, buffer, buffer.Length);
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Sends one feature report, padded to the collection's declared length.</summary>
    public static void SetFeature(SafeFileHandle handle, AllyHidEndpoint endpoint, ReadOnlySpan<byte> report)
    {
        if (report.Length == 0 || report.Length > endpoint.FeatureLength)
        {
            throw new InvalidOperationException(
                $"A {report.Length}-byte feature report does not fit {endpoint.Describe()}.");
        }

        var buffer = new byte[endpoint.FeatureLength];
        report.CopyTo(buffer);
        if (!NativeHid.HidD_SetFeature(handle, buffer, buffer.Length))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "HidD_SetFeature failed; effect unknown.");
        }
    }

    /// <summary>Sends one output report, padded to the collection's declared length.</summary>
    public static void WriteOutput(SafeFileHandle handle, AllyHidEndpoint endpoint, ReadOnlySpan<byte> report)
    {
        if (report.Length == 0 || report.Length > endpoint.OutputLength)
        {
            throw new InvalidOperationException(
                $"A {report.Length}-byte output report does not fit {endpoint.Describe()}.");
        }

        var buffer = new byte[endpoint.OutputLength];
        report.CopyTo(buffer);
        if (!NativeHid.WriteFile(handle, buffer, (uint)buffer.Length, out var written, 0) || written != buffer.Length)
        {
            throw new IOException("The HID output report failed or was short; effect unknown.");
        }
    }

    private static bool TryDescribe(
        string path,
        nint set,
        NativeHid.DeviceInfoData info,
        IReadOnlyCollection<ushort> productIds,
        out AllyHidEndpoint? endpoint)
    {
        endpoint = null;
        using var handle = NativeHid.CreateFile(path, 0, NativeHid.FILE_SHARE_READ | NativeHid.FILE_SHARE_WRITE, 0,
            NativeHid.OPEN_EXISTING, 0, 0);
        if (handle.IsInvalid)
        {
            return false;
        }

        NativeHid.HidAttributes attributes = new() { Size = Marshal.SizeOf<NativeHid.HidAttributes>() };
        if (!NativeHid.HidD_GetAttributes(handle, ref attributes)
            || attributes.VendorId != AllyModels.AsusVendorId
            || !productIds.Contains(attributes.ProductId)
            || !NativeHid.HidD_GetPreparsedData(handle, out var preparsed))
        {
            return false;
        }

        NativeHid.HidCaps caps;
        try
        {
            if (NativeHid.HidP_GetCaps(preparsed, out caps) != NativeHid.HIDP_STATUS_SUCCESS)
            {
                return false;
            }
        }
        finally
        {
            _ = NativeHid.HidD_FreePreparsedData(preparsed);
        }

        endpoint = new AllyHidEndpoint
        {
            DevicePath = path,
            InstancePath = ReadInstancePath(set, info),
            VendorId = attributes.VendorId,
            ProductId = attributes.ProductId,
            UsagePage = caps.UsagePage,
            Usage = caps.Usage,
            InputLength = caps.InputReportByteLength,
            OutputLength = caps.OutputReportByteLength,
            FeatureLength = caps.FeatureReportByteLength,
            PhysicalLocation = ReadPhysicalLocation(info.DeviceInstance)
        };
        return true;
    }

    private static string ReadInstancePath(nint set, NativeHid.DeviceInfoData info)
    {
        var buffer = new StringBuilder(1024);
        return NativeHid.SetupDiGetDeviceInstanceId(set, ref info, buffer, buffer.Capacity, out _)
            ? buffer.ToString()
            : string.Empty;
    }

    private static string ReadPhysicalLocation(uint deviceInstance)
    {
        var current = deviceInstance;
        var key = LocationPathsKey;
        var buffer = new byte[4096];
        for (var depth = 0; depth < 6; depth++)
        {
            var length = checked((uint)buffer.Length);
            if (NativeHid.CM_Get_Device_Property(current, ref key, out _, buffer, ref length, 0) == 0)
            {
                var value = Encoding.Unicode.GetString(buffer, 0, checked((int)length)).TrimEnd('\0');
                var terminator = value.IndexOf('\0');
                if (terminator >= 0)
                {
                    value = value[..terminator];
                }

                if (!string.IsNullOrWhiteSpace(value))
                {
                    var interfaceComponent = value.IndexOf("#USBMI(", StringComparison.OrdinalIgnoreCase);
                    return interfaceComponent < 0 ? value : value[..interfaceComponent];
                }
            }

            if (NativeHid.CM_Get_Parent(out current, current, 0) != 0)
            {
                break;
            }
        }

        return string.Empty;
    }
}

/// <summary>The MCU's vendor collection: OEM button reports in, controller configuration out.</summary>
internal interface IAllyVendorHid : IAsyncDisposable
{
    /// <summary>Finds the collection. False when this model exposes none.</summary>
    ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken);

    /// <summary>Starts reading 0x5A input reports; the callback receives the event code byte.</summary>
    /// <remarks>The fault callback runs once if the reader stops on its own, such as when the MCU re-enumerates.</remarks>
    ValueTask<bool> StartAsync(
        Func<byte, DateTimeOffset, ValueTask> callback,
        Action<Exception> fault,
        CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);

    /// <summary>Sends one 0x5A configuration feature report, as HC's <c>ConfigureController</c> does.</summary>
    ValueTask WriteConfigurationAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken);
}

/// <summary>The Aura lighting collection HC drives with report 0x5D.</summary>
internal interface IAllyAuraHid : IAsyncDisposable
{
    ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken);

    ValueTask SetFeatureAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken);

    ValueTask WriteOutputAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken);

    /// <summary>Puts the Windows Dynamic Lighting lamp array in autonomous mode, as HHD does.</summary>
    ValueTask<bool> DisableDynamicLightingAsync(CancellationToken cancellationToken);
}

/// <summary>Windows transport for the vendor collection, usage page 0xFF31, usage 0x0080.</summary>
/// <remarks>
///     HC selects the collection whose feature report 0x5A reads (<c>ROGAlly.cs:422-436</c>) and HHD selects
///     by usage (<c>rog_ally/base.py:383-393</c>); HC's probe leads and usage breaks ties. Configuration uses
///     feature reports as HC does (<c>ROGAlly.cs:646-668</c>), because the Device Lab run found no output
///     report on this collection on RC73XA.
/// </remarks>
internal sealed class WindowsAllyVendorHid(IReadOnlyCollection<ushort> productIds) : IAllyVendorHid
{
    private readonly Lock _gate = new();
    private readonly IReadOnlyCollection<ushort> _productIds = productIds;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private AllyHidEndpoint? _endpoint;
    private CancellationTokenSource? _readCancellation;
    private Task? _reader;
    private FileStream? _stream;

    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Find() is not null);
    }

    public ValueTask<bool> StartAsync(
        Func<byte, DateTimeOffset, ValueTask> callback,
        Action<Exception> fault,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentNullException.ThrowIfNull(fault);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_reader is not null)
            {
                return ValueTask.FromResult(true);
            }

            var endpoint = Find();
            if (endpoint is null || endpoint.InputLength < 2)
            {
                return ValueTask.FromResult(false);
            }

            _stream = new FileStream(AllyHidEnumerator.Open(endpoint, true), FileAccess.ReadWrite, 4096, true);
            _readCancellation = new CancellationTokenSource();
            _reader = ReadLoopAsync(_stream, endpoint.InputLength, callback, fault, _readCancellation.Token);
            return ValueTask.FromResult(true);
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Task? reader;
        lock (_gate)
        {
            reader = _reader;
            _readCancellation?.Cancel();
            _stream?.Dispose();
            _stream = null;
            _reader = null;
        }

        if (reader is not null)
        {
            try
            {
                await reader.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // Closing the handle is the hard stop for a pending read.
            }
        }

        lock (_gate)
        {
            _readCancellation?.Dispose();
            _readCancellation = null;
            // The collection can re-enumerate across suspend; the next start finds it again.
            _endpoint = null;
        }
    }

    public async ValueTask WriteConfigurationAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken)
    {
        if (report.Length == 0 || report.Span[0] != AllyProtocol.VendorReportId)
        {
            throw new InvalidOperationException("Only report 0x5A configuration is written to the vendor collection.");
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var endpoint = Find() ?? throw new FileNotFoundException("The ASUS vendor collection is not present.");
            try
            {
                using var handle = AllyHidEnumerator.Open(endpoint, false);
                AllyHidEnumerator.SetFeature(handle, endpoint, report.Span);
            }
            catch (Win32Exception)
            {
                // Not retried: the next caller rediscovers the collection, this write stays failed.
                lock (_gate)
                {
                    _endpoint = null;
                }

                throw;
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>The vendor collection, chosen as HC chooses it: the one whose feature report 0x5A reads.</summary>
    /// <remarks>
    ///     HC reads events from and writes the tables to that one collection (<c>ROGAlly.cs:416-436</c>). The
    ///     FF31:0080 usage collection is preferred when it answers, then any other that does, and the usage
    ///     collection is the last resort: the Xbox Ally X refused the tables on FF31:0080.
    /// </remarks>
    private AllyHidEndpoint? Find()
    {
        lock (_gate)
        {
            if (_endpoint is not null)
            {
                return _endpoint;
            }

            var endpoints = AllyHidEnumerator.Enumerate(_productIds);
            var answering = endpoints
                .Where(endpoint => endpoint.FeatureLength >= AllyProtocol.ConfigurationLength
                                   && AllyHidEnumerator.AnswersFeature(endpoint, AllyProtocol.VendorReportId))
                .ToArray();
            _endpoint = answering.FirstOrDefault(IsVendorUsage)
                        ?? answering.LastOrDefault()
                        ?? endpoints.FirstOrDefault(IsVendorUsage);
            if (_endpoint is not null)
            {
                PluginTrace.Info("vendor-hid",
                    $"vendor collection {_endpoint.Describe()} (answering 0x5A: "
                    + $"{string.Join(", ", answering.Select(endpoint => endpoint.Describe()))}).");
            }

            return _endpoint;
        }
    }

    private static bool IsVendorUsage(AllyHidEndpoint endpoint)
    {
        return endpoint is { UsagePage: AllyProtocol.VendorUsagePage, Usage: AllyProtocol.VendorUsage };
    }

    private static async Task ReadLoopAsync(
        FileStream stream,
        int reportLength,
        Func<byte, DateTimeOffset, ValueTask> callback,
        Action<Exception> fault,
        CancellationToken cancellationToken)
    {
        var report = new byte[reportLength];
        while (!cancellationToken.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(report, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (IOException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // HC treats a failed read as the device being removed (ROGAlly.cs:364-378).
                PluginTrace.Failure("vendor-hid", "vendor collection read failed", ex);
                fault(ex);
                return;
            }

            if (read == 0)
            {
                PluginTrace.Warn("vendor-hid", "the vendor collection closed.");
                if (!cancellationToken.IsCancellationRequested)
                {
                    fault(new IOException("The ASUS vendor collection closed."));
                }

                return;
            }

            if (AllyProtocol.TryReadVendorEvent(report.AsSpan(0, read), out var code))
            {
                try
                {
                    await callback(code, DateTimeOffset.UtcNow).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    PluginTrace.Failure("vendor-hid", "OEM event publication failed", ex);
                }
            }
        }
    }
}

/// <summary>Windows transport for the Aura collection and, on the Xbox models, the lamp array.</summary>
internal sealed class WindowsAllyAuraHid(IReadOnlyCollection<ushort> productIds) : IAllyAuraHid
{
    private readonly Lock _gate = new();
    private readonly IReadOnlyCollection<ushort> _productIds = productIds;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private AllyHidEndpoint? _aura;
    private bool _searched;

    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Find() is not null);
    }

    public ValueTask SetFeatureAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken)
    {
        return WriteAsync(report, true, cancellationToken);
    }

    public ValueTask WriteOutputAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken)
    {
        return WriteAsync(report, false, cancellationToken);
    }

    public async ValueTask<bool> DisableDynamicLightingAsync(CancellationToken cancellationToken)
    {
        // HHD writes 06 01 to the lamp array (application 0x00590001) on wake so Windows Dynamic
        // Lighting stops overriding Aura (rog_ally/base.py:482-505). Report 6 is the HID Lighting and
        // Illumination LampArrayControlReport, whose first field is AutonomousMode. The specification
        // makes it a Feature report and the RC73XA lab run saw only feature reports on page 0x59, while
        // HHD's hidraw write is an output report. One report is sent: as a feature when the collection
        // has one, otherwise as output.
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var lamp = AllyHidEnumerator.Enumerate(_productIds)
                .Where(endpoint => endpoint is { UsagePage: 0x0059, Usage: 0x0001 })
                .FirstOrDefault(endpoint => endpoint.FeatureLength >= 2 || endpoint.OutputLength >= 2);
            if (lamp is null)
            {
                return false;
            }

            using var handle = AllyHidEnumerator.Open(lamp, false);
            if (lamp.FeatureLength >= 2)
            {
                AllyHidEnumerator.SetFeature(handle, lamp, [0x06, 0x01]);
            }
            else
            {
                AllyHidEnumerator.WriteOutput(handle, lamp, [0x06, 0x01]);
            }

            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private async ValueTask WriteAsync(ReadOnlyMemory<byte> report, bool feature, CancellationToken cancellationToken)
    {
        if (report.Length == 0 || report.Span[0] != AllyProtocol.AuraReportId)
        {
            throw new InvalidOperationException("Only report 0x5D is written to the Aura collection.");
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var endpoint = Find() ?? throw new FileNotFoundException("The Aura collection is not present.");
            try
            {
                using var handle = AllyHidEnumerator.Open(endpoint, false);
                if (feature)
                {
                    AllyHidEnumerator.SetFeature(handle, endpoint, report.Span);
                }
                else
                {
                    AllyHidEnumerator.WriteOutput(handle, endpoint, report.Span);
                }
            }
            catch (Exception ex) when (ex is Win32Exception or IOException)
            {
                // Not retried: the next command searches for the collection again.
                lock (_gate)
                {
                    _searched = false;
                }

                throw;
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private AllyHidEndpoint? Find()
    {
        lock (_gate)
        {
            if (_searched)
            {
                return _aura;
            }

            _searched = true;
            // HC's IsReady checks 0x5A first and 0x5D only on a collection that did not answer 0x5A.
            _aura = AllyHidEnumerator.Enumerate(_productIds).FirstOrDefault(endpoint =>
                endpoint.OutputLength > 0
                && !AllyHidEnumerator.AnswersFeature(endpoint, AllyProtocol.VendorReportId)
                && AllyHidEnumerator.AnswersFeature(endpoint, AllyProtocol.AuraReportId));
            PluginTrace.Info("lighting", _aura is null
                ? "no collection answered Aura report 0x5D."
                : $"Aura collection {_aura.Describe()}.");
            return _aura;
        }
    }
}

internal static partial class NativeHid
{
    public const uint DIGCF_PRESENT = 0x00000002;
    public const uint DIGCF_ALLCLASSES = 0x00000004;
    public const uint DIGCF_DEVICEINTERFACE = 0x00000010;
    public const int ERROR_NO_MORE_ITEMS = 259;
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    public const int HIDP_STATUS_SUCCESS = 0x00110000;
    public static readonly nint InvalidHandleValue = new(-1);

    [LibraryImport("hid.dll")]
    public static partial void HidD_GetHidGuid(out Guid hidGuid);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool HidD_GetAttributes(SafeFileHandle device, ref HidAttributes attributes);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool HidD_GetPreparsedData(SafeFileHandle device, out nint preparsedData);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool HidD_FreePreparsedData(nint preparsedData);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool HidD_SetFeature(SafeFileHandle device, [In] byte[] buffer, int length);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool HidD_GetFeature(SafeFileHandle device, [In] [Out] byte[] buffer, int length);

    [DllImport("hid.dll")]
    public static extern int HidP_GetCaps(nint preparsedData, out HidCaps capabilities);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WriteFile(SafeFileHandle file, [In] byte[] buffer, uint length, out uint written,
        nint overlapped);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    public static partial nint SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, nint parent, uint flags);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    public static partial nint SetupDiGetClassDevsAll(nint classGuid, string? enumerator, nint parent, uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiEnumDeviceInfo(nint deviceInfoSet, uint memberIndex, ref DeviceInfoData data);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiEnumDeviceInterfaces(
        nint deviceInfoSet,
        nint deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref DeviceInterfaceData deviceInterfaceData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiGetDeviceInterfaceDetail(
        nint deviceInfoSet,
        ref DeviceInterfaceData deviceInterfaceData,
        nint detailData,
        uint detailDataSize,
        out uint requiredSize,
        nint deviceInfoData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiGetDeviceInterfaceDetail(
        nint deviceInfoSet,
        ref DeviceInterfaceData deviceInterfaceData,
        nint detailData,
        uint detailDataSize,
        out uint requiredSize,
        ref DeviceInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceInstanceId(
        nint deviceInfoSet,
        ref DeviceInfoData deviceInfoData,
        StringBuilder instanceId,
        int instanceIdSize,
        out int requiredSize);

    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [LibraryImport("cfgmgr32.dll")]
    public static partial int CM_Get_Parent(out uint parent, uint deviceInstance, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    public static partial int CM_Get_Device_Property(
        uint deviceInstance,
        ref DevPropKey propertyKey,
        out uint propertyType,
        [Out] byte[] buffer,
        ref uint bufferLength,
        uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    public static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceInterfaceData
    {
        public uint Size;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DeviceInstance;
        public nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HidAttributes
    {
        public int Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HidCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;

        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DevPropKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }
}
