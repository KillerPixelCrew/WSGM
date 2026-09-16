using System.Diagnostics;
using WSGM.Device.Tests;
using WSGM.DeviceLab.Preflight;

namespace WSGM.DeviceLab.Tests.Eng;

public sealed class DevicePackageOutputTests
{
    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    public async Task ArchivePublicationPreservesFilesUntilItCommits(
        bool destinationExists, bool replaceExisting, bool locked, bool succeeds)
    {
        using TemporaryDirectory directory = new();
        var staged = directory.GetPath("staged.wsgmpkg");
        var archive = directory.GetPath("package.wsgmpkg");
        await File.WriteAllTextAsync(staged, "new package");
        if (destinationExists)
        {
            await File.WriteAllTextAsync(archive, "previous package");
        }
        var root = Assert.IsType<string>(DeviceLabRepositoryLocator.Find(AppContext.BaseDirectory));
        var helper = Path.Combine(root, "eng", "device-package-output.ps1");
        await using var held = locked
            ? new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read)
            : null;
        ProcessStartInfo start = new()
        {
            FileName = "pwsh",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$ErrorActionPreference = 'Stop'; . $env:WSGM_TEST_PACKAGE_HELPER; "
            + "Publish-DevicePackageArchive -StagedArchive $env:WSGM_TEST_STAGED_ARCHIVE "
            + "-Archive $env:WSGM_TEST_ARCHIVE -ReplaceExisting:($env:WSGM_TEST_REPLACE -eq '1')");
        start.Environment["WSGM_TEST_PACKAGE_HELPER"] = helper;
        start.Environment["WSGM_TEST_STAGED_ARCHIVE"] = staged;
        start.Environment["WSGM_TEST_ARCHIVE"] = archive;
        start.Environment["WSGM_TEST_REPLACE"] = replaceExisting ? "1" : "0";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        var diagnostic = await output + await error;

        Assert.True(process.ExitCode == 0 == succeeds, diagnostic);
        if (succeeds)
        {
            Assert.Equal("new package", await File.ReadAllTextAsync(archive, CancellationToken.None));
            Assert.False(File.Exists(staged));
        }
        else
        {
            Assert.Equal("new package", await File.ReadAllTextAsync(staged, CancellationToken.None));
            Assert.Equal(destinationExists, File.Exists(archive));
            if (destinationExists)
            {
                Assert.Equal("previous package", await File.ReadAllTextAsync(archive, CancellationToken.None));
            }
        }
    }
}
