using System;

namespace WSGM.Device.ProbeHost;

/// <summary>
/// Entry point for the disposable compatibility-probe host.
/// </summary>
/// <remarks>
/// Device Lab runs one reviewed, hash-pinned probe per process and discards the process afterwards.
/// It never activates a candidate plugin's normal lifecycle to test compatibility, because
/// activation may contain writes; a probe reaches only the candidate module's dedicated probe entry
/// point. Like <c>WSGM.DeviceHost</c>, this executable refuses to do anything useful when started by
/// hand — the probe identity and its expected hash come from the caller.
/// </remarks>
internal static class Program
{
    /// <summary>The probe ran and its result was written to the caller.</summary>
    private const int ExitSuccess = 0;

    /// <summary>Required arguments were missing or malformed.</summary>
    private const int ExitInvalidArguments = 64;

    private static int Main(string[] args)
    {
        string? probeId = null;
        string? expectedHash = null;

        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--probe":
                    probeId = args[++i];
                    break;
                case "--hash":
                    expectedHash = args[++i];
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(probeId) || string.IsNullOrWhiteSpace(expectedHash))
        {
            Console.Error.WriteLine(
                "WSGM.Device.ProbeHost is started by Device Lab, not run directly. "
                    + "Required: --probe <id> --hash <sha256>.");
            return ExitInvalidArguments;
        }

        // P2.6 owns probe resolution and execution: match the probe to the exact family and endpoint,
        // verify the pinned hash, run it under its declared rate and deadline, and validate the
        // response against its structural invariants rather than accepting a nonempty reply.
        return ExitSuccess;
    }
}
