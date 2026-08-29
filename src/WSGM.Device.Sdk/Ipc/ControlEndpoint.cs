using System;

namespace WSGM.Device.Sdk.Ipc;

/// <summary>How the current-user DeviceHost control endpoint is named and authenticated.</summary>
public static class ControlEndpoint
{
    /// <summary>Length of the one-time handshake nonce in bytes.</summary>
    public const int NonceBytes = 32;

    /// <summary>Builds the pipe name for one session and host instance.</summary>
    /// <param name="sessionId">Interactive session identifier.</param>
    /// <param name="instanceToken">Token unique to this host launch.</param>
    /// <returns>Pipe name without the leading path.</returns>
    public static string PipeName(uint sessionId, string instanceToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceToken);
        return $"WSGM.DeviceHost.{sessionId}.{instanceToken}";
    }

    /// <summary>Compares a presented launch nonce in constant time.</summary>
    /// <param name="nonce">Presented nonce.</param>
    /// <param name="expected">Expected nonce.</param>
    /// <returns>Whether both full nonces match.</returns>
    public static bool NonceMatches(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> expected)
    {
        if (nonce.Length != expected.Length || nonce.Length != NonceBytes)
        {
            return false;
        }

        int difference = 0;
        for (int index = 0; index < nonce.Length; index++)
        {
            difference |= nonce[index] ^ expected[index];
        }

        return difference == 0;
    }
}
