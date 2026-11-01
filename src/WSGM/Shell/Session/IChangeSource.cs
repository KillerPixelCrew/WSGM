using System;

namespace WSGM.Shell;

/// <summary>A session service an overlay view follows: it says when what it publishes changed.</summary>
internal interface IChangeSource
{
    /// <summary>Raised on whatever thread finished the change, possibly in bursts.</summary>
    event Action? Changed;
}
