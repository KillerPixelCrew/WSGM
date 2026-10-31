using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>The frametime feed AutoTDP consumes.</summary>
internal interface IFrametimeSource
{
    /// <summary>Reads every application currently delivering frames.</summary>
    /// <returns>Live rendering applications, newest measurement each.</returns>
    IReadOnlyList<RtssFrametimeSample> ReadLive();
}
