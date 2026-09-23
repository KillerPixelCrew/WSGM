using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>
///     Which launch route a packaged title needs, from its own metadata. The two routes differ in
///     what they write into the game, so Unknown is a first-class outcome rather than a default:
///     there is no validated route for a title that is neither shape.
/// </summary>
public sealed class XboxRuntimeClassifierTests
{
    private static XboxPackageFacts Facts(
        string entryPoint = "App.Entry",
        string executable = "game.exe",
        bool runFullTrust = false,
        int applications = 1,
        bool gameConfig = false,
        IReadOnlyList<string>? dependencies = null,
        string applicationId = "App",
        bool readable = true)
    {
        return new XboxPackageFacts(applicationId, entryPoint, executable, runFullTrust,
            applications, dependencies ?? [], gameConfig, readable);
    }

    private static XboxRuntime Runtime(XboxPackageFacts facts)
    {
        var classified = XboxRuntimeClassifier.Classify(facts);
        Assert.False(string.IsNullOrWhiteSpace(classified.Evidence));
        return classified.Runtime;
    }

    [Fact]
    public void AFullTrustTitleCarryingAGameConfigIsPackagedWin32()
    {
        // The PowerWash Simulator 2 shape.
        Assert.Equal(XboxRuntime.PackagedWin32Gdk, Runtime(Facts(
            "Windows.FullTrustApplication", gameConfig: true)));
    }

    [Fact]
    public void AFullTrustTitleActivatingTheLaunchHelperIsPackagedWin32()
    {
        Assert.Equal(XboxRuntime.PackagedWin32Gdk, Runtime(Facts(
            "Windows.FullTrustApplication", "gamelaunchhelper.exe")));
    }

    [Fact]
    public void AFullTrustTitleDependingOnGamingServicesIsPackagedWin32()
    {
        Assert.Equal(XboxRuntime.PackagedWin32Gdk, Runtime(Facts(
            "Windows.FullTrustApplication",
            dependencies: ["Microsoft.GamingServices"])));
    }

    [Fact]
    public void TheRunFullTrustCapabilityCountsAsFullTrust()
    {
        Assert.Equal(XboxRuntime.PackagedWin32Gdk, Runtime(Facts(
            "Windows.FullTrustApplication", runFullTrust: true, gameConfig: true)));
    }

    [Fact]
    public void AWinRtEntryPointWithoutFullTrustIsNativeUwp()
    {
        // The Moonlighter shape.
        Assert.Equal(XboxRuntime.NativeUwp, Runtime(Facts()));
    }

    [Fact]
    public void AFullTrustTitleWithNoGdkEvidenceIsUnknown()
    {
        // A packaged Win32 application that is not a GDK title. The demonstrated route works by
        // setting Steam up in the launch helper, and this has none.
        Assert.Equal(XboxRuntime.Unknown, Runtime(Facts("Windows.FullTrustApplication")));
    }

    [Fact]
    public void FullTrustBesideAWinRtEntryPointIsContradictoryAndUnknown()
    {
        Assert.Equal(XboxRuntime.Unknown, Runtime(Facts(runFullTrust: true, gameConfig: true)));
    }

    [Fact]
    public void AWinRtEntryPointBesideAGameConfigIsContradictoryAndUnknown()
    {
        Assert.Equal(XboxRuntime.Unknown, Runtime(Facts(gameConfig: true)));
    }

    [Fact]
    public void APackageDeclaringSeveralApplicationsIsUnknown()
    {
        // Which one is the game is exactly what is not established.
        Assert.Equal(XboxRuntime.Unknown, Runtime(Facts(applications: 2)));
    }

    [Fact]
    public void APackageWithNoApplicationIsUnknown()
    {
        Assert.Equal(XboxRuntime.Unknown, Runtime(Facts(applicationId: "", applications: 0)));
    }

    [Fact]
    public void AnUnreadableManifestIsUnknown()
    {
        Assert.Equal(XboxRuntime.Unknown, Runtime(Facts(readable: false)));
    }

    [Fact]
    public void AMissingEntryPointIsUnknown()
    {
        Assert.Equal(XboxRuntime.Unknown, Runtime(Facts("")));
    }

    [Fact]
    public void EveryOutcomeNamesItsDecidingEvidence()
    {
        // The evidence is shown to the user in the import preview, so a classification nobody can
        // argue with is a defect.
        XboxPackageFacts[] cases =
        [
            Facts(),
            Facts("Windows.FullTrustApplication", gameConfig: true),
            Facts("Windows.FullTrustApplication"),
            Facts(applications: 3),
            Facts(readable: false)
        ];

        foreach (var facts in cases)
        {
            var classified = XboxRuntimeClassifier.Classify(facts);
            Assert.False(string.IsNullOrWhiteSpace(classified.Evidence));
            Assert.EndsWith(".", classified.Evidence, StringComparison.Ordinal);
        }
    }
}
