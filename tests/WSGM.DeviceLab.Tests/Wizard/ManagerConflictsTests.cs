using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Tests.Wizard;

public sealed class ManagerConflictsTests
{
    [Fact]
    public void PowerHardwareOwners_PreserveTheExistingBlockingSet()
    {
        string[] expected =
        [
            "WSGM", "HandheldCompanion", "ControllerService", "ArmouryCrate", "ArmouryCrateControlInterface",
            "ArmourySocketServer", "GHelper", "MSI Center", "MSI.CentralServer", "AsusAppService"
        ];

        Assert.Equal(expected.Order(StringComparer.Ordinal), ManagerConflicts.Known
            .Where(manager => manager.OwnsPowerHardware).Select(manager => manager.Process)
            .Order(StringComparer.Ordinal));
    }
}
