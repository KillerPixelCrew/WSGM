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

    public event Action? Changed
    {
        add { }
        remove { }
    }

    public string LastResult => "Windows or the foreground game prevented application activation.";
    public ProgramState[] ReadState() => _state;
    public bool IsPinned(string id) => id is "monitor" or "missing";
    public Task SaveAsync(ProgramDefinition definition, bool approveExecutable) => Refused();
    public Task RemoveAsync(string id) => Refused();
    public Task RememberWindowAsync(string id, ProgramUiCandidate candidate) => Refused();
    public Task ForgetUiAsync(string id) => Refused();
    public Task ClearPendingAsync(string id) => Refused();
    public Task<string> ActivateAsync(string id, CancellationToken cancellationToken) =>
        Task.FromResult("Preview only.");
    public void ReportResult(string message) { }

    private static Task Refused() => Task.FromException(new InvalidOperationException("Preview only."));
}
