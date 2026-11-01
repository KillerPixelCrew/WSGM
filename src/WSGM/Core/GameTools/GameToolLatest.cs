using System;

namespace WSGM.Core;

/// <summary>The cached result of the latest-release lookup.</summary>
/// <param name="Release">The release, or null when the lookup failed.</param>
/// <param name="Error">Why the lookup failed.</param>
/// <param name="CheckedUtc">When the lookup ran.</param>
internal sealed record GameToolLatest(HostedRelease? Release, string? Error, DateTime CheckedUtc);
