using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace Wsgm.UwpSpike;

internal static class Program
{
    [STAThread]
    internal static int Main(string[] args)
    {
        var options = Options.Parse(args, out var error);
        if (options.HelpRequested)
        {
            Console.WriteLine(Options.Usage);
            return 0;
        }

        if (error is not null)
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine();
            Console.Error.WriteLine(Options.Usage);
            return 2;
        }

        using var log = new SpikeLog(options.LogPath);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        if (options.HideConsole)
        {
            var console = Native.GetConsoleWindow();
            if (console != IntPtr.Zero)
            {
                Native.ShowWindow(console, Native.SwHide);
            }
        }

        WriteContext(options, log);

        log.Section("Activation");
        log.Info($"mode: {options.Mode.ToString().ToLowerInvariant()}   target: {options.Aumid ?? options.Target}");
        var result = Activation.Launch(options, log);
        log.Info(result.Detail);
        if (!result.Succeeded)
        {
            log.Error("Activation failed; the wrapper is exiting so Steam does not sit in a running state forever.");
            return 3;
        }

        log.Section("Supervision");
        var supervisor = new Supervisor(options, log);
        var completed = supervisor.Run(result.SeedPid, cancellation.Token);

        log.Section("Result");
        log.Info(completed ? "Game session ended; wrapper exiting with 0." : "Wrapper exiting without a completed game session.");
        log.Info($"Transcript: {log.Path}");
        return completed ? 0 : 1;
    }

    /// The launch context is half the experiment: whether Steam is our parent, which
    /// Steam environment variables reached us, and whether Steam injected its overlay
    /// into the wrapper itself rather than into the game.
    private static void WriteContext(Options options, SpikeLog log)
    {
        log.Section("Wrapper context");
        log.Info($"transcript      : {log.Path}");
        log.Info($"command line    : {Environment.CommandLine}");
        log.Info($"wrapper pid     : {Environment.ProcessId}");
        log.Info($"working dir     : {Environment.CurrentDirectory}");
        log.Info($"os              : {Environment.OSVersion.VersionString} ({(Environment.Is64BitProcess ? "64-bit process" : "32-bit process")})");
        log.Info($"started at      : {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}");

        var snapshot = ProcessProbe.Snapshot();
        var byPid = snapshot.ToDictionary(entry => entry.Pid);
        var chain = ProcessProbe.AncestorChain(snapshot, Environment.ProcessId);
        log.Info("ancestors       : " + string.Join(" <- ", chain.Select(entry => $"{entry.Pid} {entry.Name}")));

        var self = chain.FirstOrDefault();
        if (self is not null)
        {
            var parentName = byPid.TryGetValue(self.ParentPid, out var parent) ? parent.Name : "<gone>";
            var report = ProcessProbe.Describe(self, parentName, probeRights: false);
            ProcessProbe.WriteReport(log, "wrapper process", report);
        }

        var steam = new List<string>();
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            var name = variable.Key as string ?? string.Empty;
            if (name.StartsWith("Steam", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("SDL_", StringComparison.OrdinalIgnoreCase)
                || name.Equals("ENABLE_VK_LAYER_VALVE_steam_overlay_1", StringComparison.OrdinalIgnoreCase))
            {
                steam.Add($"{name}={variable.Value}");
            }
        }

        steam.Sort(StringComparer.OrdinalIgnoreCase);
        log.Info(steam.Count == 0
            ? "steam env       : none (the wrapper was not started by Steam)"
            : "steam env       :");
        foreach (var variable in steam)
        {
            log.Line($"            {variable}");
        }

        log.Info($"package family  : {options.PackageFamily ?? "<n/a>"}");
    }
}
