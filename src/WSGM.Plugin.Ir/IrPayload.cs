using System.Text.Json;

namespace WSGM.Plugin.Ir;

internal sealed record IrPayload(int CarrierHz, int[] TimingsUs, string CarrierSource = "assumed",
    string? Protocol = null, uint Address = 0, uint Command = 0, int Bits = 0, bool Repeat = false)
{
    internal void Validate(int repeats = 0, int gapMs = 40)
    {
        if (CarrierHz is < 20000 or > 60000 || TimingsUs is not { Length: >= 2 and <= 1024 }
            || TimingsUs.Any(value => value is < 1 or > 65535) || repeats is < 0 or > 4
            || gapMs is < 0 or > 200 || Protocol?.Length > 128
            || CarrierSource is not ("assumed" or "measured" or "protocol" or "manual"))
        {
            throw new InvalidDataException("IR payload exceeds endpoint limits.");
        }
        long duration = TimingsUs.Sum(value => (long)value);
        if (duration > 2_000_000 || duration * (repeats + 1) + gapMs * 1000L * repeats > 5_000_000)
        {
            throw new InvalidDataException("IR transmission exceeds five seconds.");
        }
    }
}

internal sealed record IrCommand(string Id, string Device, string Name, IrPayload Payload, int Repeats = 0,
    int GapMs = 40, int? CarrierOverrideHz = null)
{
    internal IrPayload TransmitPayload => CarrierOverrideHz is { } frequency
        ? Payload with { CarrierHz = frequency, CarrierSource = "manual" } : Payload;
}
internal sealed record IrSceneStep(string CommandId, int DelayAfterMs = 0);
internal sealed record IrScene(string Id, string Name, IrSceneStep[] Steps);
internal sealed record IrLibrary(int Version, IrCommand[] Commands, IrScene[] Scenes)
{
    internal static IrLibrary Empty => new(1, [], []);
    internal static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 16,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    internal void Validate()
    {
        if (Version != 1 || Commands is null || Scenes is null || Commands.Length > 4096 || Scenes.Length > 1024)
        {
            throw new InvalidDataException("Unsupported or oversized IR library.");
        }
        HashSet<string> identities = new(StringComparer.Ordinal);
        foreach (IrCommand command in Commands)
        {
            if (command is null || !ValidName(command.Id) || !identities.Add(command.Id)
                || !ValidName(command.Device) || !ValidName(command.Name) || command.Payload is null)
            {
                throw new InvalidDataException("Invalid or duplicate IR command.");
            }
            command.Payload.Validate(command.Repeats, command.GapMs);
            command.TransmitPayload.Validate(command.Repeats, command.GapMs);
        }
        HashSet<string> scenes = new(StringComparer.Ordinal);
        foreach (IrScene scene in Scenes)
        {
            if (scene is null || !ValidName(scene.Id) || !scenes.Add(scene.Id) || !ValidName(scene.Name)
                || scene.Steps is not { Length: > 0 and <= 32 }
                || scene.Steps.Any(step => step is null || !identities.Contains(step.CommandId)
                    || step.DelayAfterMs is < 0 or > 5000))
            {
                throw new InvalidDataException("Invalid IR scene or missing command.");
            }
        }
    }

    private static bool ValidName(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128 && !value.Any(char.IsControl);

    internal static async Task<IrLibrary> LoadAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) { return Empty; }
        if (new FileInfo(path).Length > 32 * 1024 * 1024) { throw new InvalidDataException("IR library exceeds 32 MiB."); }
        await using FileStream stream = File.OpenRead(path);
        IrLibrary library = await JsonSerializer.DeserializeAsync<IrLibrary>(stream, Json, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("IR library is empty.");
        library.Validate();
        return library;
    }

    internal async Task SaveAsync(string path, CancellationToken token)
    {
        Validate();
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, this, Json, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }
}
