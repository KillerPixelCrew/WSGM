using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Input;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.UiTests.Fakes;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Visual;

/// <summary>
///     Render-only scenes of the Emulators page over <see cref="PreviewEmulators" />: its five views, an emulator's
///     detail, the remove confirmation and an install in progress. Nothing is downloaded or installed.
/// </summary>
public static class PreviewEmulatorPage
{
    /// <summary>Every scene, in the order the page reaches them.</summary>
    internal static readonly string[] Scenes =
        ["installed", "catalog", "cores", "defaults", "bios", "detail", "remove", "installing"];

    /// <summary>Exports every scene at one viewport.</summary>
    /// <param name="directory">The output folder.</param>
    /// <param name="width">The viewport width in DIPs.</param>
    /// <param name="height">The viewport height in DIPs.</param>
    public static void Export(string directory, int width, int height)
    {
        Directory.CreateDirectory(directory);
        foreach (var scene in Scenes)
        {
            using var fixture = new UiFixture();
            var (window, _) = Open(fixture, scene, width, height);
            window.FocusManager!.Focus(null);
            window.MouseMove(new Point(-20, -20));
            using var image = window.CaptureRenderedFrame() ??
                              throw new InvalidOperationException("No Emulators preview was rendered.");
            image.Save(Path.Combine(directory, $"emulators-{scene}-{width}x{height}.png"),
                new PngBitmapEncoderOptions());
            window.Close();
        }
    }

    /// <summary>Opens the Overlay on Tools › Emulators and drives the page to a scene the way a user would.</summary>
    /// <param name="fixture">The fixture owning the window.</param>
    /// <param name="scene">One of <see cref="Scenes" />.</param>
    /// <param name="width">The viewport width in DIPs.</param>
    /// <param name="height">The viewport height in DIPs.</param>
    /// <returns>The window and the fake it reads.</returns>
    internal static (OverlayWindow Window, EmulatorOverlaySource Source) Open(UiFixture fixture, string scene,
        int width, int height)
    {
        var window = PreviewTools.Create(fixture, width, height);
        var source = PreviewEmulators.Create();
        if (scene is "detail" or "remove")
        {
            source.State = source.State with
            {
                Offers =
                [
                    .. source.State.Offers.Where(offer => offer is not { DefinitionId: "pcsx2", Channel: "nightly" }),
                    new EmulatorOffer
                    {
                        DefinitionId = "pcsx2", Channel = "nightly", Version = "2.3.0", ReleaseId = "new",
                        NotesUrl = "https://example.invalid/notes"
                    }
                ]
            };
        }

        if (scene == "installing")
        {
            source.State = source.State with { Busy = true, Status = "Downloading RPCS3 0.0.36" };
            source.Progress = new EmulatorProgressState(true, "Downloading RPCS3 0.0.36")
            {
                Operation = "install", TargetId = "rpcs3", Phase = "download", Bytes = 13_107_200,
                Total = 32_505_856
            };
        }

        UiFixture.Named<EmulatorManagerView>(window, "EmulatorManagerHost").Attach(source);
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.EmulatorManager));
        Dispatcher.UIThread.RunJobs();
        switch (scene)
        {
            case "catalog":
                Click(window, "Get emulators");
                break;
            case "cores":
                Click(window, "RetroArch cores");
                break;
            case "defaults":
                Click(window, "System defaults");
                break;
            case "bios":
                Click(window, "BIOS and firmware");
                break;
            case "detail":
                Click(window, "PCSX2");
                break;
            case "remove":
                Click(window, "PCSX2");
                Click(window, "Remove PCSX2…");
                break;
            case "installing":
                Click(window, "Get emulators");
                Click(window, "RPCS3");
                break;
        }

        Dispatcher.UIThread.RunJobs();
        return (window, source);
    }

    internal static void Click(OverlayWindow window, string title)
    {
        var button = FocusSearch.First<ActionButton>(window,
                         button => button.Title == title && button.IsEffectivelyVisible)
                     ?? throw new InvalidOperationException("Emulators action is missing: " + title);
        UiFixture.Click(window, button);
        Dispatcher.UIThread.RunJobs();
    }
}
