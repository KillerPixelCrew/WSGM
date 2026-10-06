using System.Text.Json.Nodes;
using WSGM.Install;
using WSGM.Setup;
using WSGM.Setup.Engine;
using WSGM.Testing;

namespace WSGM.Tests.Setup;

// Every engine path, payload, executing image and log belongs to this disposable tree.
internal sealed class SetupTestInstallation : IDisposable
{
    private readonly string _previousExecutable = SetupExecutable.Path;
    private readonly string _previousLog = SetupLog.Path;
    private readonly TemporaryDirectory _temporary = new();

    internal SetupTestInstallation(Func<string, bool>? delete = null)
    {
        Root = _temporary.GetPath("installation");
        Machine = _temporary.GetPath("machine");
        User = _temporary.GetPath("user");
        SetupLog.Path = _temporary.GetPath("setup.log");
        SetupExecutable.Path = _temporary.GetPath("WSGM.Setup.exe");
        File.WriteAllText(SetupExecutable.Path, "new-setup");
        Write(Root, "App/WSGM.exe", "old-app");
        Write(Root, "App/WSGM.LogonService.exe", "old-service");
        Write(Root, "Plugins/wsgm.test-2.0.0.wsgmpkg", "old-plugin");
        Write(Root, "Setup/Packages/old.wsgmpkg", "old-package");
        Write(Root, "Setup/WSGM.Setup.exe", "old-setup");
        Write(Machine, "bundle.json", "old-bundle");
        var payload = _temporary.GetPath("payload");
        Write(payload, "App/WSGM.exe", "new-app");
        Write(payload, "App/WSGM.LogonService.exe", "new-service");
        Write(payload, "Packages/wsgm.test-2.1.0.wsgmpkg", "new-plugin");
        BundleManifest bundle = new()
        {
            SchemaVersion = BundleManifest.CurrentSchema,
            WsgmVersion = "2.1.0",
            Plugins =
            [
                new BundledPlugin
                {
                    Id = "wsgm.test", Name = "Test plugin", Version = "2.1.0", Category = "wsgm.common",
                    Origin = "first-party", Validation = "blind", File = "wsgm.test-2.1.0.wsgmpkg",
                    Sha256 = new string('0', 64)
                }
            ]
        };
        File.WriteAllBytes(Path.Combine(payload, "bundle.json"), bundle.ToUtf8Json());
        Engine = new SetupEngine(SetupPayload.Open(payload), Runtime, Root, Machine, User,
            delete ?? DeleteNow, () => Runtime.Calls.Add("RemoveSetupRegistration"));
    }

    internal string Root { get; }
    internal string Machine { get; }
    internal string User { get; }
    internal RecordingSetupRuntime Runtime { get; } = new();
    internal SetupEngine Engine { get; }

    public void Dispose()
    {
        Engine.Dispose();
        SetupLog.Path = _previousLog;
        SetupExecutable.Path = _previousExecutable;
        _temporary.Dispose();
    }

    internal IReadOnlyList<SetupStep> InstallPlan()
    {
        return Engine.PlanInstall(new InstallChoices(null, ["wsgm.test"], new JsonObject()));
    }

    internal IReadOnlyList<SetupStep> InstallThroughService()
    {
        return [.. InstallPlan().TakeWhile(step => step.Label != "Installing media preview runtime")];
    }

    internal static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static bool DeleteNow(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
        else
        {
            File.Delete(path);
        }

        return true;
    }
}

internal sealed class RecordingSetupRuntime : IRuntimeShutdown
{
    internal List<string> Calls { get; } = [];
    internal Func<int, bool>? OnStop { get; set; }
    internal Func<string, string, int>? OnRun { get; set; }
    internal string? Version { get; set; } = "2.0.0.1";
    internal int Stops { get; private set; }

    public ServiceState? InspectService()
    {
        return new ServiceState(true, false);
    }

    public bool StopService()
    {
        Calls.Add("StopService");
        Stops++;
        return OnStop?.Invoke(Stops) ?? true;
    }

    public bool ShellRunning()
    {
        return false;
    }

    public string? RunningWsgmPath()
    {
        return null;
    }

    public ShutdownHandoff RequestExit(string eventName, int graceIterations)
    {
        return ShutdownHandoff.NotRunning;
    }

    public void ForceStopCurrentSession(string image)
    {
        Calls.Add("ForceStop " + image);
    }

    public bool ShellAnchorRecoverySettled()
    {
        return true;
    }

    public bool CloseSteam(TimeSpan budget)
    {
        return true;
    }

    public IReadOnlyList<string> Blockers(bool includeSteam)
    {
        return [];
    }

    public int Run(string file, string arguments)
    {
        Calls.Add("Run " + arguments);
        return OnRun?.Invoke(file, arguments) ?? 0;
    }

    public void Start(string file, string arguments)
    {
        Calls.Add("Start " + arguments);
    }

    public Mutex? ReserveDeviceOwner(TimeSpan wait)
    {
        return new Mutex(false);
    }

    public bool RunInnoUninstaller(string command, Func<bool> stillInstalled)
    {
        throw new InvalidOperationException("This fixture never runs an uninstaller.");
    }

    public string? InstalledVersion()
    {
        return Version;
    }

    public void RestoreVersion(string? version)
    {
        Calls.Add("RestoreVersion");
        Version = version;
    }
}
