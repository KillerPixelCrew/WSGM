using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     External operations behind the format run and its <see cref="SteamLibraryProvisioner" />;
///     orchestration and library bookkeeping stay in those owners.
/// </summary>
internal sealed class SdFormatOperations
{
    internal required Func<bool?> IsElevated { get; init; }
    internal required Func<int, DiskIdentitySnapshot> ReadDiskIdentity { get; init; }
    internal required Func<string, CancellationToken, Task<ConsoleToolResult>> RunDiskpart { get; init; }
    internal required Func<int, CancellationToken, Task<int>> WaitForVolume { get; init; }
    internal required Func<int, int, CancellationToken, Task<char?>> WaitForLetter { get; init; }
    internal required Func<char, (string? Root, int Disk)> ReadVolume { get; init; }
    internal required Func<int, List<char>> LettersOnDisk { get; init; }
    internal required Func<char, string> LetterRoot { get; init; }
    internal required Func<(bool Running, string? ExePath)> ReadSteam { get; init; }
    internal required Func<(string? Path, string? Text)> ReadLibraryFolders { get; init; }
    internal required Func<string, (string? ContentId, string Label)> ReadMarker { get; init; }

    internal required Func<string, string, CancellationToken, Task<SteamLibraryRemoveResult>> RemoveLibrary
    {
        get;
        init;
    }

    internal required Func<string, string, bool, SteamLibraryAddResult> AddLibrary { get; init; }
    internal required Func<char, CancellationToken, Task<bool>> Retrim { get; init; }
    internal required Action<char> BroadcastVolumeArrival { get; init; }
    internal required Func<int, CancellationToken, Task> Delay { get; init; }
    internal required Action<LogLevel, string, Exception?> WriteLog { get; init; }
}
