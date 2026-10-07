using System.Text.Json;
using System.Text.Json.Serialization;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Plugin.Ir;

/// <summary>A raw IR envelope plus diagnostic decode metadata; transmission uses the raw timings.</summary>
/// <param name="CarrierHz">Carrier frequency in hertz, from 20,000 through 60,000.</param>
/// <param name="TimingsUs">2 through 1,024 alternating mark/space durations in microseconds, beginning with a mark.</param>
/// <param name="CarrierSource">assumed, measured, protocol or manual; raw learning does not measure carrier frequency.</param>
/// <param name="Protocol">Optional decoder protocol name; does not replace the raw envelope.</param>
/// <param name="Address">Decoded address when available.</param>
/// <param name="Command">Decoded command when available.</param>
/// <param name="Bits">Decoder-reported bit count.</param>
/// <param name="Repeat">Whether the capture was classified as a protocol repeat frame.</param>
/// <remarks>The timing array is retained; clone it before editing a shared or persisted payload.</remarks>
internal sealed record IrPayload(
    int CarrierHz,
    int[] TimingsUs,
    string CarrierSource = "assumed",
    string? Protocol = null,
    uint Address = 0,
    uint Command = 0,
    int Bits = 0,
    bool Repeat = false)
{
    /// <summary>Checks transport bounds and the complete emission duration before dispatch.</summary>
    /// <param name="repeats">Additional transmissions after the first, from 0 through 4.</param>
    /// <param name="gapMs">Gap between repeated envelopes in milliseconds, from 0 through 200.</param>
    /// <exception cref="InvalidDataException">
    ///     A field is invalid, a timing falls outside 1 through 65,535 microseconds, the envelope exceeds
    ///     two seconds, or all emissions and gaps exceed five seconds.
    /// </exception>
    internal void Validate(int repeats = 0, int gapMs = 40)
    {
        if (CarrierHz is < 20000 or > 60000 || TimingsUs is not { Length: >= 2 and <= 1024 }
                                            || TimingsUs.Any(value => value is < 1 or > 65535) || repeats is < 0 or > 4
                                            || gapMs is < 0 or > 200
                                            || CarrierSource is not ("assumed" or "measured" or "protocol" or "manual"))
        {
            throw new InvalidDataException("IR payload exceeds endpoint limits.");
        }

        var duration = TimingsUs.Sum(value => (long)value);
        if (duration > 2_000_000 || duration * (repeats + 1) + gapMs * 1000L * repeats > 5_000_000)
        {
            throw new InvalidDataException("IR transmission exceeds five seconds.");
        }
    }
}

/// <summary>A named learned command with independently editable retransmission policy.</summary>
/// <param name="Id">Stable library identity.</param>
/// <param name="Device">Plain device group name.</param>
/// <param name="Name">Plain command name within the library.</param>
/// <param name="Payload">Original learned envelope and carrier evidence.</param>
/// <param name="Repeats">Additional sends after the first, from 0 through 4.</param>
/// <param name="GapMs">Gap between repeats in milliseconds, from 0 through 200.</param>
/// <param name="CarrierOverrideHz">Optional 20,000 through 60,000 hertz override; null preserves captured metadata.</param>
internal sealed record IrCommand(
    string Id,
    string Device,
    string Name,
    IrPayload Payload,
    int Repeats = 0,
    int GapMs = 40,
    int? CarrierOverrideHz = null)
{
    internal IrPayload TransmitPayload => CarrierOverrideHz is { } frequency
        ? Payload with { CarrierHz = frequency, CarrierSource = "manual" }
        : Payload;
}

/// <summary>One host-run scene step, resolved against the current command library.</summary>
/// <param name="CommandId">Existing command identity.</param>
/// <param name="DelayAfterMs">Delay after this step in milliseconds, from 0 through 5,000.</param>
internal sealed record IrSceneStep(string CommandId, int DelayAfterMs = 0);

/// <summary>A named ordered command sequence; uncertain emission stops later steps.</summary>
/// <param name="Id">Stable scene identity.</param>
/// <param name="Name">Plain display name.</param>
/// <param name="Steps">Nonempty ordered steps; the array is retained and must remain unchanged after publication.</param>
internal sealed record IrScene(string Id, string Name, IrSceneStep[] Steps);

/// <summary>The private versioned library persisted independently from network credentials.</summary>
/// <param name="Version">File schema version; currently exactly 1.</param>
/// <param name="Commands">Unique commands with validated original and effective transmission payloads.</param>
/// <param name="Scenes">Unique scenes referencing existing command IDs.</param>
/// <param name="SelectedCommandId">Existing selected command ID, or null for no explicit selection.</param>
internal sealed record IrLibrary(int Version, IrCommand[] Commands, IrScene[] Scenes, string? SelectedCommandId = null)
{
    internal static IrLibrary Empty => new(1, [], []);

    internal static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    internal void Validate()
    {
        if (Version != 1)
        {
            throw new InvalidDataException("Unsupported IR library version.");
        }

        HashSet<string> identities = new(StringComparer.Ordinal);
        foreach (var command in Commands)
        {
            // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            if (command is null || !ValidName(command.Id) || !identities.Add(command.Id)
                || !ValidName(command.Device) || !ValidName(command.Name) || command.Payload is null)
            {
                throw new InvalidDataException("Invalid or duplicate IR command.");
            }
            // ReSharper restore ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract

            command.Payload.Validate(command.Repeats, command.GapMs);
            command.TransmitPayload.Validate(command.Repeats, command.GapMs);
        }

        HashSet<string> scenes = new(StringComparer.Ordinal);
        if (SelectedCommandId is not null && !identities.Contains(SelectedCommandId))
        {
            throw new InvalidDataException("Selected IR command is absent.");
        }

        // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (Scenes.Any(scene => scene is null || !ValidName(scene.Id) || !scenes.Add(scene.Id) || !ValidName(scene.Name)
                                || scene.Steps is not { Length: > 0 }
                                || scene.Steps.Any(step => step is null || !identities.Contains(step.CommandId)
                                                                        || step.DelayAfterMs is < 0 or > 5000)))
            // ReSharper restore ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        {
            throw new InvalidDataException("Invalid IR scene or missing command.");
        }
    }

    private static bool ValidName(string? value)
    {
        return PlainText.TryValidate(value, "name", out _);
    }

    /// <summary>Loads the library, or an empty one when the file does not exist. An unreadable file throws.</summary>
    /// <param name="path">Command-library JSON path; pairing secrets are stored separately.</param>
    /// <param name="token">Cancels asynchronous file deserialization.</param>
    /// <returns>The validated library, or the shared empty library only when its file or parent folder is absent. Unreadable or invalid content throws.</returns>
    internal static async Task<IrLibrary> LoadAsync(string path, CancellationToken token)
    {
        return await JsonFile.ReadAsync<IrLibrary>(path, library => library.Validate(), token).ConfigureAwait(false)
               ?? Empty;
    }

    internal Task SaveAsync(string path, CancellationToken token)
    {
        Validate();
        return JsonFile.WriteAsync(path, this, token);
    }
}

/// <summary>
///     Network pairing the plugin minted over USB: the endpoint's mDNS host name, its last known address
///     and the shared token. Kept apart from the command library so backups never carry the secret.
/// </summary>
/// <param name="Token">Shared endpoint token, 16 through 64 characters without control characters; never log it.</param>
/// <param name="Hostname">Endpoint host name, at most 253 characters without whitespace.</param>
/// <param name="Ip">Last observed address, at most 64 characters.</param>
internal sealed record IrPairing(string Token, string Hostname, string Ip)
{
    internal void Validate()
    {
        if (Token is not { Length: >= 16 and <= 64 } || Token.Any(char.IsControl) || Hostname is not { Length: <= 253 }
            || Hostname.Any(char.IsWhiteSpace) || Ip is not { Length: <= 64 })
        {
            throw new InvalidDataException("Invalid IR endpoint pairing.");
        }
    }

    /// <summary>Loads the pairing, or null when the file does not exist. An unreadable file throws.</summary>
    /// <param name="path">Private pairing-state JSON path; its contents must not be logged or included in command-library backups.</param>
    /// <param name="token">Cancels asynchronous file deserialization.</param>
    /// <returns>Validated pairing information, or null only for a missing file/folder; unreadable or invalid content throws.</returns>
    internal static Task<IrPairing?> LoadAsync(string path, CancellationToken token)
    {
        return JsonFile.ReadAsync<IrPairing>(path, pairing => pairing.Validate(), token);
    }

    internal Task SaveAsync(string path, CancellationToken token)
    {
        Validate();
        return JsonFile.WriteAsync(path, this, token);
    }
}

internal static class JsonFile
{
    /// <summary>
    ///     Reads a state file. Only a missing file or folder reads as absent. A file that cannot be read, or that
    ///     does not parse or validate, throws without modifying the original.
    /// </summary>
    /// <typeparam name="T">Reference type deserialized with the IR state-file JSON options.</typeparam>
    /// <param name="path">JSON state path, opened read-only without modifying or replacing it.</param>
    /// <param name="validate">Validation callback run before returning a deserialized value; failures propagate.</param>
    /// <param name="token">Cancels deserialization; file handles are released on every exit.</param>
    /// <returns>The validated object, or null only when the file or parent directory does not exist.</returns>
    /// <exception cref="InvalidDataException">The JSON is null, malformed or rejected by validation.</exception>
    /// <exception cref="IOException">The file cannot be read; the original remains untouched.</exception>
    internal static async Task<T?> ReadAsync<T>(string path, Action<T> validate, CancellationToken token)
        where T : class
    {
        var name = Path.GetFileName(path);
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                FileOptions.Asynchronous);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"{name} could not be read, and nothing was changed: {ex.Message}", ex);
        }

        await using (stream)
        {
            try
            {
                var value = await JsonSerializer.DeserializeAsync<T>(stream, IrLibrary.Json, token)
                                .ConfigureAwait(false)
                            ?? throw new InvalidDataException("The file is empty.");
                validate(value);
                return value;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                throw new InvalidDataException($"{name} is corrupt, and nothing was changed: {ex.Message}", ex);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"{name} could not be read, and nothing was changed: {ex.Message}", ex);
            }
        }
    }

    /// <summary>Writes through a temporary sibling and moves it into place, so a failure leaves the previous file intact.</summary>
    /// <typeparam name="T">Serializable state type written with the IR JSON options.</typeparam>
    /// <param name="path">Destination JSON path; a missing parent directory is created.</param>
    /// <param name="value">Value to serialize; callers validate it before writing.</param>
    /// <param name="token">Cancels serialization/flushing and is rechecked before replacement; cannot undo a completed move.</param>
    /// <returns>A task completing after the temporary sibling replaces the destination; exceptions propagate and temporary cleanup is attempted.</returns>
    internal static async Task WriteAsync<T>(string path, T value, CancellationToken token)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, IrLibrary.Json, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
            File.Move(temporary, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
