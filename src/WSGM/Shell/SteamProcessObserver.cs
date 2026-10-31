using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>One look at Steam's processes and its Big Picture window.</summary>
/// <param name="Running">Whether steam.exe or a steamwebhelper runs in this session.</param>
/// <param name="BrowserProcessId">
///     steamwebhelper's browser process, the one that opens the debugging port; an unreadable helper
///     when no browser process could be told from its children; zero while there is no helper.
/// </param>
/// <param name="BrowserDebugging">
///     Whether that browser process was started with <c>--remote-debugging-port</c>; null while there is
///     no browser process or its command line could not be read.
/// </param>
/// <param name="BigPictureWindow">Whether Steam's Big Picture window exists.</param>
internal readonly record struct SteamProcessSnapshot(
    bool Running,
    uint BrowserProcessId,
    bool? BrowserDebugging,
    bool BigPictureWindow);

/// <summary>Reads Steam's process state once per call: two process-name lookups and one window search.</summary>
/// <remarks>
///     Only the transport gate's worker calls this, once a second, so the cache needs no lock. Looking
///     the two names up creates a process object only for Steam's own processes, never for every
///     process on the machine. A steamwebhelper's role
///     never changes, so each process's command line is read once, when it first appears. Steam writes
///     its CEF browser process's command line to <c>logs\webhelper.txt</c>, and that line carries
///     <c>--remote-debugging-port</c> exactly when Steam honored the debugging flag: a deterministic
///     answer where a timer on <c>steam.exe</c>'s age could not tell a slow start or a client update
///     from a missing flag (2026-10-13: the update ran until 08:40:50, WSGM gave up at 08:40:48, and the
///     browser started at 08:40:51 with the port).
/// </remarks>
internal sealed class SteamProcessObserver
{
    private const string ClientProcessName = "steam";
    private const string HelperProcessName = "steamwebhelper";

    private readonly Dictionary<uint, HelperRole> _helpers = [];

    /// <summary>Takes one snapshot of Steam's processes and window.</summary>
    /// <returns>The snapshot; a failed process query reads as Steam not running.</returns>
    internal SteamProcessSnapshot Observe()
    {
        HashSet<uint> steam;
        HashSet<uint> helpers;
        try
        {
            // Both lookups keep only this session's processes and skip one that exits mid-read.
            helpers = ProcessLookup.FindProcessIds(HelperProcessName);
            steam = ProcessLookup.FindProcessIds(ClientProcessName);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Change("steam-process-observer", $"Steam process snapshot failed: {ex.Message}", LogLevel.Warn);
            return default;
        }

        steam.UnionWith(helpers);

        // Forget exited helpers first, so a reused id is read again.
        foreach (var exited in _helpers.Keys.Where(id => !helpers.Contains(id)).ToArray())
        {
            _helpers.Remove(exited);
        }

        uint browser = 0;
        uint unreadable = 0;
        bool? debugging = null;
        foreach (var id in helpers)
        {
            if (!_helpers.TryGetValue(id, out var role))
            {
                role = Classify(NativeShellProcess.TryGetCommandLine(id));
                _helpers[id] = role;
            }

            if (role is HelperRole.DebuggingBrowser or HelperRole.Browser)
            {
                browser = id;
                debugging = role == HelperRole.DebuggingBrowser;
            }
            else if (role == HelperRole.Unknown && (unreadable == 0 || id < unreadable))
            {
                unreadable = id;
            }
        }

        if (browser == 0)
        {
            // An unreadable helper may be the browser process: report it with an unknown flag, so the
            // activation never restarts Steam on a guess and the window admission still has an identity.
            browser = unreadable;
        }

        return new SteamProcessSnapshot(steam.Count > 0, browser, debugging,
            steam.Count > 0 && Steam.FindBigPictureWindow(steam) != 0);
    }

    /// <summary>Tells steamwebhelper's browser process from its children by command line.</summary>
    /// <param name="commandLine">The process's command line, or null when it could not be read.</param>
    /// <returns>What the process is.</returns>
    /// <remarks>Chromium starts every child with <c>--type=</c>; the browser process has none.</remarks>
    internal static HelperRole Classify(string? commandLine)
    {
        if (commandLine is null)
        {
            return HelperRole.Unknown;
        }

        if (commandLine.Contains(" --type=", StringComparison.Ordinal))
        {
            return HelperRole.Child;
        }

        return commandLine.Contains("--remote-debugging-port", StringComparison.Ordinal)
            ? HelperRole.DebuggingBrowser
            : HelperRole.Browser;
    }

    /// <summary>What a steamwebhelper process is.</summary>
    internal enum HelperRole
    {
        /// <summary>Its command line could not be read.</summary>
        Unknown,

        /// <summary>A renderer, GPU or utility process.</summary>
        Child,

        /// <summary>The browser process, started without remote debugging.</summary>
        Browser,

        /// <summary>The browser process, started with remote debugging.</summary>
        DebuggingBrowser
    }
}
