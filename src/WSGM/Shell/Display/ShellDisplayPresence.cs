using WindowsDeviceControl;

namespace WSGM.Shell;

/// <summary>Reads the live display topology.</summary>
internal sealed class ShellDisplayPresence : IDisplayPresence
{
    /// <inheritdoc />
    public DisplayArrangement Observe()
    {
        return DisplayLayouts.Observe();
    }
}
