using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WSGM.DeviceLab.Core.Capture;
using WSGM.DeviceLab.Core.Preflight;

namespace WSGM.DeviceLab.Core.Fixtures;

/// <summary>Extracts plain simulator-only fixtures from validated sanitized captures.</summary>
public static class FixtureExtractionWorkflow
{
    /// <summary>Current deterministic extractor identity.</summary>
    public const string ExtractorVersion = "wsgm-device-fixture@1";

    /// <summary>Writes a new reviewable fixture directory without invoking hardware.</summary>
    /// <param name="bundle">Validated sanitized source bundle.</param>
    /// <param name="sourceCaptureSha256">Hash of the exact source archive.</param>
    /// <param name="fixtureId">Stable fixture ID.</param>
    /// <param name="outputDirectory">New explicit fixture directory.</param>
    /// <param name="boundaries">Protected filesystem boundaries.</param>
    /// <returns>Written simulator-only manifest.</returns>
    public static FixtureManifest Extract(
        SanitizedCaptureBundle bundle,
        string sourceCaptureSha256,
        string fixtureId,
        string outputDirectory,
        DeviceLabPathBoundaries boundaries)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceCaptureSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureId);
        ArgumentNullException.ThrowIfNull(boundaries);
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
        {
            throw new IOException("Fixture output must be a new directory.");
        }

        DeviceLabOutputPathDecision decision = DeviceLabOutputPathPolicy.Evaluate(
            outputDirectory,
            DeviceLabOutputTargetKind.Directory,
            boundaries);
        if (!decision.IsAllowed || decision.FullPath is null)
        {
            throw new IOException(decision.Reason ?? "Fixture output path was rejected.");
        }

        SortedDictionary<string, byte[]> inputs = new(StringComparer.Ordinal)
        {
            ["input/inventory.json"] = WithNewline(JsonSerializer.SerializeToUtf8Bytes(
                bundle.Inventory,
                DeviceLabJsonContext.Default.MachineInventory)),
            ["input/recipe.json"] = WithNewline(JsonSerializer.SerializeToUtf8Bytes(
                bundle.Recipe,
                DeviceLabJsonContext.Default.ObserveOnlyRecipe)),
        };
        foreach (CaptureStreamFile stream in bundle.Streams.OrderBy(stream => stream.SourceId, StringComparer.Ordinal))
        {
            inputs[$"input/streams/{SafeName(stream.SourceId)}.ndjson"] = Ndjson(
                stream.Events,
                captureEvent => JsonSerializer.SerializeToUtf8Bytes(
                    captureEvent,
                    DeviceLabCompactJsonContext.Default.CaptureStreamEvent));
        }

        SortedDictionary<string, byte[]> expected = new(StringComparer.Ordinal);
        foreach (CaptureAnalysisFile analysis in bundle.Analysis.OrderBy(item => item.AnalyzerId, StringComparer.Ordinal))
        {
            expected[$"expected/analysis/{SafeName(analysis.AnalyzerId)}.ndjson"] = Ndjson(
                analysis.Results,
                result => JsonSerializer.SerializeToUtf8Bytes(
                    result,
                    DeviceLabCompactJsonContext.Default.CaptureAnalysisResult));
        }

        FixtureManifest manifest = new()
        {
            SchemaVersion = FixtureSchema.CurrentVersion,
            FixtureId = fixtureId,
            SourceCaptureSha256 = sourceCaptureSha256,
            ExtractorVersion = ExtractorVersion,
            ReplayPolicy = FixtureReplayPolicy.SimulatorOnly,
            Inputs = [.. inputs.Select(pair => Artifact(pair.Key, pair.Value))],
            ExpectedOutputs = [.. expected.Select(pair => Artifact(pair.Key, pair.Value))],
            ClaimIds = [.. bundle.Claims.Select(claim => claim.ClaimId).Order(StringComparer.Ordinal)],
        };
        IReadOnlyList<CaptureValidationError> errors = FixtureSchemaValidator.Validate(manifest);
        if (errors.Count != 0)
        {
            throw new InvalidDataException(errors[0].Message);
        }

        Directory.CreateDirectory(decision.FullPath);
        DeviceLabOutputPathDecision recheck = DeviceLabOutputPathPolicy.Evaluate(
            decision.FullPath,
            DeviceLabOutputTargetKind.Directory,
            boundaries);
        if (!recheck.IsAllowed)
        {
            throw new IOException(recheck.Reason ?? "Fixture path changed before write.");
        }

        foreach ((string path, byte[] bytes) in inputs.Concat(expected))
        {
            WriteNew(decision.FullPath, path, bytes);
        }

        WriteNew(
            decision.FullPath,
            FixtureSchema.ManifestPath,
            WithNewline(JsonSerializer.SerializeToUtf8Bytes(
                manifest,
                DeviceLabJsonContext.Default.FixtureManifest)));
        return manifest;
    }

    private static FixtureArtifact Artifact(string path, byte[] bytes) => new()
    {
        Path = path,
        MediaType = path.EndsWith(".ndjson", StringComparison.Ordinal)
            ? "application/x-ndjson"
            : "application/json",
        Length = bytes.Length,
        Sha256 = CaptureHashFile.Hash(bytes),
    };

    private static void WriteNew(string root, string relative, byte[] bytes)
    {
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Fixture artifact escaped its output directory.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(bytes);
        output.Flush(flushToDisk: true);
    }

    private static string SafeName(string value)
    {
        string result = string.Concat(value.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '-'));
        return string.IsNullOrWhiteSpace(result) ? "unknown" : result;
    }

    private static byte[] WithNewline(byte[] bytes)
    {
        byte[] output = new byte[bytes.Length + 1];
        bytes.CopyTo(output, 0);
        output[^1] = (byte)'\n';
        return output;
    }

    private static byte[] Ndjson<T>(IReadOnlyList<T> values, Func<T, byte[]> serializer)
    {
        using MemoryStream output = new();
        foreach (T value in values)
        {
            output.Write(serializer(value));
            output.WriteByte((byte)'\n');
        }

        return output.ToArray();
    }
}
