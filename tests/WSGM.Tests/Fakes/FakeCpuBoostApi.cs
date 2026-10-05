using System.ComponentModel;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Tests.Fakes;

/// <summary>
///     An in-memory processor boost policy API that records reads, writes, reveals and refreshes. It is also
///     the scheme port, so the refresh the scheme owner makes lands in the same call log.
/// </summary>
internal sealed class FakeCpuBoostApi : ICpuBoostApi, IPowerSchemeApi
{
    private static readonly Guid Scheme = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    /// <summary>When false, every read is refused, as a scheme without the setting refuses it.</summary>
    internal bool Readable { get; init; } = true;

    internal bool IgnoreWrites { get; init; }

    internal Dictionary<bool, uint> Values { get; } = new() { [false] = 1, [true] = 1 };

    internal List<string> Calls { get; } = [];

    public Guid ReadActive()
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

    public Guid? Enumerate(uint index)
    {
        return index == 0 ? Scheme : null;
    }

    public string ReadName(Guid id)
    {
        return "Balanced";
    }

    /// <summary>The scheme owner's refresh re-activates the active scheme; that is the only activation here.</summary>
    public void SetActive(Guid id)
    {
        Calls.Add("refresh");
    }

    public uint ReadSetting(Guid scheme, Guid subgroup, Guid setting, bool onBattery)
    {
        throw new InvalidOperationException("Unexpected power setting read");
    }

    public void WriteSetting(Guid scheme, Guid subgroup, Guid setting, bool onBattery, uint value)
    {
        throw new InvalidOperationException("Unexpected power setting write");
    }

    /// <summary>A boost owner over this fake as both its scheme and its boost port.</summary>
    internal CpuBoost Owner()
    {
        return new CpuBoost(new PowerSchemes(this), this);
    }
}
