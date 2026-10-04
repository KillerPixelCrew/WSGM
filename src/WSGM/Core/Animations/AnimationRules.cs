using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using WindowsDeviceControl;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;

using static WSGM.Core.AppConfigDefaults;

namespace WSGM.Core;

internal static class AnimationRules
{
    /// <summary>Repairs the animations section: a trimmed boot id and whole set-aside strings.</summary>
    /// <param name="animations">The section.</param>
    internal static IReadOnlyList<string> Normalize(AnimationsConfig animations)
    {
        animations.Boot = animations.Boot?.Trim() ?? string.Empty;
        if (animations.SteamSetAside is { } setAside)
        {
            setAside.MovieId ??= string.Empty;
            setAside.LocalPath ??= string.Empty;
        }
        return [];
    }
}
