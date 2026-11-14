using System.Diagnostics;
using System.Text.Json;
using LibGPUDriverInteract;
using SteamUiToolkit;
using WSGM.Shell;
using WSGM.Testing;
using WSGM.Tests.Fakes;

namespace WSGM.Tests.Shell;

public sealed partial class SteamNativeSettingsServiceTests
{
    [Theory]
    [InlineData("empty")]
    [InlineData("failed")]
    [InlineData("available")]
    public async Task DriverSectionsReachTheEmittedDisplayGateWithoutDeviceIntegration(string scenario)
    {
        using var config = new TemporaryConfigStore();
        var adapter = new GpuInstalledDriver("atlas", "VEN_8086&DEV_B083", GpuVendor.Intel,
            "Intel(R) Arc(TM) B370 GPU", "32.0.101.9150", null);
        await using var updates = new GpuDriverUpdateService(config.Store, () => false, _ => null, action => action(),
            readInstalled: () => scenario == "empty" ? [] : [adapter],
            checkPackage: (installed, _, _) => scenario == "failed"
                ? throw new System.IO.IOException("Catalog unavailable")
                : Task.FromResult(new GpuDriverPackage(installed, "32.0.101.9175", new DateTime(2026, 11, 3),
                    new Uri("https://downloadmirror.intel.com/fixture.exe"), new Uri("https://www.intel.com/driver"),
                    "Fixture release notes", 1024, null)),
            waitForSession: token => Task.Delay(Timeout.Infinite, token));
        Assert.True(updates.Check());
        await updates.Completion;
        await using var rig = await ProducerRig.CreateAsync(updates);
        var state = (await rig.Producer.ReadAsync())!;
        var display = Assert.Single(state.Pages, page => page.Id == SteamNativeSettingsPageId.Display);
        var section = Assert.Single(display.Sections, item => item.Id == "driver-updates");
        Assert.Equal("driver-updates", section.Level?.Id);
        Assert.Equal(SteamNativeSettingsService.MoreSettingsGroup, section.Level?.Group);
        Assert.Equal(scenario == "available" ? "Update available" : null, section.Level?.Badge);
        Assert.Contains(section.Rows, row => row.Key == "driver-updates.check");
        Assert.Contains(section.Rows, row => row.Key == "driver-updates.non-whql" && row.Checked == false);
        if (scenario == "available")
        {
            Assert.Contains(section.Rows, row => row.Key == "driver-updates.certification:atlas" && row.Text == "WHQL certified");
        }

        // Run the exact host JSON through the shipped gate, with Steam and device owners inert.
        using var temporary = new TemporaryDirectory();
        var payload = System.IO.Path.Combine(temporary.Root, "native-settings.json");
        await System.IO.File.WriteAllTextAsync(payload, SteamNativeSettingsSurface.Serialize(state).GetRawText());
        var root = WSGM.Testing.RepositoryFiles.Root;
        var start = new ProcessStartInfo("node") { WorkingDirectory = root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("tests/WSGM.Tests/Shell/Steam/driver-updates-render.mjs");
        start.ArgumentList.Add(payload);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(process.ExitCode == 0, await output + await error);

        Assert.True((await rig.Producer.SetAsync("driver-updates.non-whql", JsonSerializer.SerializeToElement(true),
            CancellationToken.None)).Succeeded);
        await updates.Completion;
        Assert.True(updates.IncludeNonWhql);
    }
}
