using System.Text.RegularExpressions;
using WSGM.Core;

namespace WSGM.Tests;

/// <summary>
/// Guards the boundary the Steam UI machinery is being lifted out along. These files are intended
/// to become a framework other applications consume, so anything of WSGM's they reach for is a
/// dependency that would have to travel with them or be abstracted at the last minute.
/// </summary>
public sealed class SteamUiToolkitBoundaryTests
{
    // Transport, patch lifecycle, bridge, and the module contract. Deliberately not the gates or
    // the patches: those are WSGM's surfaces and are expected to use whatever they need.
    private static readonly string[] ToolkitFiles =
    [
        "PersistentSteamUiTransport.cs",
        "SteamUiCdpConnection.cs",
        "SteamUiEndpointDiscovery.cs",
        "SteamUiTransportModels.cs",
        "SteamUiTransportSession.cs",
        "SteamUiPatchManager.cs",
        "SteamUiPatchEvaluation.cs",
        "SteamUiBridge.cs",
        "SteamUiBridgeIdentity.cs",
        "SteamUiInjectedAsset.cs",
        "SteamUiLog.cs",
        "SteamUiModule.cs",
        "SteamUiModuleRuntime.cs",
    ];

    [Fact]
    public void TheMachineryNeverWritesThroughWsgmsOwnLogger()
    {
        // It writes through its own sink, which WSGM installs. A direct Log call would compile
        // fine and quietly re-couple the framework to this application's logger — and the sink
        // exists so the lines still land in wsgm.log, so nothing would look wrong either.
        List<string> offenders = [];
        foreach (string file in ToolkitFiles)
        {
            string source = ReadToolkitFile(file);
            if (Regex.IsMatch(source, @"(?<![A-Za-z])Log\.(Info|Warn|Error|Change)\s*\("))
            {
                offenders.Add(file);
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheBridgeDoesNotReachForWsgmsAsset()
    {
        // The host names what it injects. A bridge that loads a fixed catalog entry cannot be
        // handed a different script, which is exactly what a second consumer would need to do.
        Assert.DoesNotContain("SteamUiAssetCatalog", ReadToolkitFile("SteamUiBridge.cs"));
    }

    [Fact]
    public void TheSinkDiscardsUntilAHostInstallsOne()
    {
        // Tests never initialize WSGM's logger, so the machinery must be silent by default rather
        // than throwing or writing somewhere. Asserted by calling every member on the default.
        SteamUiLog.Use(null);
        SteamUiLog.Info("discarded");
        SteamUiLog.Warn("discarded");
        SteamUiLog.Change("key", "discarded");
        SteamUiLog.Change("key", "discarded", warning: true);
    }

    [Fact]
    public void AnInstalledSinkReceivesWhatTheMachineryWrites()
    {
        RecordingLog recording = new();
        try
        {
            SteamUiLog.Use(recording);
            SteamUiLog.Warn("a failure");
            SteamUiLog.Change("k", "a transition", warning: true);
        }
        finally
        {
            SteamUiLog.Use(null);
        }

        Assert.Equal(["warn:a failure", "change:k:a transition:warn"], recording.Lines);
    }

    private static string ReadToolkitFile(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "WSGM.slnx")))
        {
            directory = directory.Parent;
        }

        string root = directory?.FullName
            ?? throw new InvalidOperationException("The repository root was not found.");
        return File.ReadAllText(Path.Combine(root, "src", "WSGM", "Core", name));
    }

    private sealed class RecordingLog : ISteamUiLog
    {
        public List<string> Lines { get; } = [];

        public void Info(string message) => Lines.Add($"info:{message}");

        public void Warn(string message) => Lines.Add($"warn:{message}");

        public void Change(string key, string message, bool warning = false) =>
            Lines.Add($"change:{key}:{message}:{(warning ? "warn" : "info")}");
    }
}
