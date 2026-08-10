using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Result of a live library add through Steam's own client.</summary>
public enum SteamLibraryAddStatus
{
    /// <summary>Steam adopted the folder as a new library.</summary>
    Added,
    /// <summary>The drive already carries a Steam library (nothing to do).</summary>
    AlreadyPresent,
    /// <summary>Steam actively refused the folder; <c>Detail</c> is its reason.</summary>
    Rejected,
    /// <summary>The debug channel could not be reached, so no live add happened.</summary>
    Unavailable,
}

/// <summary>Outcome of a live library add.</summary>
/// <param name="Status">What Steam did.</param>
/// <param name="Detail">Steam's reason code, when it gave one.</param>
public readonly record struct SteamLibraryAddResult(SteamLibraryAddStatus Status, string? Detail);

/// <summary>Result of removing a live Steam library selected by its content id.</summary>
public enum SteamLibraryRemoveStatus
{
    /// <summary>Steam removed the library and will persist the change.</summary>
    Removed,
    /// <summary>The content id is not registered or its folder is already absent.</summary>
    NotPresent,
    /// <summary>Steam actively refused the removal; <c>Detail</c> is its reason.</summary>
    Rejected,
    /// <summary>The debug channel could not be reached, so no live removal happened.</summary>
    Unavailable,
}

/// <summary>Outcome of removing a live library.</summary>
/// <param name="Status">What Steam did.</param>
/// <param name="Detail">Steam's reason code, when it gave one.</param>
public readonly record struct SteamLibraryRemoveResult(
    SteamLibraryRemoveStatus Status, string? Detail);

/// <summary>Adds a Steam library to the RUNNING client by driving Steam's own
/// front-end API over its CEF remote-debugging port (see <see cref="SteamCef"/>):
/// a <c>Runtime.evaluate</c> calls <c>SteamClient.InstallFolder.AddInstallFolder</c>,
/// so Steam adopts, persists, mounts and scans the folder on its own thread with no
/// restart. This is version-proof (no binary offsets) and safe (Steam performs the
/// operation), unlike poking the client's internals in-process.</summary>
public static class SteamCdp
{
    /// <summary>Writes the CEF remote-debugging flag so Steam opens its localhost
    /// devtools port on next start. Idempotent and best-effort.</summary>
    public static bool EnsureRemoteDebuggingEnabled() => SteamCef.EnsureRemoteDebuggingEnabled();

    /// <summary>Blocking wrapper for worker-thread callers (never call on the UI thread).</summary>
    /// <param name="libraryPath">The library folder, e.g. <c>E:\SteamLibrary</c>.</param>
    /// <param name="label">A label to apply after adding, or null/empty for none.</param>
    public static SteamLibraryAddResult AddLibrary(string libraryPath, string? label = null)
        => AddLibraryAsync(libraryPath, label).GetAwaiter().GetResult();

    /// <summary>Adds <paramref name="libraryPath"/> to the live Steam client and,
    /// on success, labels it.</summary>
    /// <param name="libraryPath">The library folder, e.g. <c>E:\SteamLibrary</c>.</param>
    /// <param name="label">A label to apply after adding, or null/empty for none.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    public static async Task<SteamLibraryAddResult> AddLibraryAsync(
        string libraryPath, string? label = null, CancellationToken cancellationToken = default)
    {
        var result = await SteamCef.EvaluateAsync(
            BuildAddExpression(libraryPath, label), TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);
        if (!result.Reachable)
        {
            return new SteamLibraryAddResult(SteamLibraryAddStatus.Unavailable, result.Error);
        }
        return Interpret(result.Value);
    }

    /// <summary>Removes the live Steam library whose registration carries
    /// <paramref name="contentId"/>. Steam's folder API does not expose content
    /// ids, so the id first selects its registered path; only that path's current
    /// live folder index is passed to Steam. This makes a reused card-reader drive
    /// letter unable to select a different card's library.</summary>
    /// <param name="contentId">The stable identity read from the card marker.</param>
    /// <param name="libraryFoldersVdf">Steam's current libraryfolders configuration.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The live removal outcome.</returns>
    public static async Task<SteamLibraryRemoveResult> RemoveLibraryByContentIdAsync(
        string contentId, string libraryFoldersVdf, CancellationToken cancellationToken = default)
    {
        var libraryPath = Shell.SteamLibraryVdf.PathForContentId(libraryFoldersVdf, contentId);
        if (libraryPath is null)
        {
            return new SteamLibraryRemoveResult(SteamLibraryRemoveStatus.NotPresent, null);
        }
        var matchingPaths = Shell.SteamLibraryVdf.ValuesOf(libraryFoldersVdf, "path")
            .Count(path => string.Equals(path.Replace("\\\\", "\\").TrimEnd('\\', '/'),
                libraryPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
        if (matchingPaths != 1)
        {
            return new SteamLibraryRemoveResult(SteamLibraryRemoveStatus.Rejected,
                "ContentIdPathAmbiguous");
        }
        var result = await SteamCef.EvaluateAsync(
            BuildRemoveExpression(libraryPath), TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);
        if (!result.Reachable)
        {
            return new SteamLibraryRemoveResult(SteamLibraryRemoveStatus.Unavailable, result.Error);
        }
        return InterpretRemove(result.Value);
    }

    /// <summary>Builds the JS that adds the folder, labels it when a label is
    /// given, and reports the outcome as a JSON string. Both the path and label are
    /// JSON-encoded into JS string literals — a raw path would lose its backslashes
    /// and Steam would reject the malformed path.</summary>
    private static string BuildAddExpression(string libraryPath, string? label)
    {
        var pathLiteral = SteamCef.JsString(libraryPath);
        var labelLiteral = string.IsNullOrEmpty(label) ? "null" : SteamCef.JsString(label);
        return
            "(async()=>{try{const i=await SteamClient.InstallFolder.AddInstallFolder(" +
            pathLiteral + ");const l=" + labelLiteral + ";" +
            "if(l!==null&&typeof i==='number'&&i>=0){" +
            "try{await SteamClient.InstallFolder.SetFolderLabel(i,l);}catch(e){}}" +
            "return JSON.stringify({ok:true,index:i});}" +
            "catch(e){return JSON.stringify({ok:false,result:(e&&e.result),message:(e&&e.message)});}})()";
    }

    private static string BuildRemoveExpression(string libraryPath)
    {
        var pathLiteral = SteamCef.JsString(libraryPath);
        return "(async()=>{try{const path=" + pathLiteral + ";const norm=p=>p.replace(/[\\\\/]+$/,'').toLowerCase();"
            + "const folders=await SteamClient.InstallFolder.GetInstallFolders();"
            + "const folder=folders.find(x=>norm(x.strFolderPath)===norm(path));"
            + "if(!folder)return JSON.stringify({ok:true,absent:true});"
            + "await SteamClient.InstallFolder.RemoveInstallFolder(folder.nFolderIndex);"
            + "return JSON.stringify({ok:true});}catch(e){return JSON.stringify({ok:false,result:(e&&e.result),message:(e&&e.message)});}})()";
    }

    /// <summary>Maps Steam's JSON reply to a result. Success carries no reason;
    /// <c>DriveAlreadyHasLibrary</c> is treated as already-present; anything else is
    /// a genuine rejection with Steam's own reason code.</summary>
    private static SteamLibraryAddResult Interpret(string? jsonValue)
    {
        if (jsonValue is null)
        {
            return new SteamLibraryAddResult(
                SteamLibraryAddStatus.Unavailable, "No response from Steam.");
        }
        try
        {
            using var document = JsonDocument.Parse(jsonValue);
            var root = document.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                Log.Info("Steam library added to the live client.");
                return new SteamLibraryAddResult(SteamLibraryAddStatus.Added, null);
            }

            var message = root.TryGetProperty("message", out var reason)
                && reason.ValueKind == JsonValueKind.String
                ? reason.GetString()
                : null;
            message ??= root.TryGetProperty("result", out var resultCode)
                ? $"EResult {resultCode.GetRawText()}" : null;

            if (string.Equals(message, "DriveAlreadyHasLibrary", StringComparison.Ordinal))
            {
                return new SteamLibraryAddResult(SteamLibraryAddStatus.AlreadyPresent, message);
            }
            Log.Warn($"Steam rejected the library add: {message ?? "unknown reason"}.");
            return new SteamLibraryAddResult(SteamLibraryAddStatus.Rejected, message);
        }
        catch (Exception ex)
        {
            return new SteamLibraryAddResult(SteamLibraryAddStatus.Unavailable, ex.Message);
        }
    }

    private static SteamLibraryRemoveResult InterpretRemove(string? jsonValue)
    {
        if (jsonValue is null)
        {
            return new SteamLibraryRemoveResult(
                SteamLibraryRemoveStatus.Unavailable, "No response from Steam.");
        }
        try
        {
            using var document = JsonDocument.Parse(jsonValue);
            var root = document.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                var status = root.TryGetProperty("absent", out var absent)
                    && absent.ValueKind == JsonValueKind.True
                    ? SteamLibraryRemoveStatus.NotPresent : SteamLibraryRemoveStatus.Removed;
                return new SteamLibraryRemoveResult(status, null);
            }
            var message = root.TryGetProperty("message", out var reason)
                && reason.ValueKind == JsonValueKind.String
                ? reason.GetString() : null;
            message ??= root.TryGetProperty("result", out var resultCode)
                ? $"EResult {resultCode.GetRawText()}" : null;
            Log.Warn($"Steam rejected the library removal: {message ?? "unknown reason"}.");
            return new SteamLibraryRemoveResult(SteamLibraryRemoveStatus.Rejected, message);
        }
        catch (Exception ex)
        {
            return new SteamLibraryRemoveResult(SteamLibraryRemoveStatus.Unavailable, ex.Message);
        }
    }
}
