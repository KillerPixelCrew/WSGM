using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace WSGM.DeviceLab.Knowledge;

/// <summary>The device knowledge records compiled into Device Lab.</summary>
internal sealed class DeviceKnowledgeBase
{
    /// <summary>The only record schema this build reads.</summary>
    public const int SchemaVersion = 1;

    private const string ResourcePrefix = "WSGM.DeviceLab.Knowledge.Devices.";

    private static readonly Lazy<DeviceKnowledgeBase> Embedded = new(LoadEmbedded);

    private static readonly HashSet<string> Axes = ["X", "Y", "Z"];

    // Reflection rather than the source-generated context: generated code sets every init-only
    // member at construction, so a list a sparse record omits would arrive as null instead of
    // keeping its empty default. An unknown member is a broken record, not something to skip.
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private DeviceKnowledgeBase(IReadOnlyList<DeviceKnowledgeRecord> records)
    {
        Records = records;
    }

    /// <summary>The records in this build, ordered by ID.</summary>
    public IReadOnlyList<DeviceKnowledgeRecord> Records { get; }

    /// <summary>The records compiled into this assembly.</summary>
    public static DeviceKnowledgeBase Default => Embedded.Value;

    /// <summary>Builds and validates a knowledge base from records.</summary>
    /// <param name="records">Records to admit.</param>
    /// <returns>The validated knowledge base.</returns>
    /// <exception cref="InvalidDataException">A record is malformed or the set is inconsistent.</exception>
    public static DeviceKnowledgeBase Create(IEnumerable<DeviceKnowledgeRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var ordered = records.OrderBy(record => record.Id, StringComparer.Ordinal).ToArray();
        Validate(ordered);
        return new DeviceKnowledgeBase(ordered);
    }

    /// <summary>Reads one record, rejecting unknown members.</summary>
    /// <param name="json">Record JSON.</param>
    /// <returns>The record.</returns>
    /// <exception cref="InvalidDataException">The JSON is not a record.</exception>
    public static DeviceKnowledgeRecord Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<DeviceKnowledgeRecord>(json, ReadOptions)
                   ?? throw new InvalidDataException("A knowledge record cannot be null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid knowledge record: {ex.Message}", ex);
        }
    }

    private static DeviceKnowledgeBase LoadEmbedded()
    {
        var assembly = typeof(DeviceKnowledgeBase).Assembly;
        List<DeviceKnowledgeRecord> records = [];
        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                                    && name.EndsWith(".json", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            records.Add(ReadResource(assembly, name));
        }

        return Create(records);
    }

    private static DeviceKnowledgeRecord ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidDataException($"Knowledge resource {name} is missing.");
        using var reader = new StreamReader(stream);
        try
        {
            return Parse(reader.ReadToEnd());
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"{name}: {ex.Message}", ex);
        }
    }

    private static void Validate(IReadOnlyList<DeviceKnowledgeRecord> records)
    {
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (record.SchemaVersion != SchemaVersion)
            {
                throw new InvalidDataException(
                    $"{record.Id}: schema version {record.SchemaVersion} is not {SchemaVersion}.");
            }

            if (string.IsNullOrWhiteSpace(record.Id) || !ids.Add(record.Id))
            {
                throw new InvalidDataException($"Knowledge record ID '{record.Id}' is empty or repeated.");
            }

            if (record.Identity.Count == 0)
            {
                throw new InvalidDataException($"{record.Id}: a record needs at least one identity rule.");
            }

            foreach (var rule in record.Identity)
            {
                if (string.IsNullOrWhiteSpace(rule.BaseboardManufacturer)
                    && string.IsNullOrWhiteSpace(rule.SystemModel))
                {
                    throw new InvalidDataException(
                        $"{record.Id}: an identity rule needs a baseboard manufacturer or a system model.");
                }
            }

            if (record.Status is DeviceKnowledgeStatus.Extracted && record.Supersedes.Count > 0)
            {
                throw new InvalidDataException($"{record.Id}: only a curated record can supersede another.");
            }

            ValidateAxisMap(record.Id, "gyrometer", record.Motion?.Gyrometer);
            ValidateAxisMap(record.Id, "accelerometer", record.Motion?.Accelerometer);
        }

        foreach (var record in records)
        {
            foreach (var superseded in record.Supersedes)
            {
                var target = records.FirstOrDefault(item => item.Id == superseded)
                             ?? throw new InvalidDataException(
                                 $"{record.Id}: supersedes unknown record '{superseded}'.");
                if (target.Status is not DeviceKnowledgeStatus.Extracted)
                {
                    throw new InvalidDataException(
                        $"{record.Id}: may supersede only an extracted record, not '{superseded}'.");
                }
            }
        }
    }

    // HC's own data holds an axis map that sends two raw axes to the same output (OneXPlayerApex).
    // The record keeps what HC applies so the wizard can report the disagreement, so the check is
    // that the keys and signs are well formed, not that the map is a permutation.
    private static void ValidateAxisMap(string id, string kind, DeviceAxisMap? map)
    {
        if (map is null)
        {
            return;
        }

        if (map.Swap.Count != 3
            || !map.Swap.Keys.All(Axes.Contains)
            || !map.Swap.Values.All(Axes.Contains)
            || map.Sign.Count != 3
            || !map.Sign.Keys.All(Axes.Contains)
            || !map.Sign.Values.All(sign => sign is 1 or -1))
        {
            throw new InvalidDataException($"{id}: the {kind} axis map is malformed.");
        }
    }
}
