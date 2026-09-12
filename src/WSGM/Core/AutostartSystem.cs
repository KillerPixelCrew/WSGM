using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Win32;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>The real Windows startup surfaces behind <see cref="IAutostartSystem"/>.
///
/// Scheduled tasks are read as XML through <c>schtasks</c> rather than the Task Scheduler COM
/// object, because the XML is language-neutral: the localized table output of a German or French
/// Windows would have to be parsed by column heading.</summary>
public sealed class AutostartSystem : IAutostartSystem
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    private const int TaskQueryTimeoutMs = 30_000;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> ReadRunValues(SteamAutostartScope scope, bool wow64)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(Hive(scope),
            wow64 ? RegistryView.Registry32 : RegistryView.Registry64);
        using RegistryKey? key = baseKey.OpenSubKey(RunKey);
        if (key is null) { return values; }
        foreach (string name in key.GetValueNames())
        {
            if (key.GetValue(name) is string command) { values[name] = command; }
        }
        return values;
    }

    /// <inheritdoc />
    public byte[]? ReadApproval(SteamAutostartScope scope, string list, string name)
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(Hive(scope), RegistryView.Registry64);
        using RegistryKey? key = baseKey.OpenSubKey($@"{ApprovedKey}\{list}");
        return key?.GetValue(name) as byte[];
    }

    /// <inheritdoc />
    public void WriteApproval(SteamAutostartScope scope, string list, string name, byte[]? value)
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(Hive(scope), RegistryView.Registry64);
        if (value is null)
        {
            using RegistryKey? existing = baseKey.OpenSubKey($@"{ApprovedKey}\{list}", writable: true);
            existing?.DeleteValue(name, throwOnMissingValue: false);
            return;
        }
        using RegistryKey key = baseKey.CreateSubKey($@"{ApprovedKey}\{list}")
            ?? throw new InvalidOperationException($"Cannot open the {list} startup approval key.");
        key.SetValue(name, value, RegistryValueKind.Binary);
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> ReadStartupShortcuts(SteamAutostartScope scope)
    {
        Dictionary<string, string> shortcuts = new(StringComparer.OrdinalIgnoreCase);
        string folder = Environment.GetFolderPath(scope is SteamAutostartScope.User
            ? Environment.SpecialFolder.Startup
            : Environment.SpecialFolder.CommonStartup);
        if (folder.Length == 0 || !Directory.Exists(folder)) { return shortcuts; }
        foreach (string file in Directory.EnumerateFiles(folder, "*.lnk"))
        {
            if (ShellLink.ReadTarget(file) is { } target) { shortcuts[Path.GetFileName(file)] = target; }
        }
        return shortcuts;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> ReadLogonTasks()
    {
        Dictionary<string, string> tasks = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, XElement definition) in QueryTasks())
        {
            XNamespace ns = definition.Name.Namespace;
            if (definition.Element(ns + "Triggers")?.Element(ns + "LogonTrigger") is null) { continue; }
            string? command = definition.Element(ns + "Actions")?.Element(ns + "Exec")?.Element(ns + "Command")?.Value;
            if (!string.IsNullOrWhiteSpace(command)) { tasks[path] = command.Trim(); }
        }
        return tasks;
    }

    /// <inheritdoc />
    public bool IsTaskEnabled(string taskPath) =>
        QueryTasks(taskPath).Select(entry =>
        {
            XNamespace ns = entry.Definition.Name.Namespace;
            string? enabled = entry.Definition.Element(ns + "Settings")?.Element(ns + "Enabled")?.Value;
            return !string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase);
        }).FirstOrDefault(true);

    /// <inheritdoc />
    public bool SetTaskEnabled(string taskPath, bool enabled) => ConsoleTool.Run(
        ConsoleTool.System32("schtasks.exe"),
        $"/Change /TN \"{taskPath}\" {(enabled ? "/ENABLE" : "/DISABLE")}");

    private static RegistryHive Hive(SteamAutostartScope scope) =>
        scope is SteamAutostartScope.User ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;

    /// <summary>Reads task definitions as XML. <c>schtasks /Query /XML</c> concatenates every
    /// definition into one stream that is not a single valid document, so each is parsed alone.</summary>
    private static IEnumerable<(string Path, XElement Definition)> QueryTasks(string? taskPath = null)
    {
        string arguments = taskPath is null ? "/Query /XML ONE" : $"/Query /TN \"{taskPath}\" /XML ONE";
        (int exitCode, string output) = ConsoleTool.RunCapturedAsync(
            ConsoleTool.System32("schtasks.exe"), arguments, TaskQueryTimeoutMs).GetAwaiter().GetResult();
        if (exitCode != 0 || output.Length == 0)
        {
            if (taskPath is not null) { Log.Warn($"Steam autostart: querying task \"{taskPath}\" failed."); }
            yield break;
        }
        foreach ((string path, XElement definition) in SplitTaskDefinitions(output, taskPath))
        {
            yield return (path, definition);
        }
    }

    /// <summary>Splits the concatenated definitions and names each one. The task's own
    /// <c>RegistrationInfo/URI</c> is the path; the comment schtasks writes before each definition
    /// is the fallback for a task registered without one.</summary>
    /// <param name="output">Raw <c>schtasks /Query /XML</c> output.</param>
    /// <param name="requestedPath">The single task that was asked for, when one was.</param>
    /// <returns>Task path and parsed definition pairs.</returns>
    internal static IEnumerable<(string Path, XElement Definition)> SplitTaskDefinitions(
        string output, string? requestedPath = null)
    {
        int index = 0;
        string? pendingName = requestedPath;
        while (true)
        {
            int comment = output.IndexOf("<!--", index, StringComparison.Ordinal);
            int start = output.IndexOf("<Task", index, StringComparison.Ordinal);
            if (start < 0) { yield break; }
            if (comment >= 0 && comment < start)
            {
                int commentEnd = output.IndexOf("-->", comment, StringComparison.Ordinal);
                if (commentEnd > comment) { pendingName = output[(comment + 4)..commentEnd].Trim(); }
            }
            int end = output.IndexOf("</Task>", start, StringComparison.Ordinal);
            if (end < 0) { yield break; }
            end += "</Task>".Length;
            XElement? definition = null;
            try { definition = XElement.Parse(output[start..end]); }
            catch (System.Xml.XmlException) { }
            index = end;
            if (definition is null) { continue; }
            XNamespace ns = definition.Name.Namespace;
            string? uri = definition.Element(ns + "RegistrationInfo")?.Element(ns + "URI")?.Value;
            string path = !string.IsNullOrWhiteSpace(uri) ? uri.Trim() : pendingName ?? "";
            if (path.Length != 0) { yield return (path, definition); }
        }
    }
}
