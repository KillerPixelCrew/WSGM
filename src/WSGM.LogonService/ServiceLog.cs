using System;
using System.IO;
using System.Text;

namespace WSGM.LogonService;

/// <summary>File logger at %ProgramData%\WSGM\wsgm-service.log. SYSTEM must not
/// write into user directories, so the service cannot share wsgm.log — WSGM's own
/// log stays the primary diagnostic surface (it records the --boot start); this
/// file is only needed when the splash never appeared at all. Best effort: logging
/// failures never take the service down.</summary>
internal static class ServiceLog
{
    private static readonly RotatingFileLog Log = new(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WSGM",
            "wsgm-service.log"),
        rotateAtBytes: 1024 * 1024,
        archiveSuffixes: [".old"],
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        writeRetries: 0);

    internal static void Info(string message) => Write("INFO", message);

    internal static void Warn(string message) => Write("WARN", message);

    internal static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message) =>
        Log.Append($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
}
