using System.Diagnostics;
using System.Runtime.Loader;
using System.Xml.Linq;
using WSGM.Device.Sdk;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Identity;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Testing;
using WSGM.Device.Tests;
using WSGM.DeviceLab.Capture;
using WSGM.DeviceLab.Inventory;
using WSGM.DeviceLab.Packaging;
using WSGM.DeviceLab.Preflight;
using WSGM.DeviceLab.Scaffolding;

namespace WSGM.DeviceLab.Tests.Scaffolding;

public sealed class DeviceLabScaffoldingTests
{
    [Fact]
    public void SdkReference_InWsgmCheckout_UsesTheSharedSdkProject()
    {
        var root = Assert.IsType<string>(DeviceLabRepositoryLocator.Find(AppContext.BaseDirectory));
        DeviceLabPathBoundaries boundaries = new()
        {
            RepositoryRoot = root,
            LiveDataDirectory = Path.Combine(Path.GetTempPath(), "never-live-wsgm"),
            BroadHomeDirectories = []
        };

        var reference = XElement.Parse(ScaffoldFromCaptureWorkflow.SdkReferenceXml(boundaries));

        Assert.Equal("ProjectReference", reference.Name.LocalName);
        Assert.Equal(
            Path.Combine(root, "src", "WSGM.Device.Sdk", "WSGM.Device.Sdk.csproj"),
            (string?)reference.Attribute("Include"));
    }

    [Fact]
    public void SdkReference_OutsideCheckout_UsesTheExactShippedAssemblyWithoutAnUndefinedProperty()
    {
        DeviceLabPathBoundaries boundaries = new()
        {
            LiveDataDirectory = Path.Combine(Path.GetTempPath(), "never-live-wsgm"),
            BroadHomeDirectories = []
        };

        var reference = ScaffoldFromCaptureWorkflow.SdkReferenceXml(boundaries);
        var element = XElement.Parse(reference);

        Assert.Equal("Reference", element.Name.LocalName);
        Assert.Equal("WSGM.Device.Sdk", (string?)element.Attribute("Include"));
        Assert.Equal("false", (string?)element.Element("Private"));
        var hintPath = Assert.IsType<string>((string?)element.Element("HintPath"));
        Assert.Equal(Path.GetFullPath(typeof(DeviceApi).Assembly.Location), hintPath);
        Assert.True(File.Exists(hintPath));
        Assert.DoesNotContain("$(WsgmRepositoryRoot)", reference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scaffold_OutsideCheckout_BuildsAgainstTheExactShippedSdkAssembly()
    {
        using TemporaryDirectory temporary = new();
        var capturePath = temporary.GetPath("source.wsgmcap");
        await using (FileStream capture = new(capturePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            CaptureBundleWriter.Write(capture, Capture());
        }
        DeviceLabPathBoundaries boundaries = new()
        {
            LiveDataDirectory = temporary.GetPath("never-live-wsgm"),
            BroadHomeDirectories = []
        };

        var result = ScaffoldFromCaptureWorkflow.Run(
            capturePath,
            temporary.GetPath("scaffold"),
            boundaries);

        var projectPath = Directory.EnumerateFiles(result.OutputDirectory, "*.csproj").Single();
        var project = XDocument.Load(projectPath);
        var reference = Assert.Single(project.Descendants("Reference"));
        var hintPath = Assert.IsType<string>((string?)reference.Element("HintPath"));
        Assert.Equal(Path.GetFullPath(typeof(DeviceApi).Assembly.Location), hintPath);
        Assert.True(File.Exists(hintPath));
        Assert.Equal("x64", Assert.Single(project.Descendants("PlatformTarget")).Value);
        Assert.DoesNotContain("$(WsgmRepositoryRoot)", await File.ReadAllTextAsync(projectPath), StringComparison.Ordinal);
        Assert.Contains(
            project.Descendants("None"),
            item => string.Equals((string?)item.Attribute("Update"), "LICENSE.txt", StringComparison.Ordinal)
                && string.Equals((string?)item.Attribute("CopyToOutputDirectory"), "PreserveNewest", StringComparison.Ordinal)
                && string.Equals((string?)item.Attribute("CopyToPublishDirectory"), "PreserveNewest", StringComparison.Ordinal));
        Assert.Contains("LICENSE.txt", result.Files);
        Assert.True(File.Exists(Path.Combine(result.OutputDirectory, "LICENSE.txt")));
        // A scaffolded plugin links the MIT SDK, never WSGM, so its author picks its licence. The
        // starter ships MIT with a placeholder rather than stamping the plugin with WSGM's GPL-3
        // and this project's copyright holder, which claimed something untrue about their work.
        var scaffoldedLicense =
            (await File.ReadAllTextAsync(Path.Combine(result.OutputDirectory, "LICENSE.txt"))).TrimStart();
        Assert.StartsWith("MIT License", scaffoldedLicense, StringComparison.Ordinal);
        Assert.Contains("<your name here>", scaffoldedLicense, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "GNU GENERAL PUBLIC LICENSE", scaffoldedLicense, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SPDX-License-Identifier",
            await File.ReadAllTextAsync(Path.Combine(result.OutputDirectory, "DevicePlugin.cs")),
            StringComparison.Ordinal);

        ProcessStartInfo startInfo = new()
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            WorkingDirectory = result.OutputDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment =
            {
                ["DOTNET_CLI_HOME"] = temporary.GetPath("dotnet-home"),
                ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
                ["DOTNET_NOLOGO"] = "1",
                // Without this the SDK's first run in a fresh CLI home appends
                // "<home>\.dotnet\tools" to the USER's persisted PATH — not just this child process's.
                // DOTNET_SKIP_FIRST_TIME_EXPERIENCE stopped suppressing that in .NET 6, so every run of
                // this test left one more dead temp path behind: 55 of them had accumulated on the
                // development machine, taking PATH past 6.8 KB and breaking VsDevCmd.bat, which is what
                // both build.ps1 and eng\verify.ps1 use to export-check the Steam Input gate.
                ["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false",
                ["NUGET_PACKAGES"] = temporary.GetPath("nuget-packages")
            },
            ArgumentList =
            {
                "build",
                projectPath,
                "--configuration",
                "Release",
                "--runtime",
                "win-x64",
                "--no-self-contained",
                "--disable-build-servers",
                "--nologo",
                "--verbosity",
                "quiet",
                "--property:RestoreIgnoreFailedSources=true",
                "--property:NuGetAudit=false"
            }
        };

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("The .NET SDK process did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(1));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            const string message = "The generated plugin project did not build within one minute.";
            if (process.HasExited)
            {
                throw new TimeoutException(message);
            }
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw new TimeoutException(message);
        }

        var diagnostic = await output + Environment.NewLine + await error;
        if (process.ExitCode != 0)
        {
            Assert.Fail(diagnostic);
        }
        var buildOutput = Path.Combine(
            result.OutputDirectory,
            "bin",
            "Release",
            "net10.0-windows",
            "win-x64");
        Assert.True(File.Exists(Path.Combine(buildOutput, $"{result.RootNamespace}.dll")), diagnostic);
        Assert.True(File.Exists(Path.Combine(buildOutput, "LICENSE.txt")), diagnostic);
        Assert.False(File.Exists(Path.Combine(buildOutput, "WSGM.Device.Sdk.dll")));
        var validation = PluginPackageWorkflow.ValidateOffline(buildOutput);
        Assert.True(
            validation.Valid,
            string.Join("; ", validation.Issues.Select(issue => $"{issue.Path}: {issue.Message}")));

        AssemblyLoadContext loader = new("scaffold-command-test", isCollectible: true);
        try
        {
            await using var assemblyBytes = File.OpenRead(Path.Combine(buildOutput, $"{result.RootNamespace}.dll"));
            var assembly = loader.LoadFromStream(assemblyBytes);
            await using var plugin = Assert.IsType<IDevicePlugin>(
                Activator.CreateInstance(assembly.GetType($"{result.RootNamespace}.DevicePlugin", throwOnError: true)!), exactMatch: false);
            var detection = await plugin.DetectAsync(new PluginDetectionContext
            {
                Identity = new DeviceIdentitySnapshot
                {
                    SystemManufacturer = result.Identity.SystemManufacturer,
                    BaseboardProduct = result.Identity.BaseboardProduct,
                    SystemSku = result.Identity.SystemSku,
                    BiosVersion = result.Identity.BiosVersion,
                    UsbEndpoints = [new UsbEndpointObservation
                    {
                        VendorId = result.Identity.UsbVendorId,
                        ProductId = result.Identity.UsbProductId,
                        DeviceRelease = result.Identity.UsbDeviceRelease
                    }]
                }
            }, CancellationToken.None);
            Assert.True(detection.Matched);
            await plugin.StartAsync(new PluginStartContext
            {
                Host = new TestPluginHostAdapter(1),
                CycleGeneration = 1,
                DeviceDefinitionId = detection.DeviceDefinitionId!,
                StateDirectory = temporary.GetPath("test-state"),
                ControllerManagementEnabled = false
            }, CancellationToken.None);
            var commandResult = await plugin.ExecuteCommandAsync(new CapabilityCommand
            {
                CommandId = Guid.NewGuid(),
                CapabilityId = "example.integration-toggle",
                RequestedValue = CapabilityValue.Boolean(true),
                ExpectedCycleGeneration = 1,
                ExpectedDescriptorGeneration = 1,
                Deadline = DateTimeOffset.UtcNow.AddSeconds(2)
            }, CancellationToken.None);

            Assert.Equal(CommandOutcome.AppliedUnverified, commandResult.Outcome);
            Assert.Null(commandResult.ReadbackValue);
        }
        finally
        {
            loader.Unload();
        }
    }

    [Fact]
    public void Scaffold_PreCancelledRequestPublishesNoPartialDirectory()
    {
        using TemporaryDirectory temporary = new();
        var capturePath = temporary.GetPath("source.wsgmcap");
        using (FileStream capture = new(capturePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            CaptureBundleWriter.Write(capture, Capture());
        }
        var output = temporary.GetPath("cancelled-scaffold");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        _ = Assert.Throws<OperationCanceledException>(() => ScaffoldFromCaptureWorkflow.Run(
            capturePath,
            output,
            new DeviceLabPathBoundaries
            {
                LiveDataDirectory = temporary.GetPath("never-live-wsgm"),
                BroadHomeDirectories = []
            },
            cancellation.Token));

        Assert.False(Directory.Exists(output));
        Assert.Empty(Directory.EnumerateDirectories(temporary.Root, ".cancelled-scaffold.*.tmp"));
    }

    [Fact]
    public void Scaffold_MultipleExactUsbEndpointsRequireAnExplicitInstance()
    {
        using TemporaryDirectory temporary = new();
        var original = Capture();
        var multiple = original with
        {
            Inventory = original.Inventory with
            {
                UsbInterfaces =
                [
                    original.Inventory.UsbInterfaces[0] with { InstanceId = "usb-left" },
                    original.Inventory.UsbInterfaces[0] with { InstanceId = "usb-right" }
                ]
            }
        };
        var capturePath = temporary.GetPath("multiple.wsgmcap");
        using (FileStream capture = new(capturePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            CaptureBundleWriter.Write(capture, multiple);
        }
        DeviceLabPathBoundaries boundaries = new()
        {
            LiveDataDirectory = temporary.GetPath("never-live-wsgm"),
            BroadHomeDirectories = []
        };

        var ambiguous = Assert.Throws<InvalidDataException>(() =>
            ScaffoldFromCaptureWorkflow.Run(
                capturePath,
                temporary.GetPath("ambiguous"),
                boundaries));
        var selected = ScaffoldFromCaptureWorkflow.Run(
            capturePath,
            temporary.GetPath("selected"),
            boundaries,
            usbInstanceId: "usb-right");

        Assert.Contains("Select one exact instance ID", ambiguous.Message, StringComparison.Ordinal);
        Assert.Equal("CAFE", selected.Identity.UsbVendorId);
        Assert.True(Directory.Exists(selected.OutputDirectory));
    }

    [Fact]
    public void MinimalTemplate_DemonstratesPartialStateCanonicalIoCancellationDiagnosticsAndRestore()
    {
        var assembly = typeof(ScaffoldFromCaptureWorkflow).Assembly;
        using var stream = Assert.IsType<Stream>(assembly.GetManifestResourceStream(
            "WSGM.DeviceLab.Templates.MinimalPlugin.DevicePlugin.cs.template"), exactMatch: false);
        using StreamReader reader = new(stream);
        var template = reader.ReadToEnd();

        Assert.Contains("Available = false", template, StringComparison.Ordinal);
        Assert.Contains("PluginOperationalState.Degraded", template, StringComparison.Ordinal);
        Assert.Contains("cancellationToken.ThrowIfCancellationRequested()", template, StringComparison.Ordinal);
        Assert.Contains("PublishControllerSampleAsync", template, StringComparison.Ordinal);
        Assert.Contains("ApplyHapticOutputAsync", template, StringComparison.Ordinal);
        Assert.Contains("GetDiagnosticsAsync", template, StringComparison.Ordinal);
        Assert.Contains("_exampleValue = _capturedExampleValue", template, StringComparison.Ordinal);
    }

    private static SanitizedCaptureBundle Capture()
    {
        var timestamp = DateTimeOffset.UnixEpoch;
        return new SanitizedCaptureBundle
        {
            Manifest = new ShareableCaptureManifest
            {
                SchemaVersion = CaptureSchema.CurrentVersion,
                BundleId = "scaffold-test",
                ToolVersion = "test-1",
                StartedAt = timestamp,
                CompletedAt = timestamp,
                QpcFrequency = 1
            },
            Recipe = new ObserveOnlyRecipe
            {
                SchemaVersion = CaptureSchema.CurrentVersion,
                RecipeId = "scaffold-test",
                DisplayName = "Scaffold test"
            },
            Inventory = new MachineInventory
            {
                SchemaVersion = 1,
                Firmware = new FirmwareInventory
                {
                    SystemManufacturer = "Contoso Devices",
                    BaseboardProduct = "BOARD-X1",
                    SystemSku = "BOARD-X1-SKU",
                    BiosVersion = "1.0.0"
                },
                UsbInterfaces =
                [
                    new UsbInterfaceInventory
                    {
                        InstanceId = "redacted-instance",
                        VendorId = "CAFE",
                        ProductId = "BEEF",
                        DeviceRelease = "0100",
                        Present = true
                    }
                ],
                CapturedAt = timestamp
            },
            Redaction = new CaptureRedactionManifest
            {
                SchemaVersion = CaptureSchema.CurrentVersion,
                DefaultRedactionApplied = true
            }
        };
    }
}
