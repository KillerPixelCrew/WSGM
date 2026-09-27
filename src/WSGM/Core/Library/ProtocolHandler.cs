using System;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace WSGM.Core;

/// <summary>A program and the arguments that open one URI.</summary>
/// <param name="Program">The registered program, unquoted.</param>
/// <param name="Arguments">Its arguments with the URI substituted.</param>
public sealed record ProtocolCommand(string Program, string Arguments);

/// <summary>Finds the program a URI scheme is registered to, so a launcher URI can be a shortcut.</summary>
/// <remarks>
///     <para>
///         Launchers start their games from URIs such as <c>com.epicgames.launcher://…</c>. Steam can
///         only run a program, and handing the URI to <c>explorer.exe</c> is no answer here: Explorer
///         is not running in Game Mode and starting it would bring the desktop shell back. A wrapper
///         such as PowerShell's <c>Start-Process</c> exits at once, so Steam would think the game had
///         already stopped. The registered command is what the shell itself would run, so it is run
///         directly, with the URI in the place the registration names.
///     </para>
///     <para>
///         The per-user registration wins over the machine one, as it does for the shell.
///     </para>
/// </remarks>
public static class ProtocolHandler
{
    /// <summary>Resolves the command that opens a URI.</summary>
    /// <param name="uri">The URI, with its scheme.</param>
    /// <returns>The command, or null when the scheme has no usable registration.</returns>
    public static ProtocolCommand? Resolve(string uri)
    {
        var separator = uri.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return null;
        }

        var template = ReadCommand(uri[..separator]);
        return template is null ? null : Compose(template, uri);
    }

    /// <summary>Substitutes a URI into a registered command line.</summary>
    /// <param name="template">The registered command, such as <c>"C:\x\app.exe" "%1"</c>.</param>
    /// <param name="uri">The URI to open.</param>
    /// <returns>The program and its arguments, or null when the command names no program.</returns>
    /// <remarks>
    ///     Internal so the parsing is testable without a registry. A template with no placeholder gets
    ///     the URI appended, quoted, which is what the shell does for such a registration.
    /// </remarks>
    internal static ProtocolCommand? Compose(string template, string uri)
    {
        template = template.Trim();
        string program;
        string rest;
        if (template.StartsWith('"'))
        {
            var close = template.IndexOf('"', 1);
            if (close < 0)
            {
                return null;
            }

            program = template[1..close];
            rest = template[(close + 1)..].Trim();
        }
        else
        {
            // An unquoted program ends at its extension: the path itself may contain spaces.
            var end = template.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (end < 0)
            {
                return null;
            }

            program = template[..(end + 4)];
            rest = template[(end + 4)..].Trim();
        }

        if (program.Length == 0 || !program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(Path.GetFileName(program), "explorer.exe",
                                    StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var arguments = rest.Contains("%1", StringComparison.Ordinal)
            ? rest.Replace("%1", uri, StringComparison.Ordinal)
            : rest.Contains("%L", StringComparison.OrdinalIgnoreCase)
                ? rest.Replace("%L", uri, StringComparison.OrdinalIgnoreCase)
                : (rest + " \"" + uri + "\"").Trim();
        return new ProtocolCommand(Environment.ExpandEnvironmentVariables(program), arguments);
    }

    private static string? ReadCommand(string scheme)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey($@"Software\Classes\{scheme}\shell\open\command");
                if (key?.GetValue(null) is string { Length: > 0 } command)
                {
                    return command;
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException
                                           or IOException)
            {
                // An unreadable registration is no registration.
            }
        }

        return null;
    }
}
