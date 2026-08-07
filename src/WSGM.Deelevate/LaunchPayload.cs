using System.Collections;
using System.Text;

namespace WSGM.Deelevate;

internal sealed record LaunchPayload(
    string WorkingDirectory,
    string[] Arguments,
    KeyValuePair<string, string>[] EnvironmentVariables)
{
    private const int ProtocolVersion = 1;
    private const int MaxStringBytes = 4 * 1024 * 1024;
    private const int MaxArguments = 16_384;
    private const int MaxEnvironmentVariables = 16_384;

    internal static LaunchPayload Capture(string[] arguments)
    {
        var environment = new List<KeyValuePair<string, string>>();
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                environment.Add(KeyValuePair.Create(key, value));
            }
        }

        return new LaunchPayload(Environment.CurrentDirectory, arguments, [.. environment]);
    }

    internal async Task WriteAsync(Stream stream, CancellationToken cancellationToken)
    {
        await PipeProtocol.WriteInt32Async(stream, ProtocolVersion, cancellationToken);
        await PipeProtocol.WriteStringAsync(stream, WorkingDirectory, cancellationToken);
        await PipeProtocol.WriteInt32Async(stream, Arguments.Length, cancellationToken);
        foreach (var argument in Arguments)
        {
            await PipeProtocol.WriteStringAsync(stream, argument, cancellationToken);
        }

        await PipeProtocol.WriteInt32Async(stream, EnvironmentVariables.Length, cancellationToken);
        foreach (var pair in EnvironmentVariables)
        {
            await PipeProtocol.WriteStringAsync(stream, pair.Key, cancellationToken);
            await PipeProtocol.WriteStringAsync(stream, pair.Value, cancellationToken);
        }
    }

    internal static async Task<LaunchPayload> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var version = await PipeProtocol.ReadInt32Async(stream, cancellationToken);
        if (version != ProtocolVersion)
        {
            throw new InvalidDataException($"Unsupported launch payload version {version}.");
        }

        var workingDirectory = await PipeProtocol.ReadStringAsync(stream, MaxStringBytes, cancellationToken);
        var argumentCount = await ReadCountAsync(stream, MaxArguments, "argument", cancellationToken);
        var arguments = new string[argumentCount];
        for (var i = 0; i < argumentCount; i++)
        {
            arguments[i] = await PipeProtocol.ReadStringAsync(stream, MaxStringBytes, cancellationToken);
        }

        var environmentCount = await ReadCountAsync(
            stream, MaxEnvironmentVariables, "environment variable", cancellationToken);
        var environment = new KeyValuePair<string, string>[environmentCount];
        for (var i = 0; i < environmentCount; i++)
        {
            var key = await PipeProtocol.ReadStringAsync(stream, MaxStringBytes, cancellationToken);
            var value = await PipeProtocol.ReadStringAsync(stream, MaxStringBytes, cancellationToken);
            environment[i] = KeyValuePair.Create(key, value);
        }

        return new LaunchPayload(workingDirectory, arguments, environment);
    }

    private static async Task<int> ReadCountAsync(
        Stream stream,
        int maximum,
        string description,
        CancellationToken cancellationToken)
    {
        var count = await PipeProtocol.ReadInt32Async(stream, cancellationToken);
        if (count < 0 || count > maximum)
        {
            throw new InvalidDataException($"Invalid {description} count {count}.");
        }
        return count;
    }
}

internal static class PipeProtocol
{
    internal static async Task WriteInt32Async(
        Stream stream,
        int value,
        CancellationToken cancellationToken)
    {
        var bytes = BitConverter.GetBytes(value);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    internal static async Task<int> ReadInt32Async(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return BitConverter.ToInt32(bytes);
    }

    internal static async Task WriteStringAsync(
        Stream stream,
        string value,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        await WriteInt32Async(stream, bytes.Length, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    internal static async Task<string> ReadStringAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var length = await ReadInt32Async(stream, cancellationToken);
        if (length < 0 || length > maximumBytes)
        {
            throw new InvalidDataException($"Invalid string length {length}.");
        }

        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return Encoding.UTF8.GetString(bytes);
    }
}
