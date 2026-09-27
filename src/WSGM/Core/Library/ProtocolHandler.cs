using System;
using System.IO;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace WSGM.Core;

/// <summary>A program and the arguments that open one URI.</summary>
/// <param name="Program">The registered program, unquoted and fully qualified.</param>
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
///         The handler the user chose in Windows' default apps wins, as it does for the shell: its
///         <c>UserChoice</c> names a program id whose command is used. Without one, the per-user
///         registration of the scheme wins over the machine one.
///     </para>
///     <para>
///         A URI comes from a launcher's files or a shortcut someone else wrote, and it is substituted
///         into a command line. A scheme that is not a scheme, or a URI with a quote in it that would
///         end its argument and start another, is refused rather than escaped.
///     </para>
/// </remarks>
public static class ProtocolHandler
{
    private const string UrlAssociations = @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations";

    /// <summary>Resolves the command that opens a URI.</summary>
    /// <param name="uri">The URI, with its scheme.</param>
    /// <returns>The command, or null when the URI is unusable or the scheme has no usable registration.</returns>
    public static ProtocolCommand? Resolve(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var separator = uri.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || !ValidScheme(uri[..separator]) || !SafeUri(uri))
        {
            return null;
        }

        var template = ReadCommand(uri[..separator]);
        return template is null ? null : Compose(template, uri, File.Exists);
    }

    /// <summary>Whether a string is a URI scheme: a letter, then letters, digits, <c>+</c>, <c>-</c> or <c>.</c>.</summary>
    /// <param name="scheme">The text before the URI's first colon.</param>
    /// <returns>True for a well-formed scheme, which is also a safe registry key name.</returns>
    internal static bool ValidScheme(string scheme)
    {
        if (scheme.Length is 0 or > 64 || !char.IsAsciiLetter(scheme[0]))
        {
            return false;
        }

        foreach (var character in scheme)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('+' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Substitutes a URI into a registered command line.</summary>
    /// <param name="template">The registered command, such as <c>"C:\x\app.exe" "%1"</c>.</param>
    /// <param name="uri">The URI to open.</param>
    /// <param name="fileExists">Whether a file exists, for placing a program registered by its bare name.</param>
    /// <returns>The program and its arguments, or null when the command names no usable program.</returns>
    /// <remarks>
    ///     <para>
    ///         Internal so the parsing is testable without a registry. <c>%1</c> and <c>%L</c> are the URI,
    ///         and so is <c>%*</c>, every argument, since the URI is the only one. <c>%2</c> to <c>%9</c>
    ///         name arguments the shell never passes here and become nothing. A template with no
    ///         placeholder gets the URI appended, quoted, which is what the shell does for such a
    ///         registration.
    ///     </para>
    ///     <para>
    ///         A program registered by its bare name, such as <c>rundll32.exe</c>, is the one in the
    ///         system folder, where the shell would find it. Any other relative program is refused: a
    ///         shortcut's Target has to name a file.
    ///     </para>
    /// </remarks>
    internal static ProtocolCommand? Compose(string template, string uri, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        if (!SafeUri(uri))
        {
            return null;
        }

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

        program = Environment.ExpandEnvironmentVariables(program);
        if (program.Length == 0 || !program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(Path.GetFileName(program), "explorer.exe",
                                    StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!Path.IsPathFullyQualified(program))
        {
            if (program.IndexOfAny(['\\', '/']) >= 0)
            {
                return null;
            }

            program = Path.Combine(Environment.SystemDirectory, program);
            if (!fileExists(program))
            {
                return null;
            }
        }

        return new ProtocolCommand(program, Substitute(rest, uri));
    }

    /// <summary>Whether a URI can be put into a command line as one argument.</summary>
    private static bool SafeUri(string uri)
    {
        foreach (var character in uri)
        {
            if (character == '"' || char.IsControl(character))
            {
                return false;
            }
        }

        return uri.Length > 0;
    }

    /// <summary>Replaces the shell's placeholders in a registered argument list.</summary>
    private static string Substitute(string arguments, string uri)
    {
        StringBuilder result = new(arguments.Length + uri.Length);
        var placed = false;
        for (var i = 0; i < arguments.Length; i++)
        {
            if (arguments[i] != '%' || i + 1 == arguments.Length)
            {
                result.Append(arguments[i]);
                continue;
            }

            var next = arguments[i + 1];
            if (next is '1' or 'L' or 'l' or '*')
            {
                result.Append(uri);
                placed = true;
                i++;
            }
            else if (next is >= '2' and <= '9')
            {
                i++;
            }
            else
            {
                result.Append(arguments[i]);
            }
        }

        var substituted = result.ToString().Trim();
        return placed ? substituted : (substituted + " \"" + uri + "\"").Trim();
    }

    private static string? ReadCommand(string scheme)
    {
        if (ReadUserChoice(scheme) is { } progId && ReadOpenCommand(progId) is { } chosen)
        {
            return chosen;
        }

        return ReadOpenCommand(scheme);
    }

    /// <summary>The program id the user chose for a scheme in Windows' default apps, or null.</summary>
    private static string? ReadUserChoice(string scheme)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{UrlAssociations}\{scheme}\UserChoice");
            return key?.GetValue("ProgId") is string { Length: > 0 } progId && ValidProgId(progId) ? progId : null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>The open command registered for a scheme or program id, per user first.</summary>
    private static string? ReadOpenCommand(string name)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey($@"Software\Classes\{name}\shell\open\command");
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

    /// <summary>Whether a program id read from the registry is a single key name.</summary>
    private static bool ValidProgId(string progId)
    {
        return progId.Length <= 255 && progId.IndexOfAny(['\\', '/', '"']) < 0;
    }
}
