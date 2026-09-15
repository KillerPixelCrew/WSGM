using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WSGM.AllyXLab;

internal enum ActionKind { Inventory, Capture, ReadPower, Tdp, Profile, Fan, RumbleCalibration, Rgb, RumbleProbe }

internal sealed record Request(ActionKind Action, string Label, int Value = 0, int Channel = 0,
    string Endpoint = "", int Seconds = 8, int? ExpectedAc = null, bool Motion = false);

internal sealed record LabEvent(double Milliseconds, string Kind, object Data);

internal sealed class SessionLog(int maxEvents = 24000)
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    internal List<LabEvent> Events { get; } = [];
    internal double Now => _clock.Elapsed.TotalMilliseconds;
    // Sources report from hooks, the UI thread and WMI callbacks, so appends are serialized.
    internal void Add(string kind, object data)
    {
        lock (Events)
        {
            if (Events.Count >= maxEvents)
            {
                throw new InvalidOperationException("Capture event bound reached.");
            }

            Events.Add(new(_clock.Elapsed.TotalMilliseconds, kind, data));
        }
    }
    internal static string Token(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}

internal sealed record Result(Request Request, string Outcome, string Cleanup, List<LabEvent> Events, string? Error);

internal static class Limits
{
    internal static bool Mutates(ActionKind kind) => kind is ActionKind.Tdp or ActionKind.Profile or ActionKind.Fan or ActionKind.RumbleCalibration or ActionKind.Rgb or ActionKind.RumbleProbe;
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

        if (request.Action is ActionKind.RumbleCalibration or ActionKind.RumbleProbe && (request.Value != 0 || request.Channel != 0))
        {
            throw new InvalidOperationException("Guided calibration chooses both motors and all bounded pulse settings.");
        }

        if (request.ExpectedAc is < 0 or > 1)
        {
            throw new InvalidOperationException("Invalid expected power source.");
        }

        if (request.Action == ActionKind.Rgb && (request.Value is < 0 or > 2 || request.Channel is < 0 or > 4))
        {
            throw new InvalidOperationException("Unknown RGB color or zone.");
        }
    }
}
