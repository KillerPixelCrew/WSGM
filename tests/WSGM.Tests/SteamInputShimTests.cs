using System.Text;
using WSGM.Core;

namespace WSGM.Tests;

/// <summary>Deployment coverage for the Steam Input shim: which candidate name it
/// takes, when it refuses to touch a file, when it re-copies, and what the disable
/// toggle does. Everything runs against a temporary directory standing in for
/// Steam's install directory - nothing here reads or writes a real Steam or
/// %LOCALAPPDATA%\WSGM.</summary>
public sealed class SteamInputShimTests : IDisposable
{
    private const string XInput = "XInput1_4.dll";
    private const string DInput = "dinput8.dll";

    /// <summary>The export name the deployer uses as proof a file is WSGM's own.</summary>
    private const string Signature = "WsgmSteamInputGateProxy";

    private readonly string _steamDir;
    private readonly string _sourceDir;
    private readonly string _payload;

    public SteamInputShimTests()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "wsgm-shim-tests", Guid.NewGuid().ToString("N"));
        _steamDir = Path.Combine(root, "Steam");
        _sourceDir = Path.Combine(root, "app");
        Directory.CreateDirectory(_steamDir);
        Directory.CreateDirectory(_sourceDir);
        _payload = Path.Combine(_sourceDir, "steam_input_gate.dll");
        WritePayload(_payload, "v1");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_steamDir)!, recursive: true);
        }
        catch
        {
            // A locked temp file must never fail the suite.
        }
    }

    private static void WritePayload(string path, string body) =>
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes($"MZ...{Signature}...{body}"));

    private static void WriteForeign(string path) =>
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("MZ... someone else's controller dll"));

    private SteamInputShimStatus Reconcile(bool enabled = true) =>
        SteamInputShim.ReconcileIn(_steamDir, _payload, enabled, "test");

    private string InSteam(string name) => Path.Combine(_steamDir, name);

    [Fact]
    public void ChoosesXInputWhenBothCandidateNamesAreFree()
    {
        var status = Reconcile();

        Assert.Equal(SteamInputShimState.Deployed, status.State);
        Assert.Equal(SteamInputShimVector.XInput14, status.Vector);
        Assert.True(File.Exists(InSteam(XInput)));
        Assert.False(File.Exists(InSteam(DInput)));
    }

    [Fact]
    public void FallsBackToDInputWhenXInputBelongsToAnotherProgram()
    {
        WriteForeign(InSteam(XInput));

        var status = Reconcile();

        Assert.Equal(SteamInputShimState.Deployed, status.State);
        Assert.Equal(SteamInputShimVector.DInput8, status.Vector);
        Assert.True(File.Exists(InSteam(DInput)));
    }

    [Fact]
    public void NeverOverwritesAForeignDllAtEitherCandidateName()
    {
        WriteForeign(InSteam(XInput));
        WriteForeign(InSteam(DInput));
        var xinput = File.ReadAllBytes(InSteam(XInput));
        var dinput = File.ReadAllBytes(InSteam(DInput));

        var status = Reconcile();

        Assert.Equal(SteamInputShimState.Blocked, status.State);
        Assert.Equal(SteamInputShimVector.None, status.Vector);
        Assert.Equal(xinput, File.ReadAllBytes(InSteam(XInput)));
        Assert.Equal(dinput, File.ReadAllBytes(InSteam(DInput)));
    }

    [Fact]
    public void AdoptsItsOwnDeployedFileInsteadOfCopyingAgain()
    {
        Reconcile();
        var firstWrite = File.GetLastWriteTimeUtc(InSteam(XInput));

        var status = Reconcile();

        Assert.Equal(SteamInputShimState.Deployed, status.State);
        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(InSteam(XInput)));
    }

    [Fact]
    public void RecopiesWhenTheStagedPayloadChanged()
    {
        Reconcile();
        WritePayload(_payload, "v2-much-longer-body-so-the-length-differs");

        var status = Reconcile();

        Assert.Equal(SteamInputShimState.Deployed, status.State);
        Assert.Contains("v2", Encoding.ASCII.GetString(File.ReadAllBytes(InSteam(XInput))));
    }

    [Fact]
    public void RecopiesWhenTheDeployedFileWasReplacedUnderneathUs()
    {
        Reconcile();
        // Same ownership signature, different content: the stamp records the
        // deployed file's identity precisely so this is detected.
        WritePayload(InSteam(XInput), "tampered-and-a-different-length-entirely");

        var status = Reconcile();

        Assert.Equal(SteamInputShimState.Deployed, status.State);
        Assert.Contains("v1", Encoding.ASCII.GetString(File.ReadAllBytes(InSteam(XInput))));
    }

    [Fact]
    public void TreatsAnUnparseableStampAsStale()
    {
        Reconcile();
        File.WriteAllText(InSteam("XInput1_4.wsgm-shim"), "not a stamp this build knows");
        WritePayload(InSteam(XInput), "stale-copy-of-a-different-length");

        var status = Reconcile();

        Assert.Equal(SteamInputShimState.Deployed, status.State);
        Assert.Contains("v1", Encoding.ASCII.GetString(File.ReadAllBytes(InSteam(XInput))));
    }

    [Fact]
    public void DisablingParksTheDeployedFileWithoutDeletingIt()
    {
        Reconcile();

        var status = Reconcile(enabled: false);

        Assert.Equal(SteamInputShimState.Disabled, status.State);
        Assert.False(File.Exists(InSteam(XInput)));
        Assert.True(File.Exists(InSteam("XInput1_4.dlld")));
    }

    [Fact]
    public void EnablingRestoresTheParkedFileToItsCandidateName()
    {
        Reconcile();
        Reconcile(enabled: false);

        var status = Reconcile();

        Assert.Equal(SteamInputShimState.Deployed, status.State);
        Assert.True(File.Exists(InSteam(XInput)));
        Assert.False(File.Exists(InSteam("XInput1_4.dlld")));
    }

    [Fact]
    public void DoesNotRestoreOverAForeignFileThatAppearedWhileParked()
    {
        Reconcile();
        Reconcile(enabled: false);
        WriteForeign(InSteam(XInput));
        var foreign = File.ReadAllBytes(InSteam(XInput));

        var status = Reconcile();

        Assert.Equal(foreign, File.ReadAllBytes(InSteam(XInput)));
        Assert.Equal(SteamInputShimVector.DInput8, status.Vector);
    }

    [Fact]
    public void RemovesACopyLeftOnACandidateNameItNoLongerUses()
    {
        Reconcile();
        Assert.True(File.Exists(InSteam(XInput)));
        // Force the fallback by making the preferred name unavailable, then free it
        // again: the abandoned copy must not linger.
        File.Move(InSteam(XInput), InSteam("held.tmp"));
        WriteForeign(InSteam(XInput));
        Reconcile();
        Assert.True(File.Exists(InSteam(DInput)));

        File.Delete(InSteam(XInput));
        Reconcile();

        Assert.True(File.Exists(InSteam(XInput)));
        Assert.False(File.Exists(InSteam(DInput)));
    }

    [Fact]
    public void ReportsSteamNotInstalledWithoutThrowingWhenTheDirectoryIsMissing()
    {
        var status = SteamInputShim.ReconcileIn(
            Path.Combine(_steamDir, "does-not-exist"), _payload, enabled: true, "test");

        Assert.Equal(SteamInputShimState.SteamNotInstalled, status.State);
    }

    [Fact]
    public void ReportsFailureRatherThanDeployingWhenThePayloadIsMissing()
    {
        var status = SteamInputShim.ReconcileIn(
            _steamDir, Path.Combine(_sourceDir, "absent.dll"), enabled: true, "test");

        Assert.Equal(SteamInputShimState.Failed, status.State);
        Assert.False(File.Exists(InSteam(XInput)));
    }

    [Fact]
    public void RemoveDeletesOnlyFilesItCanProveAreItsOwn()
    {
        Reconcile();
        WriteForeign(InSteam(DInput));
        var foreign = File.ReadAllBytes(InSteam(DInput));

        SteamInputShim.RemoveIn(_steamDir, "test");

        Assert.False(File.Exists(InSteam(XInput)));
        Assert.Equal(foreign, File.ReadAllBytes(InSteam(DInput)));
    }

    [Fact]
    public void ProbeReportsTheDeployedVectorWithoutWritingAnything()
    {
        Reconcile();
        var before = Directory.GetFiles(_steamDir).Length;

        var status = SteamInputShim.ProbeIn(_steamDir, _payload, enabled: true);

        Assert.Equal(SteamInputShimState.Deployed, status.State);
        Assert.Equal(SteamInputShimVector.XInput14, status.Vector);
        Assert.Equal(before, Directory.GetFiles(_steamDir).Length);
    }

    [Fact]
    public void ProbeReportsBlockedWhenEveryCandidateNameIsForeign()
    {
        WriteForeign(InSteam(XInput));
        WriteForeign(InSteam(DInput));

        var status = SteamInputShim.ProbeIn(_steamDir, _payload, enabled: true);

        Assert.Equal(SteamInputShimState.Blocked, status.State);
    }

    [Fact]
    public void StampRoundTripsEveryFieldItRecords()
    {
        var marker = new SteamInputShim.Marker(11, 22, 33, 44, SteamInputShimVector.DInput8);

        Assert.True(SteamInputShim.Marker.TryParse(marker.Format(), out var parsed));
        Assert.Equal(marker, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("WSGM-SIM/99 1 2 3 4 DInput8")]
    [InlineData("WSGM-SIM/1 1 2 3 DInput8")]
    [InlineData("WSGM-SIM/1 x 2 3 4 DInput8")]
    public void StampRejectsAnythingItDidNotWrite(string line)
    {
        Assert.False(SteamInputShim.Marker.TryParse(line, out _));
    }
}
