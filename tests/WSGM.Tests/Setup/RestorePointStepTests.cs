using System.Text.Json.Nodes;
using WindowsDeviceControl;
using WSGM.Setup.Engine;

namespace WSGM.Tests.Setup;

public sealed class RestorePointStepTests
{
    private static InstallChoices Choices(bool restorePoint)
    {
        return new InstallChoices(null, ["wsgm.test"], new JsonObject())
        {
            Compatibility = new DriverCompatibilityChoices { CreateRestorePoint = restorePoint }
        };
    }

    [Fact]
    public void ChosenRestorePointIsTheFirstStep()
    {
        using var installation = new SetupTestInstallation();

        var plan = installation.Engine.PlanInstall(Choices(true));

        Assert.Equal("Creating a Windows restore point", plan[0].Label);
        Assert.False(plan[0].Fatal);
    }

    [Fact]
    public void NoRestorePointUnlessChosen()
    {
        using var installation = new SetupTestInstallation();
        installation.Engine.RestorePoints = _ => throw new InvalidOperationException("Unexpected restore point");

        var plan = installation.Engine.PlanInstall(Choices(false));

        Assert.DoesNotContain(plan, step => step.Label == "Creating a Windows restore point");
    }

    [Theory]
    [InlineData(SystemRestorePointResult.Created, 0, false)]
    [InlineData(SystemRestorePointResult.Disabled, 0, true)]
    [InlineData(SystemRestorePointResult.Failed, 1460, true)]
    public void RestorePointNeverStopsSetup(SystemRestorePointResult result, int error, bool skipped)
    {
        using var installation = new SetupTestInstallation();
        string? description = null;
        installation.Engine.RestorePoints = text =>
        {
            description = text;
            return new SystemRestorePointOutcome(result, error);
        };
        var step = installation.Engine.PlanInstall(Choices(true))[0];

        Assert.True(step.Run());

        Assert.Equal(skipped, step.State is StepState.Skipped);
        Assert.StartsWith("WSGM ", description);
        if (result is SystemRestorePointResult.Failed)
        {
            Assert.Contains("1460", step.Note, StringComparison.Ordinal);
        }
    }
}
