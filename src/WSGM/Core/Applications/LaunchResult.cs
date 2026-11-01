using System.Diagnostics;

namespace WSGM.Core;

/// <summary>Reports whether a launch request started and whether UAC was declined.</summary>
/// <param name="Process">Caller-owned process wrapper when available; null for shell/protocol activation or failure.</param>
/// <param name="Started">Whether Windows accepted activation; does not imply a new process or readiness.</param>
/// <param name="ElevationDeclined">Whether UAC was declined, even if the subsequent normal launch succeeded.</param>
public sealed record LaunchResult(Process? Process, bool Started, bool ElevationDeclined);
