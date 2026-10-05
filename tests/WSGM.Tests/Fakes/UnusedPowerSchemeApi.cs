using WSGM.Interop;

namespace WSGM.Tests.Fakes;

/// <summary>
///     A scheme port for an owner that only takes the scheme owner's mutation lock: every call into Windows
///     fails the test.
/// </summary>
internal sealed class UnusedPowerSchemeApi : IPowerSchemeApi
{
    public Guid? Enumerate(uint index)
    {
        throw new InvalidOperationException("Unexpected scheme enumeration");
    }

    public string ReadName(Guid id)
    {
        throw new InvalidOperationException("Unexpected scheme name read");
    }

    public Guid ReadActive()
    {
        throw new InvalidOperationException("Unexpected active scheme read");
    }

    public void SetActive(Guid id)
    {
        throw new InvalidOperationException("Unexpected scheme activation");
    }

    public uint ReadSetting(Guid scheme, Guid subgroup, Guid setting, bool onBattery)
    {
        throw new InvalidOperationException("Unexpected power setting read");
    }

    public void WriteSetting(Guid scheme, Guid subgroup, Guid setting, bool onBattery, uint value)
    {
        throw new InvalidOperationException("Unexpected power setting write");
    }
}
