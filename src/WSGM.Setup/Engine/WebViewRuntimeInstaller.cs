using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace WSGM.Setup.Engine;

/// <summary>The shared Evergreen runtime, bundled offline or downloaded online and left installed for other applications.</summary>
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
        if (Path.GetFileName(asset) != asset)
        {
            throw new InvalidDataException("The WebView2 runtime pin is invalid.");
        }

        var stage = Path.Combine(Path.GetTempPath(), "WSGM-webview2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var installer = Path.Combine(stage, asset);
        try
        {
            if (payload.Online)
            {
                var download = new Uri(pin.RootElement.GetProperty("url").GetString()!, UriKind.Absolute);
                if (!RtssInstaller.TryDownload(new SocketsHttpHandler(), download, installer,
                        RtssInstaller.BodyStallTimeout, out var problem))
                {
                    step.Note = $"Setup could not download WebView2 ({problem}). Connect to the internet and run Repair. Media previews need WebView2.";
                    return false;
                }
            }
            else
            {
                try
                {
                    payload.ExtractFile("MediaRuntime/" + asset, installer);
                }
                catch (FileNotFoundException)
                {
                    step.Note = "This setup is missing the bundled WebView2 installer. Use a complete offline setup or install Microsoft WebView2, then run Repair.";
                    return false;
                }
            }

            using (var input = File.OpenRead(installer))
            {
                if (!Convert.ToHexString(SHA256.HashData(input)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    step.Note = "The WebView2 runtime checksum does not match this setup's pin, so setup did not run it. Use a complete WSGM setup or install Microsoft WebView2.";
                    return false;
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
            try
            {
                Directory.Delete(stage, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetupLog.Warn($"WebView2: the installer folder could not be removed: {ex.Message}");
            }
        }
    }
}
