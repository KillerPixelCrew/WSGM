using WSGM.Core;
using WSGM.Device.Tests;

namespace WSGM.Tests.Core;

public sealed class SteamGuideChordMirrorTests
{
    private const string ValveTemplate = """
        "controller_mappings"
        {
        	"version"		"3"
        	"revision"		"19"
        	"title"		"Steam Button Chord Basic Configuration"
        	"description"		""
        	"controller_type"		"controller_neptune"
        }
        """;

    private static string Autosave(int revision)
    {
        return $$"""
            "controller_mappings"
            {
            	"version"		"3"
            	"revision"		"{{revision}}"
            	"title"		"Steam Button Chord Basic Configuration"
            	"description"		"#SettingsController_AutosaveDescription"
            	"progenitor"		"default://c:\\program files (x86)\\steam/controller_base/chord_neptune.vdf"
            	"controller_type"		"controller_neptune"
            }
            """;
    }

    [Fact]
    public void MirrorsTheAutosaveOverTheTemplateAndKeepsValvesFileOnce()
    {
        using Rig rig = new();
        rig.WriteAutosave(Autosave(20));
        using var mirror = rig.Create();

        mirror.Apply(true, true);

        Assert.True(mirror.Active);
        Assert.Equal(Autosave(20), rig.Template);
        Assert.Equal(ValveTemplate, rig.Backup);

        rig.WriteAutosave(Autosave(21));
        mirror.Reconcile();
        Assert.Equal(Autosave(21), rig.Template);
        // The backup is Valve's file, never an earlier mirror.
        Assert.Equal(ValveTemplate, rig.Backup);
    }

    [Fact]
    public void ResetRestoresValvesTemplateAndIgnoresTheOlderAutosaveUntilANewerOne()
    {
        using Rig rig = new();
        rig.WriteAutosave(Autosave(20));
        using var mirror = rig.Create();
        mirror.Apply(true, true);

        Assert.True(mirror.RestoreDefault());
        Assert.Equal(ValveTemplate, rig.Template);

        // The autosave that preceded the reset must not come back on its own.
        mirror.Reconcile();
        Assert.Equal(ValveTemplate, rig.Template);

        Thread.Sleep(20);
        rig.WriteAutosave(Autosave(22));
        mirror.Reconcile();
        Assert.Equal(Autosave(22), rig.Template);
    }

    [Fact]
    public void TurningOffRestoresValvesTemplateAndForgetsTheBackup()
    {
        using Rig rig = new();
        rig.WriteAutosave(Autosave(20));
        using var mirror = rig.Create();
        mirror.Apply(true, true);

        mirror.Apply(false, true);

        Assert.False(mirror.Active);
        Assert.Equal(ValveTemplate, rig.Template);
        Assert.Null(rig.Backup);
    }

    [Fact]
    public void TheNewestChordLayoutIsMirroredWhateverSteamNamedIt()
    {
        using Rig rig = new();
        rig.WriteAutosave(Autosave(20));
        using var mirror = rig.Create();
        mirror.Apply(true, true);

        Thread.Sleep(20);
        rig.WriteAutosave(Autosave(24), "28de-1205-43fa5b1.vdf");
        Thread.Sleep(20);
        // A newer file for another controller type does not outrank the chord layout.
        rig.WriteAutosave(Autosave(30).Replace("controller_neptune", "controller_ps5", StringComparison.Ordinal),
            "controller_ps5.vdf");
        mirror.Reconcile();

        Assert.Equal(Autosave(24), rig.Template);
    }

    [Fact]
    public void ALayoutForAnotherControllerOrWithoutAChordProgenitorIsNotMirrored()
    {
        using Rig rig = new();
        rig.WriteAutosave(Autosave(20).Replace("controller_neptune", "controller_xbox360", StringComparison.Ordinal));
        using var mirror = rig.Create();

        mirror.Apply(true, true);

        Assert.Equal(ValveTemplate, rig.Template);
        Assert.Null(rig.Backup);
    }

    [Fact]
    public void ATemplateReplacedByASteamUpdateBecomesTheNewBackup()
    {
        using Rig rig = new();
        rig.WriteAutosave(Autosave(20));
        using var mirror = rig.Create();
        mirror.Apply(true, true);

        var updated = ValveTemplate.Replace("\"19\"", "\"30\"", StringComparison.Ordinal);
        rig.WriteTemplate(updated);
        Thread.Sleep(20);
        rig.WriteAutosave(Autosave(21));
        mirror.Reconcile();

        Assert.Equal(Autosave(21), rig.Template);
        Assert.Equal(updated, rig.Backup);
    }

    [Fact]
    public void UninstallRestoreWorksWithoutAWatcher()
    {
        using Rig rig = new();
        rig.WriteAutosave(Autosave(20));
        using (var mirror = rig.Create())
        {
            mirror.Apply(true, true);
            Assert.Equal(Autosave(20), rig.Template);
            // Disposal restores as well; the uninstall path has to cope with nothing left to do.
        }

        Assert.Equal(ValveTemplate, rig.Template);
        Assert.False(SteamGuideChordMirror.RestoreInstalledSteam(rig.Steam));
    }

    private sealed class Rig : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();

        internal Rig()
        {
            Directory.CreateDirectory(Path.Combine(Steam, "controller_base"));
            WriteTemplate(ValveTemplate);
        }

        internal string Steam => _directory.Root;

        internal string Template => File.ReadAllText(TemplatePath);

        internal string? Backup =>
            File.Exists(TemplatePath + SteamGuideChordMirror.BackupSuffix)
                ? File.ReadAllText(TemplatePath + SteamGuideChordMirror.BackupSuffix)
                : null;

        private string TemplatePath => Path.Combine(Steam, "controller_base", SteamGuideChordMirror.TemplateFileName);

        private string AutosaveDirectory => Path.Combine(
            Steam, "steamapps", "common", "Steam Controller Configs", "12345678", "config",
            SteamGuideChordMirror.ChordAppId.ToString());

        public void Dispose()
        {
            _directory.Dispose();
        }

        internal SteamGuideChordMirror Create()
        {
            return new SteamGuideChordMirror(Steam);
        }

        internal void WriteTemplate(string text)
        {
            File.WriteAllText(TemplatePath, text);
        }

        internal void WriteAutosave(string text, string fileName = "controller_neptune.vdf")
        {
            Directory.CreateDirectory(AutosaveDirectory);
            File.WriteAllText(Path.Combine(AutosaveDirectory, fileName), text);
        }
    }
}
