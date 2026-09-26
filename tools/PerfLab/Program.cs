using System.Globalization;
using System.Text;
using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Cpu;
using Microsoft.Windows.EventTracing.Symbols;

namespace WSGM.Tools.PerfLab;

/// <summary>
///     Reduces one ETW trace to the numbers docs/perf tracks: CPU time and context switches per
///     process, and for the processes WSGM ships or drives, per-thread cost, the sampled stacks that
///     spent the CPU, the wait sites that produced the wakeups, and the .NET exceptions thrown.
/// </summary>
internal static class Program
{
    private static readonly string[] DefaultFocus =
    [
        "WSGM.exe",
        "WSGM.Launch.exe",
        "WSGM.PackagedLaunch.exe",
        "WSGM.LogonService.exe",
        "steam.exe",
        "steamwebhelper.exe",
        "RTSS.exe",
        "RTSSHooksLoader64.exe",
        "LHMDataProvider.exe"
    ];

    /// <summary>Images whose frames count as "ours" when attributing a sample to a WSGM frame.</summary>
    private static readonly string[] OwnImages =
    [
        "WSGM",
        "SteamUiToolkit",
        "WindowsDeviceControl",
        "libviiper",
        "steam_input_lease",
        "Avalonia.LiveBackdrop"
    ];

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine(
                "usage: WSGM.PerfLab <trace.etl> [--out report.md] [--focus image.exe]... [--top N] [--no-symbols]");
            return 2;
        }

        var tracePath = args[0];
        string? outPath = null;
        List<string> focus = [];
        var top = 15;
        var loadSymbols = true;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out":
                    outPath = args[++i];
                    break;
                case "--focus":
                    focus.Add(args[++i]);
                    break;
                case "--top":
                    top = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--no-symbols":
                    loadSymbols = false;
                    break;
                default:
                    Console.Error.WriteLine($"unknown option {args[i]}");
                    return 2;
            }
        }

        if (focus.Count == 0)
        {
            focus.AddRange(DefaultFocus);
        }

        var report = await Analyze(tracePath, focus, top, loadSymbols);
        if (outPath is null)
        {
            Console.Write(report);
        }
        else
        {
            await File.WriteAllTextAsync(outPath, report, new UTF8Encoding(false));
            Console.WriteLine($"wrote {outPath}");
        }

        return 0;
    }

    private static async Task<string> Analyze(string tracePath, List<string> focus, int top, bool loadSymbols)
    {
        using var trace = TraceProcessor.Create(tracePath, new TraceProcessorSettings { AllowLostEvents = true });
        var cpuSamples = trace.UseCpuSamplingData();
        var contextSwitches = trace.UseContextSwitchData();
        var genericEvents = trace.UseGenericEvents();
        var symbols = trace.UseSymbols();
        trace.Process();

        if (loadSymbols)
        {
            Console.Error.WriteLine("loading symbols...");
            await symbols.Result.LoadSymbolsAsync(SymCachePath.Automatic, SymbolPath.Automatic);
        }

        var duration = SampleSpanSeconds(cpuSamples.Result);
        StringBuilder md = new();
        md.AppendLine(CultureInfo.InvariantCulture, $"# Trace {Path.GetFileName(tracePath)}");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"Duration {duration:F1} s. Samples and switches are whole-trace totals; rates are per second of trace.");
        md.AppendLine();

        // Per-process CPU and context switches.
        Dictionary<(string Image, int Pid), double> processCpu = new();
        Dictionary<(string Image, int Pid), long> processSwitches = new();
        Dictionary<(int Pid, int Tid), double> threadCpu = new();
        Dictionary<(int Pid, int Tid), long> threadSwitches = new();
        Dictionary<(int Pid, int Tid), string> threadNames = new();
        Dictionary<string, Dictionary<string, double>> selfByFocus = new();
        Dictionary<string, Dictionary<string, double>> ownByFocus = new();
        Dictionary<string, Dictionary<string, double>> stacksByFocus = new();
        Dictionary<string, Dictionary<string, long>> waitSitesByFocus = new();
        Dictionary<string, Dictionary<string, long>> exceptionsByFocus = new();
        Dictionary<string, Dictionary<string, long>> exceptionStacksByFocus = new();
        Dictionary<(int Pid, int Tid), Dictionary<string, double>> threadStacks = new();
        Dictionary<(int Pid, int Tid), Dictionary<string, double>> threadWaits = new();
        double totalCpu = 0;

        static bool IsFocus(List<string> focus, string? image)
        {
            return image is not null && focus.Any(f => string.Equals(f, image, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var sample in cpuSamples.Result.Samples)
        {
            var image = sample.Process?.ImageName ?? "?";
            var pid = sample.Process?.Id ?? -1;
            var ms = (double)sample.Weight.TotalMilliseconds;
            totalCpu += ms;
            processCpu[(image, pid)] = processCpu.GetValueOrDefault((image, pid)) + ms;
            if (!IsFocus(focus, image))
            {
                continue;
            }

            var tid = sample.Thread?.Id ?? -1;
            threadCpu[(pid, tid)] = threadCpu.GetValueOrDefault((pid, tid)) + ms;
            if (sample.Thread?.Name is { Length: > 0 } name)
            {
                threadNames[(pid, tid)] = name;
            }

            var frames = Describe(sample.Stack);
            Add<double>(selfByFocus, image, frames.Count > 0 ? frames[0] : "?", ms);
            var own = frames.FirstOrDefault(f => OwnImages.Any(o => f.StartsWith(o, StringComparison.OrdinalIgnoreCase)));
            Add<double>(ownByFocus, image, own ?? "(no WSGM frame)", ms);
            var collapsed = Collapse(frames, 14);
            Add<double>(stacksByFocus, image, collapsed, ms);
            AddThread(threadStacks, (pid, tid), collapsed, ms);
        }

        foreach (var cs in contextSwitches.Result.ContextSwitches)
        {
            var image = cs.SwitchIn.Process?.ImageName ?? "?";
            var pid = cs.SwitchIn.Process?.Id ?? -1;
            processSwitches[(image, pid)] = processSwitches.GetValueOrDefault((image, pid)) + 1;
            if (!IsFocus(focus, image))
            {
                continue;
            }

            var tid = cs.SwitchIn.Thread?.Id ?? -1;
            threadSwitches[(pid, tid)] = threadSwitches.GetValueOrDefault((pid, tid)) + 1;
            if (cs.SwitchIn.Thread?.Name is { Length: > 0 } name)
            {
                threadNames[(pid, tid)] = name;
            }

            var frames = Describe(cs.SwitchIn.Stack);
            var collapsed = Collapse(frames, 12);
            Add(waitSitesByFocus, image, collapsed, 1L);
            AddThread(threadWaits, (pid, tid), collapsed, 1);
        }

        foreach (var ev in genericEvents.Result.Events)
        {
            if (ev.ProviderName != "Microsoft-Windows-DotNETRuntime" || ev.Id != 80)
            {
                continue;
            }

            var image = ev.Process?.ImageName ?? "?";
            if (!IsFocus(focus, image))
            {
                continue;
            }

            var type = ev.Fields.FirstOrDefault(f => f.Name == "ExceptionType")?.AsString ?? "?";
            var message = ev.Fields.FirstOrDefault(f => f.Name == "ExceptionMessage")?.AsString;
            Add(exceptionsByFocus, image, string.IsNullOrEmpty(message) ? type : $"{type}: {message}", 1);
            Add(exceptionStacksByFocus, image, type + " @ " + Collapse(Describe(ev.Stack), 12), 1);
        }

        md.AppendLine("## Processes");
        md.AppendLine();
        md.AppendLine("| Process | PID | CPU ms | CPU % of one core | Context switches | Switches/s |");
        md.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: |");
        var keys = processCpu.Keys.Union(processSwitches.Keys)
            .OrderByDescending(k => processCpu.GetValueOrDefault(k))
            .Take(40);
        foreach (var key in keys)
        {
            var cpu = processCpu.GetValueOrDefault(key);
            var sw = processSwitches.GetValueOrDefault(key);
            md.AppendLine(CultureInfo.InvariantCulture,
                $"| {key.Image} | {key.Pid} | {cpu:F0} | {(duration > 0 ? cpu / duration / 10 : 0):F2} | {sw} | {(duration > 0 ? sw / duration : 0):F0} |");
        }

        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"All processes: {totalCpu:F0} ms CPU, {(duration > 0 ? totalCpu / duration / 10 : 0):F1} % of one core.");

        foreach (var image in focus)
        {
            var pids = processCpu.Keys.Where(k => string.Equals(k.Image, image, StringComparison.OrdinalIgnoreCase)).Select(k => k.Pid)
                .Union(processSwitches.Keys.Where(k => string.Equals(k.Image, image, StringComparison.OrdinalIgnoreCase)).Select(k => k.Pid))
                .ToList();
            if (pids.Count == 0)
            {
                continue;
            }

            md.AppendLine();
            md.AppendLine(CultureInfo.InvariantCulture, $"## {image}");
            md.AppendLine();
            md.AppendLine("### Threads");
            md.AppendLine();
            md.AppendLine("| PID | TID | Name | CPU ms | Switches | Switches/s |");
            md.AppendLine("| ---: | ---: | --- | ---: | ---: | ---: |");
            var threads = threadCpu.Keys.Union(threadSwitches.Keys)
                .Where(k => pids.Contains(k.Pid))
                .OrderByDescending(k => threadCpu.GetValueOrDefault(k))
                .Take(top);
            foreach (var t in threads)
            {
                var sw = threadSwitches.GetValueOrDefault(t);
                md.AppendLine(CultureInfo.InvariantCulture,
                    $"| {t.Pid} | {t.Tid} | {threadNames.GetValueOrDefault(t, "")} | {threadCpu.GetValueOrDefault(t):F0} | {sw} | {(duration > 0 ? sw / duration : 0):F0} |");
            }

            var image1 = image;
            var ownKey = selfByFocus.Keys.FirstOrDefault(k => string.Equals(k, image1, StringComparison.OrdinalIgnoreCase));
            if (ownKey is not null)
            {
                Section(md, "CPU by WSGM frame (inclusive, first own frame from the leaf)", ownByFocus[ownKey], top, "ms");
                Section(md, "CPU by leaf function", selfByFocus[ownKey], top, "ms");
                Section(md, "Hottest sampled stacks (leaf first)", stacksByFocus[ownKey], top, "ms");
            }

            var waitKey = waitSitesByFocus.Keys.FirstOrDefault(k => string.Equals(k, image1, StringComparison.OrdinalIgnoreCase));
            if (waitKey is not null)
            {
                Section(md, "Wakeups by wait site (switch-in stack, leaf first)", waitSitesByFocus[waitKey].ToDictionary(p => p.Key, p => (double)p.Value), top, "switches");
            }

            var exKey = exceptionsByFocus.Keys.FirstOrDefault(k => string.Equals(k, image1, StringComparison.OrdinalIgnoreCase));
            if (exKey is not null)
            {
                Section(md, ".NET exceptions thrown", exceptionsByFocus[exKey].ToDictionary(p => p.Key, p => (double)p.Value), top, "thrown");
                Section(md, ".NET exception throw sites", exceptionStacksByFocus[exKey].ToDictionary(p => p.Key, p => (double)p.Value), Math.Min(top, 5), "thrown");
            }

            // The threads that matter: the busiest by CPU and the most woken, each with what it ran
            // and what it was waiting on.
            var hotThreads = threadCpu.Keys.Where(k => pids.Contains(k.Pid))
                .OrderByDescending(k => threadCpu[k]).Take(8)
                .Union(threadSwitches.Keys.Where(k => pids.Contains(k.Pid))
                    .OrderByDescending(k => threadSwitches[k]).Take(5))
                .ToList();
            foreach (var t in hotThreads)
            {
                md.AppendLine();
                md.AppendLine(CultureInfo.InvariantCulture,
                    $"### Thread {t.Tid} {threadNames.GetValueOrDefault(t, "")}: {threadCpu.GetValueOrDefault(t):F0} ms, {threadSwitches.GetValueOrDefault(t)} switches");
                if (threadStacks.TryGetValue(t, out var stacks))
                {
                    Section(md, $"Thread {t.Tid} hottest stacks", stacks, 4, "ms");
                }

                if (threadWaits.TryGetValue(t, out var waits))
                {
                    Section(md, $"Thread {t.Tid} wait sites", waits, 4, "switches");
                }
            }
        }

        return md.ToString();
    }

    private static void AddThread(
        Dictionary<(int Pid, int Tid), Dictionary<string, double>> byThread,
        (int Pid, int Tid) thread,
        string key,
        double weight)
    {
        if (!byThread.TryGetValue(thread, out var inner))
        {
            inner = new Dictionary<string, double>();
            byThread[thread] = inner;
        }

        inner[key] = inner.GetValueOrDefault(key) + weight;
    }

    private static void Add<T>(Dictionary<string, Dictionary<string, T>> byImage, string image, string key, T weight)
        where T : struct, System.Numerics.IAdditionOperators<T, T, T>
    {
        if (!byImage.TryGetValue(image, out var inner))
        {
            inner = new Dictionary<string, T>();
            byImage[image] = inner;
        }

        inner[key] = inner.TryGetValue(key, out var current) ? current + weight : weight;
    }

    private static void Section(StringBuilder md, string title, Dictionary<string, double> rows, int top, string unit)
    {
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"### {title}");
        md.AppendLine();
        var total = rows.Values.Sum();
        foreach (var (key, value) in rows.OrderByDescending(p => p.Value).Take(top))
        {
            var share = total > 0 ? 100 * value / total : 0;
            if (key.Contains('\n'))
            {
                md.AppendLine(CultureInfo.InvariantCulture, $"- {value:F0} {unit} ({share:F1} %)");
                md.AppendLine();
                md.AppendLine("  ```text");
                foreach (var line in key.Split('\n'))
                {
                    md.AppendLine("  " + line);
                }

                md.AppendLine("  ```");
            }
            else
            {
                md.AppendLine(CultureInfo.InvariantCulture, $"- {value:F0} {unit} ({share:F1} %) `{key}`");
            }
        }
    }

    private static List<string> Describe(IStackSnapshot? stack)
    {
        List<string> frames = [];
        if (stack is null)
        {
            return frames;
        }

        foreach (var frame in stack.Frames)
        {
            var image = frame.Image?.FileName ?? "?";
            var symbol = frame.Symbol;
            frames.Add(symbol is not null
                ? $"{image}!{symbol.FunctionName}"
                : $"{image}!0x{frame.RelativeVirtualAddress.Value:X}");
        }

        return frames;
    }

    /// <summary>Seconds between the first and last CPU sample, which is the window every rate uses.</summary>
    private static double SampleSpanSeconds(ICpuSampleDataSource samples)
    {
        TraceTimestamp? first = null;
        TraceTimestamp? last = null;
        foreach (var sample in samples.Samples)
        {
            if (first is null || sample.Timestamp < first.Value)
            {
                first = sample.Timestamp;
            }

            if (last is null || sample.Timestamp > last.Value)
            {
                last = sample.Timestamp;
            }
        }

        return first is not null && last is not null && last.Value > first.Value
            ? (double)(last.Value - first.Value).TotalSeconds
            : 0;
    }

    /// <summary>
    ///     The first user-mode frames of a stack, leaf first. Kernel frames are dropped because every
    ///     wait and every sample enters through the same dozen scheduler and system-call frames, which
    ///     would otherwise be all a ten-frame prefix ever shows.
    /// </summary>
    private static string Collapse(List<string> frames, int depth)
    {
        if (frames.Count == 0)
        {
            return "(no stack)";
        }

        var user = frames.Where(f => !IsKernel(f)).Take(depth).ToList();
        return user.Count == 0 ? "(kernel only)" : string.Join('\n', user);
    }

    private static bool IsKernel(string frame)
    {
        var bang = frame.IndexOf('!');
        var image = bang < 0 ? frame : frame[..bang];
        return image.Equals("ntoskrnl.exe", StringComparison.OrdinalIgnoreCase)
               || image.EndsWith(".sys", StringComparison.OrdinalIgnoreCase);
    }
}
