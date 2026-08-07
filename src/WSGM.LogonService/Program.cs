using System;
using System.Linq;

namespace WSGM.LogonService;

/// <summary>Entry point: SCM dispatcher by default, elevated install/uninstall
/// one-shots for the Inno installer.</summary>
internal static class Program
{
    internal static int Main(string[] args)
    {
        if (args.Contains("--install", StringComparer.OrdinalIgnoreCase))
        {
            return ServiceInstaller.Install();
        }
        if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            return ServiceInstaller.Uninstall();
        }
        return ServiceHost.RunDispatcher();
    }
}
