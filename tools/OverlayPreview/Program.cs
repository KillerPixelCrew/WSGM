using WSGM.UiTests.Infrastructure;
using WSGM.UiTests.Visual;

namespace WSGM.OverlayPreview;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var output = Path.GetFullPath(args.Length > 0 ? args[0] : "TestResults/overlay-preview");
        var width = args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 1280;
        var height = args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 720;
        var scale = args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 1;
        TestApplication.BuildAvaloniaApp().SetupWithoutStarting();
        PreviewExports.Export(output, width, height, scale);
    }
}
