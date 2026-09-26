using System.ComponentModel;
using WSGM.Interop;

namespace WSGM.Tests.Fakes;

/// <summary>An in-memory processor boost policy API that records reads, writes, reveals and refreshes.</summary>
internal sealed class FakeCpuBoostApi : ICpuBoostApi
{
    private static readonly Guid Scheme = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    /// <summary>When false, every read is refused, as a scheme without the setting refuses it.</summary>
    internal bool Readable { get; init; } = true;

    internal bool IgnoreWrites { get; init; }

    internal Dictionary<bool, uint> Values { get; } = new() { [false] = 1, [true] = 1 };

    internal List<string> Calls { get; } = [];

    public Guid ReadActiveScheme()
    {
        return Scheme;
    }

    public uint Read(Guid scheme, bool onBattery)
    {
        Calls.Add("read");
        if (!Readable)
        {
            throw new Win32Exception(2, "The setting does not exist.");
        }

        return Values[onBattery];
    }

    public void Write(Guid scheme, bool onBattery, uint value)
    {
        Calls.Add($"write {(onBattery ? "dc" : "ac")} {value}");
        if (!IgnoreWrites)
        {
            Values[onBattery] = value;
        }
    }

    public bool TryReveal()
    {
        Calls.Add("reveal");
        return true;
    }

    public void RefreshActiveScheme()
    {
        Calls.Add("refresh");
    }
}
