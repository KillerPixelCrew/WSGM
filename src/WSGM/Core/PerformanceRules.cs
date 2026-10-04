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

internal static class PerformanceRules
{
    internal static IReadOnlyList<string> Normalize(PerformanceConfig performance)
    {
        ConfigRepair.NormalizeEnums(performance);
        return [];
    }
}
