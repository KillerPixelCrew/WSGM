using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace WSGM.AllyXLab;

internal sealed record RumbleBoundary(string Phase, int Motor, int? LowestFelt, int? FirstNotFelt, string Status, string Units);

internal sealed class RumblePhase
{
    private readonly int[] _levels;
    private int _index;
    internal bool Complete { get; private set; }
    internal int Current => !Complete ? _levels[_index] : throw new InvalidOperationException("Phase already completed.");
    internal int? LowestFelt { get; private set; }
    internal int? FirstNotFelt { get; private set; }
    internal RumblePhase(int[] levels)
    {
        if (levels.Length == 0 || levels.Any(x => x <= 0) || levels.Zip(levels.Skip(1)).Any(p => p.First <= p.Second))
        {
            throw new ArgumentException("Calibration levels must be positive and strictly descending.", nameof(levels));
        }

        _levels = (int[])levels.Clone();
    }
    internal void Answer(bool felt)
    {
        if (Complete)
        {
            throw new InvalidOperationException("No outstanding trial.");
        }

        if (!felt) { FirstNotFelt = Current; Complete = true; return; }
        LowestFelt = Current;
        Complete = ++_index == _levels.Length;
    }
    internal string Status => !Complete ? "incomplete" : LowestFelt is null ? "nothing-felt-at-ceiling"
        : FirstNotFelt is null ? "felt-at-lowest-tested-value" : "boundary-bracketed";
}

internal static class RumbleCalibration
{
    internal static readonly int[] Strengths = [50, 32, 20, 12, 8, 5, 3, 1];
    internal static readonly int[] Durations = [200, 120, 70, 40, 25, 15, 10, 5];

    internal static void Run(SafeFileHandle handle, HidEndpoint endpoint, SessionLog log, CancellationToken cancel)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        budget.CancelAfter(TimeSpan.FromMinutes(5));
        var token = budget.Token;
        var ask = Worker.Interaction ?? throw new InvalidOperationException("Calibration requires the attended wizard.");
        List<RumbleBoundary> boundaries = [];
        try
        {
            for (int motor = 0; motor < 2; motor++)
            {
                for (int phase = 0; phase < 3; phase++)
                {
                    string name = phase switch { 0 => "Sustained strength", 1 => "Short-tick strength", _ => "Shortest pulse" };
                    string method = phase switch
                    {
                        0 => "You will feel 1.5-second bursts, getting weaker.",
                        1 => "You will feel short 30 ms ticks, getting weaker.",
                        _ => "You will feel 50% strength pulses, getting shorter.",
                    };
                    string ready = ask(new { Title = $"Motor {motor + 1} of 2 · {name}", Text = method + " Make sure both motors are silent before pressing Ready. Hold the device normally. After each pulse, answer only once the motors are silent. Felt it advances to the next weaker/shorter pulse. Stop is always available.", Mode = "ready" }, token);
                    if (ready != "ready")
                    {
                        throw new OperationCanceledException("Calibration stopped by tester.");
                    }

                    var state = new RumblePhase(phase == 2 ? Durations : Strengths);
                    while (!state.Complete)
                    {
                        int value = state.Current;
                        int percent = phase == 2 ? 50 : value;
                        int duration = phase switch { 0 => 1500, 1 => 30, _ => value };
                        int repeats = 0;
                        while (true)
                        {
                            token.ThrowIfCancellationRequested();
                            Worker.Progress?.Invoke(new { Message = $"Motor {motor + 1} · {name} · {percent}% · {duration} ms — feel this pulse", Seconds = 0 });
                            Stopwatch time = Stopwatch.StartNew();
                            try
                            {
                                Hid.Output(handle, endpoint, [0x0D, 0x0F, 0, 0, (byte)(motor == 0 ? percent : 0), (byte)(motor == 1 ? percent : 0), 0xFF, 0, 0xEB], log);
                                if (token.WaitHandle.WaitOne(duration))
                                {
                                    token.ThrowIfCancellationRequested();
                                }
                            }
                            finally { Zero(handle, endpoint, log); }
                            log.Add("calibration-pulse", new { Motor = motor, Phase = name, Percent = percent, RequestedMilliseconds = duration, SoftwareWriteToZeroMilliseconds = time.Elapsed.TotalMilliseconds, Repeat = repeats });
                            string answer = ask(new { Title = $"Motor {motor + 1} · {name}", Text = $"Did you feel that pulse? ({percent}%, {duration} ms)\nAnswer after the motors are silent. A = felt, B = not felt. You can also use the buttons below.", Mode = "feedback", CanRepeat = repeats < 2 }, token);
                            if (answer == "repeat" && repeats++ < 2)
                            {
                                continue;
                            }

                            if (answer is not ("felt" or "not-felt"))
                            {
                                throw new OperationCanceledException("Tester stopped calibration or reported continuing vibration.");
                            }

                            log.Add("calibration-answer", new { Motor = motor, Phase = name, Percent = percent, Milliseconds = duration, Felt = answer == "felt", MotorsSilentConfirmed = true });
                            state.Answer(answer == "felt");
                            break;
                        }
                        if (!state.Complete && token.WaitHandle.WaitOne(350))
                        {
                            token.ThrowIfCancellationRequested();
                        }
                    }
                    var boundary = new RumbleBoundary(name, motor, state.LowestFelt, state.FirstNotFelt, state.Status, phase == 2 ? "milliseconds at 50% drive" : "percent drive");
                    boundaries.Add(boundary);
                    log.Add("rumble-boundary", boundary);
                }
            }
        }
        finally
        {
            log.Add("rumble-calibration-summary", new
            {
                CompletedPhases = boundaries.Count,
                Complete = boundaries.Count == 6,
                Boundaries = boundaries,
                Method = "Both motors independently; 50% ceiling; sustained bursts 1500 ms, strength ticks 30 ms, duration sweep at 50%. Lowest felt is a confirmed trial, not an exact threshold. Missing bounds and incomplete phases are never filled with defaults."
            });
        }
    }
    internal static void Zero(SafeFileHandle handle, HidEndpoint endpoint, SessionLog log) =>
        Hid.Output(handle, endpoint, [0x0D, 0x0F, 0, 0, 0, 0, 0xFF, 0, 0xEB], log);
}
