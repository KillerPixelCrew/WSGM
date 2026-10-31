using System;

namespace WSGM.Core;

/// <summary>Runtime process identity observed without opening an IPC/control path.</summary>
/// <param name="ProcessId">Windows process identifier.</param>
/// <param name="ExecutablePath">Image path observed for this process.</param>
/// <param name="StartedAt">Process creation time, distinguishing PID reuse.</param>
internal sealed record RtssProcessIdentity(
    int ProcessId,
    string ExecutablePath,
    DateTimeOffset StartedAt);
