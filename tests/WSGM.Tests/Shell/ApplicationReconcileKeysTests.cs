using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

/// <summary>Which per-application values a pass carries, kept per capability.</summary>
public sealed class ApplicationReconcileKeysTests
{
    private static readonly ManualTdpProfile Manual = new(false, null, 12);

    [Fact]
    public void APowerLimitPublishedAfterVariableRefreshStillReconcilesForTheSameApplication()
    {
        ApplicationReconcileKeys keys = new();

        // The graphics package publishes variable refresh before the device publishes its power limit.
        Assert.Equal((false, true), keys.Take("steam:42", Manual, true, false, true));
        Assert.Equal((true, false), keys.Take("steam:42", Manual, true, true, true));
        Assert.Equal((false, false), keys.Take("steam:42", Manual, true, true, true));
    }

    [Fact]
    public void NothingIsRecordedWhileNoCapabilityIsPublished()
    {
        ApplicationReconcileKeys keys = new();

        Assert.Equal((false, false), keys.Take("steam:42", Manual, true, false, false));
        Assert.Equal((true, true), keys.Take("steam:42", Manual, true, true, true));
    }

    [Fact]
    public void EachValueReconcilesAgainOnlyWhenWhatItResolvesToChanges()
    {
        ApplicationReconcileKeys keys = new();
        Assert.Equal((true, true), keys.Take("steam:42", Manual, true, true, true));

        Assert.Equal((false, true), keys.Take("steam:42", Manual, false, true, true));
        Assert.Equal((true, false), keys.Take("steam:42", null, false, true, true));
        Assert.Equal((true, true), keys.Take(null, null, false, true, true));
    }
}
