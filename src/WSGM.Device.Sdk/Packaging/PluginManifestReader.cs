using System;
using System.Collections.Generic;
using System.Text.Json;
using WSGM.Device.Sdk.Serialization;

namespace WSGM.Device.Sdk.Packaging;

/// <summary>
/// The outcome of reading a <c>plugin.wsgm.json</c> document.
/// </summary>
/// <param name="Manifest">The parsed manifest, or <see langword="null"/> when reading failed.</param>
/// <param name="Errors">Every problem found. Empty exactly when <paramref name="Manifest"/> is valid.</param>
public sealed record PluginManifestReadResult(
    PluginManifest? Manifest,
    IReadOnlyList<ManifestValidationError> Errors)
{
    /// <summary>Whether the document parsed and satisfied every package rule.</summary>
    public bool IsValid => Manifest is not null && Errors.Count == 0;
}

/// <summary>
/// Parses and validates a package manifest from untrusted bytes.
/// </summary>
/// <remarks>
/// Reading is deliberately two-staged: the parser enforces size, depth, and shape, and only a
/// document that survives that is handed to <see cref="PluginManifestValidator"/> for meaning. The
/// size and depth caps are applied before any allocation proportional to the input, so an oversized
/// or deeply nested document costs a length check rather than a parse.
/// </remarks>
public static class PluginManifestReader
{
    // Built once and reused. A JsonSerializerOptions instance becomes read-only on first use and is
    // bound to the context that consumes it, so constructing a fresh context per call against a
    // shared options instance races as soon as two reads overlap.
    private static readonly DeviceJsonContext ReadContext =
        new(new JsonSerializerOptions(DeviceJsonContext.Default.Options)
        {
            MaxDepth = ManifestLimits.MaxDepth,
        });

    /// <summary>
    /// Reads a manifest from a UTF-8 JSON document.
    /// </summary>
    /// <param name="utf8Json">The raw document bytes as read from the package.</param>
    /// <returns>
    /// The parsed manifest and an empty error list, or <see langword="null"/> with the reasons it was
    /// rejected. This method does not throw for malformed input; a bad manifest is an expected
    /// condition, not an exceptional one.
    /// </returns>
    public static PluginManifestReadResult Read(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > ManifestLimits.MaxDocumentBytes)
        {
            return Failure("", ManifestValidationCode.DocumentTooLarge,
                $"Manifest is {utf8Json.Length} bytes, above the {ManifestLimits.MaxDocumentBytes}-byte limit.");
        }

        if (utf8Json.IsEmpty)
        {
            return Failure("", ManifestValidationCode.MalformedDocument, "Manifest is empty.");
        }

        PluginManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(utf8Json, ReadContext.PluginManifest);
        }
        catch (JsonException ex)
        {
            // The serializer disallows unknown members. Keep one structural-document outcome rather
            // than guessing a subcategory from localized exception text.
            return Failure("", ManifestValidationCode.MalformedDocument, ex.Message);
        }
        catch (NotSupportedException ex)
        {
            return Failure("", ManifestValidationCode.MalformedDocument, ex.Message);
        }

        if (manifest is null)
        {
            return Failure("", ManifestValidationCode.MalformedDocument, "Manifest deserialized to null.");
        }

        return new PluginManifestReadResult(manifest, PluginManifestValidator.Validate(manifest));
    }

    private static PluginManifestReadResult Failure(
        string path,
        ManifestValidationCode code,
        string message) =>
        new(null, [new ManifestValidationError(path, code, message)]);
}
