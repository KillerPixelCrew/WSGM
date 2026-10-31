using WSGM.Interop;

namespace WSGM.Tests.Fakes;

internal sealed class FakePowerModeApi : IPowerModeApi
{
    internal Action? AfterRead;
    internal Action? AfterWrite;
    internal bool FailRead;
    internal bool FailWrite;
    internal bool IgnoreWrite;
    internal Guid Mode = Guid.Empty;
    internal int Writes;

    public Guid Read()
    {
        if (FailRead)
        {
            throw new InvalidOperationException("Windows read failed.");
        }

        AfterRead?.Invoke();
        return Mode;
    }

    public void Set(Guid mode)
    {
        Writes++;
        if (FailWrite)
        {
            throw new InvalidOperationException("Windows refused the mode.");
        }

        if (!IgnoreWrite)
        {
            Mode = mode;
        }

        AfterWrite?.Invoke();
    }
}
