using System.Diagnostics;
using WSGM.DeviceLab.Preflight;

namespace WSGM.Device.Tests;

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
        string staged = directory.GetPath("staged.wsgmpkg");
        string archive = directory.GetPath("package.wsgmpkg");
        File.WriteAllText(staged, "new package");
        if (destinationExists)
        {
            File.WriteAllText(archive, "previous package");
        }
        string root = Assert.IsType<string>(DeviceLabRepositoryLocator.Find(AppContext.BaseDirectory));
        string helper = Path.Combine(root, "eng", "device-package-output.ps1");
        using FileStream? held = locked
            ? new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read)
            : null;
        ProcessStartInfo start = new()
        {
            FileName = "pwsh",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
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
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
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
                await process.WaitForExitAsync();
            }
        }
        string diagnostic = (await output) + (await error);

        Assert.True((process.ExitCode == 0) == succeeds, diagnostic);
        if (succeeds)
        {
            Assert.Equal("new package", File.ReadAllText(archive));
            Assert.False(File.Exists(staged));
        }
        else
        {
            Assert.Equal("new package", File.ReadAllText(staged));
            Assert.Equal(destinationExists, File.Exists(archive));
            if (destinationExists)
            {
                Assert.Equal("previous package", File.ReadAllText(archive));
            }
        }
    }
}
