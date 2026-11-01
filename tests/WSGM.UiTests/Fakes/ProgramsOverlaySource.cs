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

    public string LastResult => "Windows or the foreground game prevented application activation.";

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
        return Refused();
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
