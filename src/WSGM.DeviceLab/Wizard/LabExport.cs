using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using WSGM.DeviceLab.Application;
using WSGM.DeviceLab.Capture;
using WSGM.DeviceLab.Preflight;

namespace WSGM.DeviceLab.Wizard;

/// <summary>One file as it will appear in the shared report.</summary>
/// <param name="Path">Path inside the archive, with forward slashes.</param>
/// <param name="Bytes">Size after redaction.</param>
/// <param name="Sha256">Digest after redaction, upper-case hex.</param>
/// <param name="Redacted">Whether the content passed through the redactor.</param>
internal sealed record LabExportFile(string Path, long Bytes, string Sha256, bool Redacted);

/// <summary>What the tester reviews before the report is written.</summary>
/// <param name="Files">Every file that will be shared.</param>
/// <param name="Excluded">Project files that will not be shared, with the reason.</param>
/// <param name="Redactions">What the redactor replaced, by category.</param>
/// <param name="ProjectManifest">The shareable project manifest, in full.</param>
internal sealed record LabExportPreview(
    IReadOnlyList<LabExportFile> Files,
    IReadOnlyList<string> Excluded,
    IReadOnlyList<RedactionSummary> Redactions,
    string ProjectManifest);

/// <summary>
///     Turns a project into a shareable report archive.
/// </summary>
/// <remarks>
///     The whole report is built in memory first, so the preview the tester approves is exactly what is
///     written. Every JSON string passes through one <see cref="CaptureRedactor" />, so a device seen in
///     two segments keeps one token; inventories use the inventory redaction, which also tokenizes
///     location paths. Only JSON and raw ACPI tables are shared; anything else in the project stays
///     local and is listed as excluded.
/// </remarks>
internal sealed class LabExport
{
    private static readonly string[] BinaryExtensions = [".aml", ".dat"];

    private readonly SortedDictionary<string, (byte[] Content, bool Redacted)> _files = new(StringComparer.Ordinal);

    private LabExport(LabExportPreview preview)
    {
        Preview = preview;
    }

    /// <summary>What will be written.</summary>
    public LabExportPreview Preview { get; private set; }

    /// <summary>Builds the redacted report for a project.</summary>
    /// <param name="project">Project to share.</param>
    /// <param name="cancellationToken">Stops the build between files; nothing is written either way.</param>
    /// <returns>The report, ready to preview and write.</returns>
    public static LabExport Prepare(LabProject project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        // The project replaces its immutable manifest as a whole under its lock, so one read is a consistent
        // snapshot. The shared project.json is that snapshot too, so it always agrees with analysis.json.
        var manifest = project.Manifest;
        var manifestJson = JsonSerializer.Serialize(manifest, LabProject.JsonOptions) + "\n";
        CaptureRedactor redactor = new();
        List<string> excluded = [];
        var export = new LabExport(new LabExportPreview([], [], [], string.Empty));
        foreach (var file in Directory.EnumerateFiles(project.Directory, "*", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(project.Directory, file).Replace('\\', '/');
            if (Path.GetFileName(relative).StartsWith('.'))
            {
                excluded.Add($"{relative}: temporary file");
                continue;
            }

            var extension = Path.GetExtension(relative).ToLowerInvariant();
            byte[] content;
            bool redacted;
            if (extension == ".json")
            {
                var json = relative == LabProject.ManifestFileName ? manifestJson : File.ReadAllText(file);
                content = Encoding.UTF8.GetBytes(RedactJson(relative, json, redactor));
                redacted = true;
            }
            else if (BinaryExtensions.Contains(extension))
            {
                content = File.ReadAllBytes(file);
                redacted = false;
            }
            else
            {
                excluded.Add($"{relative}: not a shared file type");
                continue;
            }

            export._files[relative] = (content, redacted);
        }

        // One table of every step for whoever opens the report: status, attempts and summary.
        var analysis = Encoding.UTF8.GetBytes(RedactJson("analysis.json", JsonSerializer.Serialize(new
        {
            manifest.Device,
            manifest.ToolVersion,
            manifest.SourceRevision,
            Steps = manifest.Segments.Select(segment => new
            {
                segment.Id,
                segment.Status,
                segment.Attempts,
                segment.Summary
            }),
            Counts = manifest.Segments.GroupBy(segment => segment.Status)
                .ToDictionary(group => group.Key.ToString(), group => group.Count()),
            Limits =
                "Candidates are what reacted during a step, never proof of cause. Stages the tester skipped or stopped are marked, never counted as measured."
        }, LabProject.JsonOptions), redactor));
        export._files["analysis.json"] = (analysis, true);

        var shared = export._files.TryGetValue(LabProject.ManifestFileName, out var entry)
            ? Encoding.UTF8.GetString(entry.Content)
            : string.Empty;
        export.Preview = new LabExportPreview(
            [
                .. export._files.Select(pair => new LabExportFile(
                    pair.Key,
                    pair.Value.Content.LongLength,
                    Convert.ToHexString(SHA256.HashData(pair.Value.Content)),
                    pair.Value.Redacted))
            ],
            excluded,
            redactor.Summarize(),
            shared);
        return export;
    }

    /// <summary>Writes the archive to a new file.</summary>
    /// <param name="target">Archive path; must not exist.</param>
    /// <param name="boundaries">Output path boundaries.</param>
    /// <exception cref="IOException">The target is refused or already exists.</exception>
    public void Write(string target, DeviceLabPathBoundaries boundaries)
    {
        var decision = DeviceLabOutputPathPolicy.Evaluate(target, DeviceLabOutputTargetKind.NewFile, boundaries);
        if (!decision.IsAllowed)
        {
            throw new IOException(decision.Reason);
        }

        var full = decision.FullPath!;
        var staging = DurableFile.StagingPath(full);
        try
        {
            DurableFile.WriteNew(staging, stream =>
            {
                using ZipArchive archive = new(stream, ZipArchiveMode.Create, true);
                foreach (var (path, (content, _)) in _files)
                {
                    Add(archive, path, content);
                }

                Add(archive, "report-manifest.json", Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(Preview.Files, LabProject.JsonOptions) + "\n"));
            }, access: FileAccess.ReadWrite);
            File.Move(staging, full, false);
        }
        catch
        {
            DurableFile.TryDeleteFile(staging);
            throw;
        }
    }

    private static void Add(ZipArchive archive, string path, byte[] content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static string RedactJson(string relative, string json, CaptureRedactor redactor)
    {
        if (Path.GetFileName(relative) == "inventory.json")
        {
            var inventory = JsonSerializer.Deserialize(json, DeviceLabJsonContext.Default.MachineInventory)
                            ?? throw new InvalidDataException($"{relative} is not an inventory.");
            return DeviceLabJson.Serialize(InventoryRedaction.ToShareable(inventory, redactor)) + "\n";
        }

        var node = JsonNode.Parse(json);
        RedactStrings(node, redactor);
        return (node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null") + "\n";
    }

    // Replaces string leaves in place; containers are never reassigned, because a JsonNode that
    // already has a parent cannot be set again.
    private static void RedactStrings(JsonNode? node, CaptureRedactor redactor)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(pair => pair.Key).ToArray())
                {
                    if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        obj[key] = JsonValue.Create(redactor.Redact(text));
                    }
                    else
                    {
                        RedactStrings(obj[key], redactor);
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        array[i] = JsonValue.Create(redactor.Redact(text));
                    }
                    else
                    {
                        RedactStrings(array[i], redactor);
                    }
                }

                break;
        }
    }
}
