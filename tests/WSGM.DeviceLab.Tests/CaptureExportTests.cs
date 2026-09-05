using WSGM.DeviceLab.Capture;
using WSGM.DeviceLab.Inventory;

namespace WSGM.Device.Tests;

public sealed class CaptureExportTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ExportReportsCleanupFailureAndPreservesTheOriginalFailure(bool cancelled, bool locked)
    {
        using TemporaryDirectory directory = new();
        using CancellationTokenSource cancellation = new();
        CaptureExportPlan plan = Plan(directory);
        FileStream? heldFile = null;
        string? temporaryPath = null;
        try
        {
            void FailPublication(string staged, string destination)
            {
                temporaryPath = staged;
                Assert.Equal(plan.ShareableOutputPath, destination);
                Assert.NotEmpty(File.ReadAllBytes(staged));
                if (locked)
                {
                    heldFile = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
                if (cancelled)
                {
                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                }
                throw new IOException("Publication failed.");
            }

            if (cancelled && !locked)
            {
                OperationCanceledException failure = Assert.Throws<OperationCanceledException>(() =>
                    ObserveOnlyCaptureWorkflow.Export(plan, true, null, FailPublication, cancellation.Token));
                Assert.Equal(cancellation.Token, failure.CancellationToken);
            }
            else
            {
                CaptureExportResult result = ObserveOnlyCaptureWorkflow.Export(
                    plan, true, null, FailPublication, cancellation.Token);
                Assert.False(result.Exported);
                Assert.Contains(cancelled ? "Export cancelled." : "Publication failed.", result.Error);
                if (locked)
                {
                    Assert.Contains("cleanup failed", result.Error);
                    Assert.Contains(Assert.IsType<string>(temporaryPath), result.Error);
                }
            }

            Assert.NotNull(temporaryPath);
            Assert.Equal(locked, File.Exists(temporaryPath));
            Assert.False(File.Exists(plan.ShareableOutputPath));
            Assert.Equal(locked ? 1 : 0, Directory.EnumerateFiles(directory.Root, "*.tmp").Count());
        }
        finally
        {
            heldFile?.Dispose();
        }
    }

    [Fact]
    public void ExportPublishesAReadableBundleAndRefusesToOverwriteIt()
    {
        using TemporaryDirectory directory = new();
        CaptureExportPlan plan = Plan(directory);

        CaptureExportResult first = ObserveOnlyCaptureWorkflow.Export(plan, true, null);

        Assert.True(first.Exported, first.Error);
        byte[] original = File.ReadAllBytes(plan.ShareableOutputPath);
        using MemoryStream archive = new(original);
        Assert.True(CaptureBundleReader.Read(archive).Succeeded);
        CaptureExportResult repeated = ObserveOnlyCaptureWorkflow.Export(plan, true, null);
        Assert.False(repeated.Exported);
        Assert.Equal(original, File.ReadAllBytes(plan.ShareableOutputPath));
        Assert.Empty(Directory.EnumerateFiles(directory.Root, "*.tmp"));
    }

    [Fact]
    public void UnconfirmedExportDoesNotCreateAFile()
    {
        using TemporaryDirectory directory = new();

        Assert.False(ObserveOnlyCaptureWorkflow.Export(Plan(directory), false, null).Exported);

        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Root));
    }

    private static CaptureExportPlan Plan(TemporaryDirectory directory) => new()
    {
        PrivateWorkingDirectory = directory.GetPath("private"),
        ShareableOutputPath = directory.GetPath("capture.wsgmcap"),
        Bundle = new SanitizedCaptureBundle
        {
            Manifest = new ShareableCaptureManifest
            {
                SchemaVersion = CaptureSchema.CurrentVersion,
                BundleId = "export-test",
                ToolVersion = "test",
                StartedAt = DateTimeOffset.UnixEpoch,
                CompletedAt = DateTimeOffset.UnixEpoch,
                QpcFrequency = 1,
            },
            Recipe = new ObserveOnlyRecipe
            {
                SchemaVersion = CaptureSchema.CurrentVersion,
                RecipeId = "export-test",
                DisplayName = "Export test",
            },
            Inventory = new MachineInventory
            {
                SchemaVersion = 1,
                Firmware = new FirmwareInventory(),
                CapturedAt = DateTimeOffset.UnixEpoch,
            },
            Redaction = new CaptureRedactionManifest
            {
                SchemaVersion = CaptureSchema.CurrentVersion,
                DefaultRedactionApplied = true,
            },
        },
    };
}
