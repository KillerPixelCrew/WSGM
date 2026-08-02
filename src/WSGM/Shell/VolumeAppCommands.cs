namespace WSGM.Shell;

/// <summary>Decodes the volume-related APPCOMMAND values from a shell-hook
/// notification. Keeping this parsing isolated gives the device-only message
/// path a small, executable specification.</summary>
internal static class VolumeAppCommands
{
    internal const int Mute = 8;
    internal const int Down = 9;
    internal const int Up = 10;

    private const int AppCommandMask = 0x0FFF;

    /// <summary>Gets the supported volume command carried by a shell-hook lParam,
    /// or zero when the command belongs to another subsystem.</summary>
    internal static int FromShellHookLParam(nint lParam)
    {
        // GET_APPCOMMAND_LPARAM(lParam): HIWORD(lParam) without the device bits.
        var raw = unchecked((int)(long)lParam);
        var command = ((raw >> 16) & 0xFFFF) & AppCommandMask;
        if (IsSupported(command))
        {
            return command;
        }

        // Some OEM shell implementations relay the already-extracted command
        // rather than the original WM_APPCOMMAND lParam. Accept that shape too.
        return IsSupported(raw) ? raw : 0;
    }

    /// <summary>Gets a concise diagnostic name for a supported command.</summary>
    internal static string Describe(int command) => command switch
    {
        Mute => "mute",
        Down => "down",
        Up => "up",
        _ => "unknown",
    };

    private static bool IsSupported(int command)
        => command is Mute or Down or Up;
}
