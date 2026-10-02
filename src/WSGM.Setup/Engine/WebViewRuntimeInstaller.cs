using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace WSGM.Setup.Engine;

/// <summary>The shared Evergreen runtime, carried offline and left installed for other applications.</summary>
internal static class WebViewRuntimeInstaller
{
    internal static bool Present()
    {
        const string client = @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(client);
                if (Version.TryParse(key?.GetValue("pv") as string, out var version) && version > new Version(0, 0))
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static bool Install(SetupPayload payload, SetupStep step)
    {
        if (Present())
        {
            step.DoneLabel = "WebView2 runtime already installed";
            step.State = StepState.Skipped;
            return true;
        }

        using var pinStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WSGM.WebView2RuntimePin")
                              ?? throw new InvalidOperationException("The WebView2 runtime pin is missing.");
        using var pin = JsonDocument.Parse(pinStream);
        var asset = pin.RootElement.GetProperty("asset").GetString()!;
        var expected = pin.RootElement.GetProperty("sha256").GetString()!;
        var stage = Path.Combine(Path.GetTempPath(), "WSGM-webview2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var installer = Path.Combine(stage, asset);
        try
        {
            payload.ExtractFile("MediaRuntime/" + asset, installer);
            using (var input = File.OpenRead(installer))
            {
                if (!Convert.ToHexString(SHA256.HashData(input)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The WebView2 runtime checksum does not match the setup's pin.");
                }
            }

            var code = WindowsSetup.Run(installer, "/silent /install");
            if (code != 0 || !Present())
            {
                step.Note =
                    $"WebView2 setup exited with code {code}. Media previews require the Microsoft WebView2 runtime.";
                return false;
            }

            return true;
        }
        finally
        {
            if (File.Exists(installer))
            {
                File.Delete(installer);
            }

            Directory.Delete(stage);
        }
    }
}
