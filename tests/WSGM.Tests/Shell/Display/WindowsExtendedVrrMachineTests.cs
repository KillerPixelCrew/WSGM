using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class WindowsExtendedVrrMachineTests
{
    /// The standalone script writes its saved state with PowerShell 5.1's ConvertTo-Json and a UTF-8 BOM.
    [Fact]
    public void TheStandaloneScriptsSavedStateReadsAsTheOriginal()
    {
        var path = Path.Combine(Path.GetTempPath(), "wsgm-tests-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """
                                    {
                                        "Source":  "MEASURED",
                                        "ProfileId":  7,
                                        "MinimumHz":  48,
                                        "MaximumHz":  120,
                                        "MaxIncreaseUs":  0,
                                        "MaxDecreaseUs":  0,
                                        "LowFpsSolutionEnabled":  true,
                                        "HighFpsSolutionEnabled":  true
                                    }
                                    """, new System.Text.UTF8Encoding(true));

            Assert.Equal(new ExtendedVrrIntelState
            {
                Source = "MEASURED", ProfileId = 7, MinimumHz = 48, MaximumHz = 120, LowFpsSolutionEnabled = true,
                HighFpsSolutionEnabled = true
            }, WindowsExtendedVrrMachine.ReadOriginal(path));
            File.WriteAllText(path, "{ \"Source\": 1 }");
            Assert.Null(WindowsExtendedVrrMachine.ReadOriginal(path));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Null(WindowsExtendedVrrMachine.ReadOriginal(path));
    }
}
