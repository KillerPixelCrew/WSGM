using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WSGM.AllyXLab;

internal enum ActionKind { Inventory, Capture, ReadPower, Tdp, Profile, Fan, Rumble, Rgb }

internal sealed record Request(ActionKind Action, string Label, int Value = 0, int Channel = 0,
    string Endpoint = "", int Seconds = 8, int PulseMilliseconds = 250);

internal sealed record LabEvent(double Milliseconds, string Kind, object Data);

internal sealed class SessionLog
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    internal List<LabEvent> Events { get; } = [];
    internal void Add(string kind, object data)
    {
        if (Events.Count >= 24000)
        {
            throw new InvalidOperationException("Capture event bound reached.");
        }

        Events.Add(new(_clock.Elapsed.TotalMilliseconds, kind, data));
    }
    internal static string Token(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}

internal sealed record Result(Request Request, string Outcome, string Cleanup, List<LabEvent> Events, string? Error);

internal static class Limits
{
    internal static bool Mutates(ActionKind kind) => kind is ActionKind.Tdp or ActionKind.Profile or ActionKind.Fan or ActionKind.Rumble or ActionKind.Rgb;
    internal static void Validate(Request request)
    {
        if (!Enum.IsDefined(request.Action) || request.Label.Length > 160 || request.Seconds is < 1 or > 20)
        {
            throw new InvalidOperationException("Invalid bounded action.");
        }

        if (request.Action == ActionKind.Tdp && request.Value is < 5 or > 25)
        {
            throw new InvalidOperationException("TDP must be 5–25 W.");
        }

        if (request.Action == ActionKind.Profile && request.Value is < 0 or > 2)
        {
            throw new InvalidOperationException("Unknown firmware profile.");
        }

        if (request.Action == ActionKind.Fan && (request.Value is < 10 or > 30 || request.Channel is < 0 or > 1))
        {
            throw new InvalidOperationException("Fan test permits a 10–30 percentage-point increase only.");
        }

        if (request.Action == ActionKind.Rumble && (request.Value is < 1 or > 50 || request.Channel is < 0 or > 1))
        {
            throw new InvalidOperationException("Rumble must select one motor at 1–50%.");
        }

        if (request.Action == ActionKind.Rumble && request.PulseMilliseconds is < 5 or > 2000)
        {
            throw new InvalidOperationException("Rumble pulse must be 5–2000 ms.");
        }

        if (request.Action == ActionKind.Rgb && (request.Value is < 0 or > 2 || request.Channel is < 0 or > 4))
        {
            throw new InvalidOperationException("Unknown RGB color or zone.");
        }
    }
}
