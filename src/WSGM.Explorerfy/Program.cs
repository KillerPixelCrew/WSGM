using System.Diagnostics;

namespace WSGM.Explorerfy;

/// <summary>Steam launch-option wrapper for games that need Windows Explorer
/// running (some games and mod tools require the shell). Used as
/// <c>"…\WSGM.Explorerfy.exe" %command%</c>: it asks the running WSGM shell to
/// drop to desktop mode (Explorer up), launches the wrapped game, stays alive for
/// the game's whole lifetime, and releases on exit so WSGM returns to game mode.</summary>
internal static class Program
{
    private const string PipeName = "WSGM.Explorerfy";
    private static readonly TimeSpan AcquireTimeout = TimeSpan.FromSeconds(20);

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                ExplorerfyLog.Error(
                    "No target command was supplied. Expected: WSGM.Explorerfy.exe <program> [arguments].");
                return 64;
            }

            ExplorerfyLog.Info(
                $"Steam wrapper invoked (target={Path.GetFileName(args[0])}, argumentCount={args.Length - 1}).");

            // The pipe connection is held for the game's whole lifetime; disposing it
            // (clean exit or Steam killing us) is what returns WSGM to game mode.
            await using var lease = await ExplorerLease.AcquireAsync(PipeName, AcquireTimeout);

            using var process = Start(args);
            if (process is null)
            {
                ExplorerfyLog.Error("Process.Start returned no process.");
                return 1;
            }

            ExplorerfyLog.Info(
                $"Launched {Path.GetFileName(args[0])} (pid {process.Id}); preserving Steam wrapper lifetime.");
            // No timeout: Steam expects its launch-option wrapper to stay alive for
            // the entire game lifetime, and returns the target's exit code.
            await process.WaitForExitAsync();
            ExplorerfyLog.Info($"Target pid {process.Id} exited with {process.ExitCode}.");
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            ExplorerfyLog.Error($"Unhandled wrapper failure: {ex}");
            return 1;
        }
    }

    /// <summary>Launches the wrapped game exactly as Steam would: same command,
    /// arguments, working directory, and inherited environment (this wrapper is a
    /// direct Steam child, so its environment already carries Steam's dynamic
    /// SteamAppId/GameId variables — no rebuild needed, unlike the de-elevation
    /// helper whose Task Scheduler child gets a clean environment).</summary>
    internal static Process? Start(string[] args)
    {
        var workingDirectory = Directory.Exists(Environment.CurrentDirectory)
            ? Environment.CurrentDirectory
            : SafeTargetDirectory(args[0]);
        var target = args[0];
        if (!Path.IsPathFullyQualified(target))
        {
            var candidate = Path.Combine(workingDirectory, target);
            if (File.Exists(candidate))
            {
                target = candidate;
            }
        }

        var startInfo = new ProcessStartInfo(target)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        for (var i = 1; i < args.Length; i++)
        {
            startInfo.ArgumentList.Add(args[i]);
        }
        return Process.Start(startInfo);
    }

    private static string SafeTargetDirectory(string target)
    {
        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(target)) ?? Environment.CurrentDirectory;
        }
        catch
        {
            return Environment.CurrentDirectory;
        }
    }
}
