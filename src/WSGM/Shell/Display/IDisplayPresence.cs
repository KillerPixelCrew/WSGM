using System.ComponentModel;
using WindowsDeviceControl;

namespace WSGM.Shell;

/// <summary>Reads which monitors the adapter can currently see.</summary>
internal interface IDisplayPresence
{
    /// <summary>Observes every monitor. May throw <see cref="Win32Exception" /> when the driver is mid-change.</summary>
    /// <returns>The current observation.</returns>
    DisplayArrangement Observe();
}
