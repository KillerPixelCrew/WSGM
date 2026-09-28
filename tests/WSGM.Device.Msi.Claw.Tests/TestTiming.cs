using System.Runtime.CompilerServices;

namespace WSGM.Device.Msi.Claw.Tests;

/// <summary>Removes HC's write spacing so the tests do not sleep through it.</summary>
internal static class TestTiming
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        ClawPowerCapability.WriteSpacing = TimeSpan.Zero;
        ControllerService.McuDelayScale = 0;
    }
}
