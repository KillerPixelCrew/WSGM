using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.DeviceLab.Capture.Live;
using WSGM.DeviceLab.Worker;
using WSGM.Interop;

namespace WSGM.DeviceLab.Transports;

/// <summary>The Claw's persistent RGB profile, following WindowsHidTransports and ClawCapabilities.</summary>
internal interface ILabClawLighting : IDisposable
{
    /// <summary>Reads the exact original 32-byte profile.</summary>
    [LabWorkerSnapshot]
    byte[] Original();

    /// <summary>Writes a complete profile once and verifies it.</summary>
    [LabWorkerWrite]
    bool Apply(byte[] profile);
}

/// <summary>Worker-owned MCU profile transport for the reviewed Claw collection.</summary>
internal sealed class LabClawLighting : ILabClawLighting
{
    private DateTime _lastWrite;

    /// <summary>The worker registration.</summary>
    public static LabWorkerService Service { get; } = new("claw-lighting", typeof(ILabClawLighting),
        (args, _) => LabWorkerService.Arg<string>(args, 0) == "wsgm.claw-8-a2vm"
            ? new LabClawLighting()
            : throw new InvalidOperationException("No Claw lighting profile for this device."));

    /// <inheritdoc />
    public void Dispose()
    {
    }

    /// <inheritdoc />
    public byte[] Original()
    {
        var reply = Exchange(Request(0x04), 0x05);
        if (reply[5] != 1 || reply[6] != 2 || reply[7] != 0x4A || reply[8] != 32)
        {
            throw new InvalidDataException("The Claw RGB profile reply has an unexpected address or length.");
        }
        var profile = reply.AsSpan(9, 32).ToArray();
        Validate(profile);
        return profile;
    }

    /// <inheritdoc />
    public bool Apply(byte[] profile)
    {
        Validate(profile);
        var remaining = TimeSpan.FromSeconds(1) - (DateTime.UtcNow - _lastWrite);
        if (remaining > TimeSpan.Zero)
        {
            Thread.Sleep(remaining);
        }
        var request = Request(0x21);
        profile.CopyTo(request, 9);
        _lastWrite = DateTime.UtcNow;
        Exchange(request, 0x06);
        return Original().SequenceEqual(profile);
    }

    /// <summary>Sets all zones to one test colour while preserving the profile's other bytes.</summary>
    internal static byte[] Colour(byte[] original, byte red, byte green, byte blue)
    {
        Validate(original);
        var profile = (byte[])original.Clone();
        profile[4] = 100;
        for (var offset = 5; offset <= 29; offset += 3)
        {
            profile[offset] = red;
            profile[offset + 1] = green;
            profile[offset + 2] = blue;
        }
        return profile;
    }

    private static void Validate(byte[] profile)
    {
        if (profile.Length != 32 || profile[1] != 1 || profile[2] != 9)
        {
            throw new InvalidDataException("The committed Claw RGB profile has an unrecognized shape.");
        }
    }

    private static byte[] Request(byte command)
    {
        var request = new byte[64];
        request[0] = 0x0F;
        request[3] = 0x3C;
        request[4] = command;
        request[5] = 1;
        request[6] = 2;
        request[7] = 0x4A;
        request[8] = 32;
        return request;
    }

    private static byte[] Exchange(byte[] request, byte replyCommand)
    {
        var endpoint = LabRumbleNative.HidEndpoints(0x0DB0).SingleOrDefault(item =>
            item.OutputLength == 64 &&
            ((item.ProductId == 0x1902 && item.UsagePage == 0xFFF0 && item.Usage == 0x0040)
             || (item.ProductId == 0x1901 && item.UsagePage == 0xFFA0 && item.Usage == 1)))
            ?? throw new IOException("The Claw MCU lighting collection was not found.");
        using var handle = Kernel32.CreateFileW(endpoint.Path, Kernel32.GenericRead | Kernel32.GenericWrite,
            Kernel32.FileShareRead | Kernel32.FileShareWrite, 0, Kernel32.OpenExisting, 0x40000000, 0);
        if (handle.IsInvalid)
        {
            throw new IOException("The Claw MCU lighting collection could not be opened.");
        }
        using var stream = new FileStream(handle, FileAccess.ReadWrite, 64, true);
        return ExchangeAsync(stream, request, replyCommand).GetAwaiter().GetResult();
    }

    private static async Task<byte[]> ExchangeAsync(FileStream stream, byte[] request, byte replyCommand)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await stream.WriteAsync(request, timeout.Token).ConfigureAwait(false);
        var reply = new byte[64];
        while (true)
        {
            await stream.ReadExactlyAsync(reply, timeout.Token).ConfigureAwait(false);
            if (reply[0] == 0x10 && reply[4] == replyCommand
                && (replyCommand != 0x05 || (reply[5] == 1 && reply[6] == 2 && reply[7] == 0x4A)))
            {
                return reply;
            }
        }
    }
}
