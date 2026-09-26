using System.Text;
using WSGM.Core;
using WSGM.Device.Tests;

namespace WSGM.Tests.Core;

public sealed class SteamGuideChordMirrorTests
{
    // Valve's file is indented the way Steam writes it, so a mirrored autosave fits once its
    // indentation is gone, the way the real 6 KB template does.
    private const string ValveTemplate = """
        "controller_mappings"
        {
        	"version"		"3"
        	"revision"		"19"
        	"title"		"Steam Button Chord Basic Configuration"
        	"description"		""
        	"controller_type"		"controller_neptune"
        	"group"
        	{
        		"id"		"0"
        		"mode"		"switches"
        		"inputs"
        		{
        			"button_b"
        			{
        				"activators"
        				{
        					"Long_Press"
        					{
        						"bindings"
        						{
        							"binding"		"controller_action quit_application"
        						}
        					}
        				}
        			}
        			"button_x"
        			{
        				"activators"
        				{
        					"Full_Press"
        					{
        						"bindings"
        						{
        							"binding"		"controller_action screenshot"
        						}
        					}
        				}
        			}
        		}
        	}
        }
        """;

    private static readonly int ValveSize = Encoding.UTF8.GetByteCount(ValveTemplate);

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
            	"group"
            	{
            		"id"		"0"
            		"mode"		"switches"
            		"inputs"
            		{
            			"button_a"
            			{
            				"activators"
            				{
            					"Full_Press"
            					{
            						"bindings"
            						{
            							"binding"		"controller_action gr_toggle, , "
            						}
            					}
            				}
            			}
            		}
            	}
            }
            """;
    }

    private static string Mirrored(int revision)
    {
        return SteamGuideChordMirror.FitToSize(Autosave(revision), ValveSize)!;
    }

    [Fact]
    public void MirrorsTheAutosaveOverTheTemplateAndKeepsValvesFileOnce()
    {
        using Rig rig = new();
        rig.WriteAutosave(Autosave(20));
        using var mirror = rig.Create();

        mirror.Apply(true, true);

        Assert.True(mirror.Active);
        Assert.Equal(Mirrored(20), rig.Template);
        Assert.Equal(ValveTemplate, rig.Backup);

        rig.WriteAutosave(Autosave(21));
        mirror.Reconcile();
        Assert.Equal(Mirrored(21), rig.Template);
        // The backup is Valve's file, never an earlier mirror.
        Assert.Equal(ValveTemplate, rig.Backup);
    }

    [Fact]
    public void TheMirrorHasExactlyValvesByteCountSoSteamsSizeCheckPasses()
    {
        using Rig rig = new();
        rig.WriteAutosave(Autosave(20));
        using var mirror = rig.Create();

        mirror.Apply(true, true);

        Assert.Equal(ValveSize, rig.TemplateBytes);
        Assert.Contains("\"revision\"\t\"20\"", rig.Template, StringComparison.Ordinal);
        Assert.Contains("\"progenitor\"", rig.Template, StringComparison.Ordinal);
    }

    [Fact]
    public void ALayoutThatDoesNotFitValvesFileIsLeftAlone()
    {
        using Rig rig = new();
        rig.WriteTemplate("\"controller_mappings\"\n{\n\t\"controller_type\"\t\t\"controller_neptune\"\n}\n");
        rig.WriteAutosave(Autosave(20));
        using var mirror = rig.Create();

        mirror.Apply(true, true);

        Assert.DoesNotContain("progenitor", rig.Template, StringComparison.Ordinal);
    }

    [Fact]
    public void FitToSizeDropsWhitespaceInStagesAndPadsToTheByte()
    {
        const string layout = "\"a\"\n{\n\t\"k\"\t\t\"v\"\n\t\"g\"\n\t{\n\t\t\"x\"\t\t\"y\"\n\t}\n}\n";

        Assert.Equal("\"a\"\n{\n\"k\"\t\"v\"\n\"g\"\n{\n\"x\"\t\"y\"\n}\n}\n\n\n", SteamGuideChordMirror.FitToSize(layout, 34));
        Assert.Equal("\"a\"{\"k\"\"v\"\"g\"{\"x\"\"y\"}}\n", SteamGuideChordMirror.FitToSize(layout, 23));
        Assert.Null(SteamGuideChordMirror.FitToSize(layout, 21));
    }

    [Fact]
    public void ATemplateSteamPutBackIsMirroredAgain()
    {
        using Rig rig = new();
        rig.WriteAutosave(Autosave(20));
        using var mirror = rig.Create();
        mirror.Apply(true, true);

        // The bootstrapper's reinstall, or a client update with the same file.
        rig.WriteTemplate(ValveTemplate);
        mirror.Reconcile();

        Assert.Equal(Mirrored(20), rig.Template);
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
        Assert.Equal(Mirrored(22), rig.Template);
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

        Assert.Equal(Mirrored(24), rig.Template);
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

        Assert.Equal(Mirrored(21), rig.Template);
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
            Assert.Equal(Mirrored(20), rig.Template);
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

        internal int TemplateBytes => checked((int)new FileInfo(TemplatePath).Length);

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
            File.WriteAllText(TemplatePath, text, new UTF8Encoding(false));
        }

        internal void WriteAutosave(string text, string fileName = "controller_neptune.vdf")
        {
            Directory.CreateDirectory(AutosaveDirectory);
            File.WriteAllText(Path.Combine(AutosaveDirectory, fileName), text, new UTF8Encoding(false));
        }
    }
}
