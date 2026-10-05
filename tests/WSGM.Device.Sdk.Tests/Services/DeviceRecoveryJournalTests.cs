using System.Text.Json.Serialization;
using WSGM.Device.Sdk.Services;
using WSGM.Testing;

namespace WSGM.Device.Sdk.Tests.Services;

/// <summary>The shared recovery record both first-party packages keep their captured originals in.</summary>
public sealed class DeviceRecoveryJournalTests
{
    [Fact]
    public async Task AnAbsentRecordIsHealthyAndCanCaptureAnOriginal()
    {
        using TemporaryDirectory state = new();
        var journal = await TestJournal.OpenAsync(state.Root);

        Assert.Null(journal.FailureReason);
        Assert.Empty(journal.OutstandingEntries);
        var operation = await journal.BeginAsync("power", "fw-1", new TestState { Value = "original" },
            CancellationToken.None);
        Assert.True(operation.Opened);
    }

    [Fact]
    public async Task ADirectoryAtTheRecordPathIsBlockedAndPreserved()
    {
        using TemporaryDirectory state = new();
        var path = state.GetPath("temporary-state.v1.json");
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "marker"), "preserve");

        var journal = await TestJournal.OpenAsync(state.Root);

        Assert.NotNull(journal.FailureReason);
        Assert.Equal("blocked", journal.DiagnosticState);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await journal.BeginAsync("power", "fw-1", new TestState { Value = "original" }, CancellationToken.None));
        Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Combine(path, "marker")));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData(
        "{\"version\":1,\"entries\":[{\"serviceId\":\"power\",\"firmwareIdentity\":\"fw-1\",\"originalState\":{\"value\":\"original\"},\"status\":999}]}")]
    public async Task InvalidRecordsAreBlockedAndKeptByteIdentical(string contents)
    {
        using TemporaryDirectory state = new();
        var path = state.GetPath("temporary-state.v1.json");
        await File.WriteAllTextAsync(path, contents);
        var bytes = await File.ReadAllBytesAsync(path);

        var journal = await TestJournal.OpenAsync(state.Root);

        Assert.NotNull(journal.FailureReason);
        Assert.Empty(journal.OutstandingEntries);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await journal.BeginAsync("power", "fw-1", new TestState { Value = "replacement" }, CancellationToken.None));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task AnUnreadableRecordIsNotMistakenForAnAbsentOne()
    {
        using TemporaryDirectory state = new();
        var path = state.GetPath("temporary-state.v1.json");
        await File.WriteAllTextAsync(path, "{\"version\":1,\"entries\":[]}");
        var bytes = await File.ReadAllBytesAsync(path);
        await using (FileStream locked = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var journal = await TestJournal.OpenAsync(state.Root);
            Assert.NotNull(journal.FailureReason);
        }

        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UndefinedStatusesAreRejectedBeforeWritingEvenWithoutAnEntry(bool hasEntry)
    {
        using TemporaryDirectory state = new();
        var path = state.GetPath("temporary-state.v1.json");
        var journal = await TestJournal.OpenAsync(state.Root);
        if (hasEntry)
        {
            await journal.BeginAsync("power", "fw-1", new TestState { Value = "original" }, CancellationToken.None);
        }

        var bytes = hasEntry ? await File.ReadAllBytesAsync(path) : null;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await journal.SetStatusAsync("power", (DeviceRecoveryStatus)999, CancellationToken.None));

        if (hasEntry)
        {
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        else
        {
            Assert.False(File.Exists(path));
        }
    }

    [Fact]
    public async Task AFailedSaveRefusesOnlyThatMutationAndRecoversAfterTheFileLockIsReleased()
    {
        using TemporaryDirectory state = new();
        var journal = await TestJournal.OpenAsync(state.Root);
        await journal.BeginAsync("power", "fw-1", new TestState { Value = "original" }, CancellationToken.None);
        var path = state.GetPath("temporary-state.v1.json");
        await using (FileStream locked = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var failure = await Record.ExceptionAsync(async () =>
                await journal.SetStatusAsync("power", DeviceRecoveryStatus.RestoredVerified, CancellationToken.None));
            Assert.True(failure is IOException or UnauthorizedAccessException);
            Assert.NotNull(journal.EntryFor("power"));
            Assert.Null(journal.FailureReason);
        }

        await journal.SetStatusAsync("power", DeviceRecoveryStatus.RestoredVerified, CancellationToken.None);
        Assert.Empty(journal.OutstandingEntries);
        var reopened = await TestJournal.OpenAsync(state.Root);
        Assert.Empty(reopened.OutstandingEntries);
        Assert.Null(reopened.FailureReason);
    }

    [Fact]
    public async Task AWriteQueuedBehindAnInFlightWriteCompletes()
    {
        using TemporaryDirectory state = new();
        var journal = await TestJournal.OpenAsync(state.Root);
        using ManualResetEventSlim release = new();
        TaskCompletionSource serializing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var validations = 0;
        journal.BeforeValidation = () =>
        {
            if (Interlocked.Increment(ref validations) != 2)
            {
                return;
            }

            serializing.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(3)));
        };
        var write = Task.Run(async () => await journal.BeginAsync("power", "fw-1",
            new TestState { Value = "original" }, CancellationToken.None));
        try
        {
            await serializing.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var queued = journal.SetStatusAsync("power", DeviceRecoveryStatus.RestoredVerified,
                CancellationToken.None).AsTask();
            Assert.False(queued.IsCompleted);
            release.Set();
            await write;
            await queued;
            Assert.Empty(journal.OutstandingEntries);
        }
        finally
        {
            release.Set();
            await write;
        }
    }

    [Fact]
    public async Task AnOpenedEntrySurvivesAReopenAndAVerifiedRestoreRemovesIt()
    {
        using TemporaryDirectory state = new();
        {
            var journal = await TestJournal.OpenAsync(state.Root);
            var operation = await journal.BeginAsync("power", "fw-1", new TestState { Value = "original" },
                CancellationToken.None);
            Assert.True(operation.Opened);
        }

        {
            var reopened = await TestJournal.OpenAsync(state.Root);
            Assert.Null(reopened.FailureReason);
            Assert.Equal("original", reopened.OriginalStateFor("power")?.Value);
            await reopened.SetStatusAsync("power", DeviceRecoveryStatus.RestoredVerified, CancellationToken.None);
        }

        var restored = await TestJournal.OpenAsync(state.Root);
        Assert.Empty(restored.OutstandingEntries);
    }

    [Fact]
    public async Task ARecordIsNotRefusedForItsSize()
    {
        // The record holds whatever a package captured; a size cap would block the very service whose
        // original it failed to keep.
        using TemporaryDirectory state = new();
        var large = new string('x', 64 * 1024);
        {
            var journal = await TestJournal.OpenAsync(state.Root);
            _ = await journal.BeginAsync("fans", "fw-1", new TestState { Value = large }, CancellationToken.None);
            Assert.Null(journal.FailureReason);
        }

        var reopened = await TestJournal.OpenAsync(state.Root);
        Assert.Null(reopened.FailureReason);
        Assert.Equal(large, reopened.OriginalStateFor("fans")?.Value);
    }

    [Fact]
    public async Task AnUnresolvedRestoreIsSetPendingAgainWithItsFirstOriginal()
    {
        using TemporaryDirectory state = new();
        var journal = await TestJournal.OpenAsync(state.Root);
        _ = await journal.BeginAsync("power", "fw-1", new TestState { Value = "original" }, CancellationToken.None);
        await journal.SetStatusAsync("power", DeviceRecoveryStatus.RestoreFailed, CancellationToken.None);
        Assert.Null(journal.PendingOriginalFor("power"));

        var operation = await journal.BeginAsync("power", "fw-1", new TestState { Value = "newer" },
            CancellationToken.None);

        Assert.False(operation.Opened);
        Assert.Equal(DeviceRecoveryStatus.Pending, journal.EntryFor("power")?.Status);
        Assert.Equal("original", journal.PendingOriginalFor("power")?.Value);
    }

    [Fact]
    public async Task AnEntryBoundToOtherFirmwareIsReplacedByTheFreshCapture()
    {
        using TemporaryDirectory state = new();
        var journal = await TestJournal.OpenAsync(state.Root);
        _ = await journal.BeginAsync("power", "fw-1", new TestState { Value = "original" }, CancellationToken.None);

        var operation = await journal.BeginAsync("power", "fw-2", new TestState { Value = "newer" },
            CancellationToken.None);

        Assert.True(operation.Opened);
        Assert.Equal("fw-2", journal.EntryFor("power")?.FirmwareIdentity);
        Assert.Equal("newer", journal.PendingOriginalFor("power")?.Value);
    }

    internal sealed record TestState
    {
        public required string Value { get; init; }
    }

    private sealed class TestJournal() : DeviceRecoveryJournal<TestState>(TestJournalJsonContext.Default.Document)
    {
        public Action? BeforeValidation { get; set; }

        public static async Task<TestJournal> OpenAsync(string stateDirectory)
        {
            var journal = new TestJournal();
            await journal.LoadAsync(stateDirectory, CancellationToken.None);
            return journal;
        }

        protected override void ValidateEntry(DeviceRecoveryEntry<TestState> entry)
        {
            BeforeValidation?.Invoke();
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DeviceRecoveryDocument<DeviceRecoveryJournalTests.TestState>),
    TypeInfoPropertyName = "Document")]
internal sealed partial class TestJournalJsonContext : JsonSerializerContext;
