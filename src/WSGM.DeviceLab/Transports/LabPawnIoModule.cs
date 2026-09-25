using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using WSGM.DeviceLab.Application;

namespace WSGM.DeviceLab.Transports;

/// <summary>
///     One PawnIO module loaded from a pinned, digest-checked copy: the driver's two calls, load and
///     execute, and nothing else.
/// </summary>
/// <remarks>
///     PawnIO checks the module's own signature when it loads it; this class checks the digest against
///     <c>external/pawnio/pawnio.lock.json</c> first, so only the pinned release ever reaches the driver.
///     A module handle is closed on dispose, which unloads it.
/// </remarks>
internal sealed class LabPawnIoModule : IDisposable
{
    /// <summary>Embedded resource holding the pinned RyzenSMU module.</summary>
    public const string RyzenSmuResource = "WSGM.DeviceLab.PawnIO.RyzenSMU.bin";

    private const string DevicePath = @"\\?\GLOBALROOT\Device\PawnIO";
    private const int FunctionNameLength = 32;
    private const uint DeviceType = 41394u << 16;
    private const uint LoadBinary = DeviceType | (0x821 << 2);
    private const uint ExecuteFunction = DeviceType | (0x841 << 2);

    private readonly SafeFileHandle _handle;

    private LabPawnIoModule(SafeFileHandle handle)
    {
        _handle = handle;
    }

    /// <summary>Whether this build carries the pinned RyzenSMU module.</summary>
    public static bool RyzenSmuBundled =>
        typeof(LabPawnIoModule).Assembly.GetManifestResourceInfo(RyzenSmuResource) is not null;

    /// <inheritdoc />
    public void Dispose()
    {
        _handle.Dispose();
    }

    /// <summary>Loads the pinned RyzenSMU module.</summary>
    /// <returns>The loaded module.</returns>
    /// <exception cref="InvalidOperationException">The module is missing, does not match its pin, or PawnIO refused it.</exception>
    public static LabPawnIoModule LoadRyzenSmu()
    {
        using var stream = typeof(LabPawnIoModule).Assembly.GetManifestResourceStream(RyzenSmuResource)
                           ?? throw new InvalidOperationException("This build does not include the RyzenSMU module.");
        using MemoryStream memory = new();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        var digest = Convert.ToHexString(SHA256.HashData(bytes));
        var pinned = PinnedDigest("RyzenSMU");
        if (!string.Equals(digest, pinned, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The bundled RyzenSMU module does not match its pin ({digest}).");
        }

        return Load(bytes);
    }

    /// <summary>Runs one module function.</summary>
    /// <param name="name">Function name, for example <c>ioctl_get_code_name</c>.</param>
    /// <param name="input">Input words.</param>
    /// <param name="outputLength">Output words expected.</param>
    /// <returns>The output words the module returned.</returns>
    /// <exception cref="Win32Exception">The call failed.</exception>
    public long[] Execute(string name, long[] input, int outputLength)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(input);
        if (name.Length >= FunctionNameLength)
        {
            throw new ArgumentException("The function name is too long.", nameof(name));
        }

        var request = new byte[FunctionNameLength + input.Length * sizeof(long)];
        Encoding.ASCII.GetBytes(name, request);
        Buffer.BlockCopy(input, 0, request, FunctionNameLength, input.Length * sizeof(long));
        var response = new byte[Math.Max(outputLength, 0) * sizeof(long)];
        LabTrace.Write($"pawnio {name}: call");
        if (!DeviceIoControl(_handle, ExecuteFunction, request, (uint)request.Length, response,
                (uint)response.Length, out var returned, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            LabTrace.Write($"pawnio {name}: failed, error {error}");
            throw new Win32Exception(error, $"PawnIO {name} failed.");
        }

        LabTrace.Write($"pawnio {name}: returned");

        var words = new long[returned / sizeof(long)];
        Buffer.BlockCopy(response, 0, words, 0, words.Length * sizeof(long));
        return words;
    }

    private static LabPawnIoModule Load(byte[] module)
    {
        LabTrace.Write($"pawnio load module: {module.Length} bytes");
        var handle = CreateFileW(DevicePath, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new InvalidOperationException($"The PawnIO driver could not be opened (error {error}).");
        }

        if (!DeviceIoControl(handle, LoadBinary, module, (uint)module.Length, [], 0, out _, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new InvalidOperationException($"PawnIO refused the module (error {error}).");
        }

        return new LabPawnIoModule(handle);
    }

    private static string PinnedDigest(string id)
    {
        using var stream = typeof(LabPawnIoModule).Assembly.GetManifestResourceStream("WSGM.DeviceLab.PawnIO.lock.json")
                           ?? throw new InvalidOperationException("The PawnIO lock file is not embedded.");
        using var document = JsonDocument.Parse(stream);
        foreach (var module in document.RootElement.GetProperty("modules").EnumerateArray())
        {
            if (module.GetProperty("id").GetString() == id)
            {
                return module.GetProperty("memberSha256").GetString()!;
            }
        }

        throw new InvalidOperationException($"The PawnIO lock file has no {id} module.");
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, uint inputLength,
        byte[] output, uint outputLength, out uint returned, IntPtr overlapped);
}
