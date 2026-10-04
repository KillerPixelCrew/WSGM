using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests.Builders;

/// <summary>Profile stores and performance services over the hardware-free RTSS simulation.</summary>
internal static class PerformanceBuilders
{
    /// <summary>A profile store kept in memory, so no test touches a real configuration file.</summary>
    internal static ProfileService Profiles(ProfileConfig? config = null)
    {
        var initial = config ?? new ProfileConfig();
        return new ProfileService(initial, new InMemoryProfileStore(initial).MutateAsync);
    }

    /// <summary>A store whose Global layer holds a frame limit and overlay level.</summary>
    internal static ProfileConfig Config(int? frameLimit = null, int? overlayLevel = null, params GameProfile[] games)
    {
        return new ProfileConfig
        {
            Global = new ProfileValues { FrameLimit = frameLimit, OverlayLevel = overlayLevel },
            Games = [.. games]
        };
    }

    /// <summary>One game profile bound to its canonical id and, optionally, an executable.</summary>
    internal static GameProfile Game(string id, string? executable = null, int? frameLimit = null,
        int? overlayLevel = null, bool enabled = true)
    {
        return new GameProfile
        {
            Id = id,
            Name = executable ?? id,
            ProcessNames = executable is null ? [] : [executable],
            Enabled = enabled,
            Values = new ProfileValues { FrameLimit = frameLimit, OverlayLevel = overlayLevel }
        };
    }

    internal static PerformanceService Service(ProfileService? profiles = null)
    {
        return Service(new SimulatedRtssAdapter(), profiles ?? Profiles());
    }

    internal static PerformanceService Service(
        IRtssAdapter adapter,
        ProfileService profiles,
        TimeSpan? pollInterval = null,
        TimeSpan? commandTimeout = null,
        bool enabled = true)
    {
        return new PerformanceService(
            adapter,
            InertLauncher(),
            (field, value, token) => profiles.SetAsync(field, value, token),
            profiles.Current,
            enabled,
            pollInterval,
            commandTimeout);
    }

    /// <summary>A launcher that never starts or watches a real RTSS process.</summary>
    internal static RtssLauncher InertLauncher()
    {
        return new RtssLauncher(static _ => Task.FromResult(false), static (_, _) => null);
    }

    /// <summary>Makes an application the running one and lets RTSS apply what resolves for it.</summary>
    internal static Task RunAsync(this PerformanceService service, ProfileService profiles,
        PerformanceApplicationTarget? target)
    {
        return service.ApplyProfilesAsync(profiles.SetRunningApplication(target), service.Enabled);
    }
}
