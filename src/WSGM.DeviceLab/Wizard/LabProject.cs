using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using WSGM.DeviceLab.Application;

namespace WSGM.DeviceLab.Wizard;

/// <summary>Where one wizard segment stands.</summary>
internal enum LabSegmentStatus
{
    /// <summary>Not run yet.</summary>
    NotStarted,

    /// <summary>Ran to the end; its latest attempt holds the evidence.</summary>
    Completed,

    /// <summary>The tester skipped it, for example a button the device does not have.</summary>
    Skipped,

    /// <summary>It stopped with an error; the latest attempt records why.</summary>
    Failed
}

/// <summary>The state of one segment in a project manifest.</summary>
internal sealed record LabSegmentState
{
    /// <summary>Segment ID. A slash nests it, for example <c>buttons/a</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Current status.</summary>
    public required LabSegmentStatus Status { get; init; }

    /// <summary>How many attempts exist; the highest-numbered one is current.</summary>
    public int Attempts { get; init; }

    /// <summary>When the status last changed.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>One line for the stage list.</summary>
    public string? Summary { get; init; }
}

/// <summary>Which device the tester says this is.</summary>
internal sealed record LabDeviceIdentity
{
    /// <summary>The knowledge record the tester confirmed, if any.</summary>
    public string? RecordId { get; init; }

    /// <summary>Its display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Product name typed by the tester when no record matched.</summary>
    public string? ProductName { get; init; }

    /// <summary>Exact model typed by the tester when no record matched.</summary>
    public string? Model { get; init; }
}

/// <summary>The project manifest: identity and segment states.</summary>
internal sealed record LabProjectManifest
{
    /// <summary>The only schema this build reads.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Manifest schema version.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Random project ID; not derived from the machine.</summary>
    public required string Id { get; init; }

    /// <summary>When the project was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Device Lab version that created the project.</summary>
    public required string ToolVersion { get; init; }

    /// <summary>SHA-256 of the executable that created the project, upper-case hex.</summary>
    public string? ToolSha256 { get; init; }

    /// <summary>Source revision the executable was built from, when the build recorded one.</summary>
    public string? SourceRevision { get; init; }

    /// <summary>What the report contains and what it never contains, as the tester was told.</summary>
    public string Notice { get; init; } = LabProject.PrivacyNotice;

    /// <summary>Which device this is.</summary>
    public LabDeviceIdentity Device { get; init; } = new();

    /// <summary>Segment states, in wizard order.</summary>
    public IReadOnlyList<LabSegmentState> Segments { get; init; } = [];
}

/// <summary>
///     A wizard project on disk: a manifest plus one directory per segment attempt.
/// </summary>
/// <remarks>
///     Every attempt of a segment gets its own directory (<c>segments/buttons/a/attempt-2</c>), so
///     redoing one button keeps the earlier evidence and the export can show both. The manifest is
///     rewritten atomically after every change; nothing else in the project is ever overwritten.
/// </remarks>
internal sealed class LabProject
{
    /// <summary>Manifest file name at the project root.</summary>
    public const string ManifestFileName = "project.json";

    /// <summary>The privacy notice recorded in every manifest.</summary>
    public const string PrivacyNotice =
        "This test records hardware identity, firmware tables, inputs and sensor readings. It never collects serial numbers, UUIDs, network addresses or account names; user folders and device instance paths are replaced before sharing, and nothing is uploaded.";

    private readonly object _gate = new();

    private LabProject(string directory, LabProjectManifest manifest)
    {
        Directory = directory;
        Manifest = manifest;
    }

    /// <summary>Project root.</summary>
    public string Directory { get; }

    /// <summary>The manifest as last saved.</summary>
    public LabProjectManifest Manifest { get; private set; }

    /// <summary>Serializer settings shared by everything the wizard writes into a project.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    /// <summary>Creates a new project in a directory that must not exist yet.</summary>
    /// <param name="directory">New project directory.</param>
    /// <param name="segmentIds">Top-level segment IDs in wizard order.</param>
    /// <param name="toolVersion">Device Lab version.</param>
    /// <param name="now">Creation time.</param>
    /// <param name="toolSha256">SHA-256 of the running executable.</param>
    /// <param name="sourceRevision">Source revision of the build.</param>
    /// <returns>The project.</returns>
    public static LabProject Create(
        string directory,
        IEnumerable<string> segmentIds,
        string toolVersion,
        DateTimeOffset now,
        string? toolSha256 = null,
        string? sourceRevision = null)
    {
        var full = Path.GetFullPath(directory);
        if (Path.Exists(full))
        {
            throw new IOException($"The project directory already exists: {full}");
        }

        System.IO.Directory.CreateDirectory(full);
        var manifest = new LabProjectManifest
        {
            SchemaVersion = LabProjectManifest.CurrentSchemaVersion,
            Id = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
            ToolVersion = toolVersion,
            ToolSha256 = toolSha256,
            SourceRevision = sourceRevision,
            Segments =
            [
                .. segmentIds.Select(id => new LabSegmentState
                {
                    Id = ValidateSegmentId(id), Status = LabSegmentStatus.NotStarted
                })
            ]
        };
        var project = new LabProject(full, manifest);
        project.Save();
        return project;
    }

    /// <summary>Opens an existing project.</summary>
    /// <param name="directory">Project root holding <see cref="ManifestFileName" />.</param>
    /// <returns>The project.</returns>
    /// <exception cref="InvalidDataException">The manifest is missing or unreadable.</exception>
    public static LabProject Open(string directory)
    {
        var full = Path.GetFullPath(directory);
        var path = Path.Combine(full, ManifestFileName);
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"{full} is not a Device Lab project: {ManifestFileName} is missing.");
        }

        LabProjectManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<LabProjectManifest>(File.ReadAllText(path), JsonOptions)
                       ?? throw new InvalidDataException("The project manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The project manifest is unreadable: {ex.Message}", ex);
        }

        if (manifest.SchemaVersion != LabProjectManifest.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Project schema {manifest.SchemaVersion} is not supported by this Device Lab.");
        }

        foreach (var segment in manifest.Segments)
        {
            ValidateSegmentId(segment.Id);
        }

        return new LabProject(full, manifest);
    }

    /// <summary>Returns a segment's state, or a not-started state when it has never run.</summary>
    /// <param name="id">Segment ID.</param>
    /// <returns>Its state.</returns>
    public LabSegmentState Segment(string id)
    {
        ValidateSegmentId(id);
        lock (_gate)
        {
            return Manifest.Segments.FirstOrDefault(segment => segment.Id == id)
                   ?? new LabSegmentState { Id = id, Status = LabSegmentStatus.NotStarted };
        }
    }

    /// <summary>Starts a new attempt of a segment and returns its empty evidence directory.</summary>
    /// <param name="id">Segment ID.</param>
    /// <param name="now">Start time.</param>
    /// <returns>The new attempt directory.</returns>
    /// <remarks>
    ///     Starting an attempt marks the segment not started until it finishes, so a crash mid-way
    ///     never leaves a segment reported as complete with a half-written attempt.
    /// </remarks>
    public string BeginAttempt(string id, DateTimeOffset now)
    {
        lock (_gate)
        {
            var current = Segment(id);
            var attempt = current.Attempts + 1;
            var directory = AttemptDirectory(id, attempt);
            System.IO.Directory.CreateDirectory(directory);
            Update(current with { Status = LabSegmentStatus.NotStarted, Attempts = attempt, UpdatedAt = now });
            return directory;
        }
    }

    /// <summary>Records how the current attempt ended.</summary>
    /// <param name="id">Segment ID.</param>
    /// <param name="status">Final status.</param>
    /// <param name="summary">One line for the stage list.</param>
    /// <param name="now">Finish time.</param>
    public void Finish(string id, LabSegmentStatus status, string? summary, DateTimeOffset now)
    {
        lock (_gate)
        {
            var current = Segment(id);
            if (current.Attempts == 0)
            {
                throw new InvalidOperationException($"Segment {id} has no attempt to finish.");
            }

            Update(current with { Status = status, Summary = summary, UpdatedAt = now });
        }
    }

    /// <summary>The evidence directory of the latest attempt, or null when the segment never ran.</summary>
    /// <param name="id">Segment ID.</param>
    /// <returns>The directory, or null.</returns>
    public string? CurrentAttemptDirectory(string id)
    {
        var state = Segment(id);
        return state.Attempts == 0 ? null : AttemptDirectory(id, state.Attempts);
    }

    /// <summary>Writes one new JSON evidence file into an attempt directory.</summary>
    /// <typeparam name="T">Value type.</typeparam>
    /// <param name="attemptDirectory">Directory from <see cref="BeginAttempt" />.</param>
    /// <param name="name">File name without extension: lower-case letters, digits and hyphens.</param>
    /// <param name="value">Value to write.</param>
    /// <returns>The file path.</returns>
    public string WriteEvidence<T>(string attemptDirectory, string name, T value)
    {
        // Names come from wizard code, not from the tester, so they are checked rather than rewritten:
        // a hashed name would let a repeated write create a second file instead of failing.
        if (name.Contains('/') || ValidateSegmentId(name) != name)
        {
            throw new ArgumentException($"Invalid evidence name '{name}'.", nameof(name));
        }

        var path = Path.Combine(attemptDirectory, name + ".json");
        if (!Path.GetFullPath(path)
                .StartsWith(Directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Evidence must stay inside the project.", nameof(attemptDirectory));
        }

        DurableFile.WriteNewText(path, JsonSerializer.Serialize(value, JsonOptions) + "\n");
        return path;
    }

    /// <summary>Changes the device identity.</summary>
    /// <param name="device">New identity.</param>
    public void SetDevice(LabDeviceIdentity device)
    {
        lock (_gate)
        {
            Manifest = Manifest with { Device = device };
            Save();
        }
    }

    /// <summary>Checks a segment ID: lower-case words, digits and hyphens, nested with slashes.</summary>
    /// <param name="id">Candidate ID.</param>
    /// <returns>The ID.</returns>
    public static string ValidateSegmentId(string id)
    {
        if (string.IsNullOrEmpty(id)
            || id.Split('/').Any(part => part.Length == 0
                                         || part.Any(c =>
                                             c is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-')))
        {
            throw new ArgumentException($"Invalid segment ID '{id}'.", nameof(id));
        }

        return id;
    }

    private string AttemptDirectory(string id, int attempt)
    {
        return Path.Combine([Directory, "segments", .. id.Split('/'), $"attempt-{attempt}"]);
    }

    private void Update(LabSegmentState state)
    {
        List<LabSegmentState> segments = [.. Manifest.Segments];
        var index = segments.FindIndex(segment => segment.Id == state.Id);
        if (index >= 0)
        {
            segments[index] = state;
        }
        else
        {
            segments.Add(state);
        }

        Manifest = Manifest with { Segments = segments };
        Save();
    }

    private void Save()
    {
        var target = Path.Combine(Directory, ManifestFileName);
        var staging = DurableFile.StagingPath(target);
        DurableFile.WriteNewText(staging, JsonSerializer.Serialize(Manifest, JsonOptions) + "\n");
        File.Move(staging, target, true);
    }
}
