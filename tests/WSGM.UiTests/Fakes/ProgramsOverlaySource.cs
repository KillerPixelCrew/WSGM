using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.UiTests.Fakes;

/// <summary>Inert Programs presentation for captures; never inspects processes, files or Windows.</summary>
internal sealed class ProgramsOverlaySource : IProgramsSource
{
    private readonly ProgramState[] _state =
    [
        new(new ProgramDefinition
        {
            Id = "monitor", Name = "Performance monitor", Executable = @"C:\Tools\Monitor\Monitor.exe",
            ExecutableHash = "approved", Arguments = "--compact"
        }, true, true, "Running", null),
        new(new ProgramDefinition
        {
            Id = "mod-manager", Name = "Mod manager", Executable = @"D:\Tools\Mods\Launcher.exe",
            ExecutableHash = "approved", UiExecutable = @"D:\Tools\Mods\Manager.exe",
            UiExecutableHash = "approved", WorkingDirectory = @"D:\Tools\Mods"
        }, false, true, "Not running", null),
        new(new ProgramDefinition
        {
            Id = "missing", Name = "Window utility", Executable = @"D:\Tools\WindowUtility.exe",
            ExecutableHash = "old"
        }, false, false, "Unavailable · executable missing or changed", null),
        new(new ProgramDefinition
        {
            Id = "background", Name = "Background helper", Executable = @"C:\Tools\Helper.exe",
            ExecutableHash = "approved"
        }, true, true, "Running · no window", null)
    ];

    /// <summary>What the Add program list reads; a pending task shows the loading state.</summary>
    internal Task<InstalledProgramList> Installed { get; set; } = Task.FromResult(new InstalledProgramList(
    [
        new InstalledProgram("app:calc", "Calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", null, ""),
        new InstalledProgram("app:long",
            "A desktop application with a very long name that has to wrap inside the row without clipping",
            null, @"C:\Tools\long.exe", ""),
        new InstalledProgram("app:notepad", "Notepad++", null, @"C:\Program Files\Notepad++\notepad++.exe", ""),
        new InstalledProgram("app:monitor", "Performance monitor", null, @"C:\Tools\Monitor\Monitor.exe", "--compact"),
        new InstalledProgram("app:xbox", "Xbox", "Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App", null, "")
    ]));

    /// <summary>Every icon handed to the view, which owns and disposes them.</summary>
    internal List<WriteableBitmap> Icons { get; } = [];

    /// <summary>The definitions saved with explicit approval.</summary>
    internal List<ProgramDefinition> Added { get; } = [];

    public string LastResult => "Windows or the foreground game prevented application activation.";

    public Task<InstalledProgramList> ReadInstalledAsync(CancellationToken cancellationToken)
    {
        return Installed;
    }

    public Task<IReadOnlyDictionary<string, Bitmap?>> ReadInstalledIconsAsync(IReadOnlyList<string> keys,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyDictionary<string, Bitmap?>>(keys.ToDictionary(key => key, key =>
        {
            var icon = new WriteableBitmap(new PixelSize(32, 32), new Vector(96, 96), PixelFormat.Bgra8888,
                AlphaFormat.Premul);
            using (var buffer = icon.Lock())
            {
                var pixels = new int[32 * 32];
                // Captures need the same colour every run; a string's hash code differs per process.
                Array.Fill(pixels, unchecked((int)(0xFF204060 | (uint)(key.Sum(c => c) * 2777 & 0x00FFFFFF))));
                System.Runtime.InteropServices.Marshal.Copy(pixels, 0, buffer.Address, pixels.Length);
            }

            Icons.Add(icon);
            return (Bitmap?)icon;
        }));
    }

    public ProgramState[] ReadState()
    {
        return _state;
    }

    public bool IsPinned(string id)
    {
        return id is "monitor" or "missing";
    }

    public void ReportResult(string message)
    {
    }

    public event Action? Changed
    {
        add { }
        remove { }
    }

    public Task SaveAsync(ProgramDefinition definition, bool approveExecutable)
    {
        if (!approveExecutable)
        {
            return Refused();
        }

        Added.Add(definition);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string id)
    {
        return Refused();
    }

    public Task RememberWindowAsync(string id, ProgramUiCandidate candidate)
    {
        return Refused();
    }

    public Task ForgetUiAsync(string id)
    {
        return Refused();
    }

    public Task ClearPendingAsync(string id)
    {
        return Refused();
    }

    public Task<string> ActivateAsync(string id, CancellationToken cancellationToken)
    {
        return Task.FromResult("Preview only.");
    }

    private static Task Refused()
    {
        return Task.FromException(new InvalidOperationException("Preview only."));
    }
}
