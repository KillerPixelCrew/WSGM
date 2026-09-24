using System;
using System.Linq;
using WSGM.DeviceLab.Cli;
using WSGM.DeviceLab.Gui;
using WSGM.DeviceLab.Probes;
using WSGM.DeviceLab.Testing;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0 || string.Equals(args[0], "wizard", StringComparison.Ordinal))
        {
            return RunWizard(args.Length == 0 ? [] : args[1..]);
        }

        if (string.Equals(args[0], "gui", StringComparison.Ordinal))
        {
            return DeviceLabGui.Run(args[1..]);
        }

        if (string.Equals(args[0], ReadProbeWorker.Mode, StringComparison.Ordinal))
        {
            return ReadProbeWorker.Run(args[1..]);
        }

        return string.Equals(args[0], PluginTestWorker.Mode, StringComparison.Ordinal)
            ? PluginTestWorker.Run(args[1..])
            : DeviceLabCli.RunAsync(args).GetAwaiter().GetResult();
    }

    // The wizard needs an administrator token for HidHide, PawnIO and the hardware stages. It asks
    // once; a declined prompt still opens the wizard with those checks skipped.
    private static int RunWizard(string[] args)
    {
        var relaunched = args.Contains(WizardElevation.RelaunchedArgument, StringComparer.Ordinal);
        string[] rest =
            [.. args.Where(arg => !string.Equals(arg, WizardElevation.RelaunchedArgument, StringComparison.Ordinal))];
        var projectIndex = Array.IndexOf(rest, "--project");
        var project = projectIndex >= 0 && projectIndex + 1 < rest.Length ? rest[projectIndex + 1] : null;
        var outcome = WizardElevation.EnsureElevated(["wizard", .. rest], relaunched);
        return outcome switch
        {
            WizardElevationOutcome.Relaunched => 0,
            WizardElevationOutcome.AlreadyElevated => DeviceLabGui.RunWizard(new WizardOptions(true, null, project)),
            WizardElevationOutcome.Declined => DeviceLabGui.RunWizard(new WizardOptions(false,
                "You declined the administrator prompt, so HidHide, PawnIO and the hardware checks are skipped. Restart Device Lab to try again.",
                project)),
            _ => DeviceLabGui.RunWizard(new WizardOptions(false,
                "Device Lab could not start as administrator, so HidHide, PawnIO and the hardware checks are skipped.",
                project))
        };
    }
}
