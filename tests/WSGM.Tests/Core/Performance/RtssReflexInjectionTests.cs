using WSGM.Core;
using WSGM.Testing;

namespace WSGM.Tests.Core;

public sealed class RtssReflexInjectionTests
{
    // The [Framerate] section of RTSS 7.3's global profile, with injection on as a fresh install leaves it.
    private const string GlobalProfile =
        "[OSD]\r\nEnableOSD=1\r\n[Framerate]\r\nLimit=0\r\nLimitDenominator=1\r\nSyncLimiter=0\r\nPassiveWait=1\r\n"
        + "ReflexSleep=1\r\nReflexSetLatencyMarker=1\r\n[Hooking]\r\nEnableHooking=1\r\n";

    [Fact]
    public void ReflexInjectionIsSwitchedOffAndEverythingElseIsLeftAlone()
    {
        using var directory = new TemporaryDirectory();
        var profile = Path.Combine(directory.Root, "Global");
        File.WriteAllText(profile, GlobalProfile);

        var (present, changed) = RtssNativeAdapter.SwitchOffReflexInjection(profile);

        Assert.Equal(2, present);
        Assert.Equal(["ReflexSleep", "ReflexSetLatencyMarker"], changed);
        Assert.Equal(GlobalProfile.Replace("ReflexSleep=1", "ReflexSleep=0")
            .Replace("ReflexSetLatencyMarker=1", "ReflexSetLatencyMarker=0"), File.ReadAllText(profile));

        var again = RtssNativeAdapter.SwitchOffReflexInjection(profile);
        Assert.Empty(again.Changed);
    }

    [Fact]
    public void AnRtssWithoutTheKeysIsNotChanged()
    {
        using var directory = new TemporaryDirectory();
        var profile = Path.Combine(directory.Root, "Global");
        const string older = "[Framerate]\r\nLimit=60\r\n";
        File.WriteAllText(profile, older);

        var (present, changed) = RtssNativeAdapter.SwitchOffReflexInjection(profile);

        Assert.Equal(0, present);
        Assert.Empty(changed);
        Assert.Equal(older, File.ReadAllText(profile));
    }
}
