using System.Text.Json;
using System.Text.Json.Serialization;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Plugin.Ir;

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

internal sealed record IrSceneStep(string CommandId, int DelayAfterMs = 0);

internal sealed record IrScene(string Id, string Name, IrSceneStep[] Steps);

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
    ///     does not parse or validate, throws and stays as it is, so no later save replaces it.
    /// </summary>
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
