using System;
using System.Collections.Generic;
using System.IO;
using WSGM.Device.Sdk.Ipc;

namespace WSGM.DeviceHost;

/// <summary>Validated coordinator-supplied launch arguments.</summary>
internal sealed record HostArguments
{
    public required string PackagePath { get; init; }

    public required string PackageId { get; init; }

    public required string PipeName { get; init; }

    public required byte[] Nonce { get; init; }

    public required uint SessionId { get; init; }

    public required long CycleGeneration { get; init; }

    public string? StateRingName { get; init; }

    public string? StateEventName { get; init; }

    public static bool TryParse(string[] args, out HostArguments? parsed, out string error)
    {
        parsed = null;
        error = string.Empty;
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        HashSet<string> allowed = new(StringComparer.Ordinal)
        {
            "--package",
            "--package-id",
            "--pipe",
            "--nonce",
            "--session",
            "--generation",
            "--state-ring",
            "--state-event",
        };

        if ((args.Length & 1) != 0)
        {
            error = "Every host option requires exactly one value.";
            return false;
        }

        for (int i = 0; i < args.Length; i += 2)
        {
            string option = args[i];
            if (!allowed.Contains(option))
            {
                error = $"Unknown host option '{option}'.";
                return false;
            }

            if (!values.TryAdd(option, args[i + 1]))
            {
                error = $"Host option '{option}' was supplied more than once.";
                return false;
            }
        }

        if (!Required(values, "--package", out string packagePath)
            || !Required(values, "--package-id", out string packageId)
            || !Required(values, "--pipe", out string pipeName)
            || !Required(values, "--nonce", out string nonceText)
            || !Required(values, "--session", out string sessionText)
            || !Required(values, "--generation", out string generationText))
        {
            error = "Required host launch arguments are missing.";
            return false;
        }

        byte[] nonce;
        try
        {
            nonce = Convert.FromBase64String(nonceText);
        }
        catch (FormatException)
        {
            error = "The handshake nonce is not valid Base64.";
            return false;
        }

        if (nonce.Length != ControlEndpoint.NonceBytes
            || !uint.TryParse(sessionText, out uint sessionId)
            || !long.TryParse(generationText, out long cycleGeneration)
            || cycleGeneration <= 0)
        {
            error = "The nonce, session, or cycle generation is malformed.";
            return false;
        }

        parsed = new HostArguments
        {
            PackagePath = Path.GetFullPath(packagePath),
            PackageId = packageId,
            PipeName = pipeName,
            Nonce = nonce,
            SessionId = sessionId,
            CycleGeneration = cycleGeneration,
            StateRingName = values.GetValueOrDefault("--state-ring"),
            StateEventName = values.GetValueOrDefault("--state-event"),
        };
        return true;
    }

    private static bool Required(
        IReadOnlyDictionary<string, string> values,
        string key,
        out string value)
    {
        if (values.TryGetValue(key, out string? candidate) && !string.IsNullOrWhiteSpace(candidate))
        {
            value = candidate;
            return true;
        }

        value = string.Empty;
        return false;
    }
}
