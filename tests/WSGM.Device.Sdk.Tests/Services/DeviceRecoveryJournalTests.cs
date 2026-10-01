using System.Text.Json.Serialization;
using WSGM.Device.Sdk.Services;
using WSGM.Testing;

namespace WSGM.Device.Sdk.Tests.Services;

/// <summary>The shared recovery record both first-party packages keep their captured originals in.</summary>
public sealed class DeviceRecoveryJournalTests
{
    [Fact]
    public async Task AnOpenedEntrySurvivesAReopenAndAVerifiedRestoreRemovesIt()
    {
        using TemporaryDirectory state = new();
        await using (var journal = await TestJournal.OpenAsync(state.Root))
        {
            var operation = await journal.BeginAsync("power", "fw-1", new TestState { Value = "original" },
                CancellationToken.None);
            Assert.True(operation.Opened);
        }

        await using (var reopened = await TestJournal.OpenAsync(state.Root))
        {
            Assert.Null(reopened.FailureReason);
            Assert.Equal("original", reopened.OriginalStateFor("power")?.Value);
            await reopened.SetStatusAsync("power", DeviceRecoveryStatus.RestoredVerified, CancellationToken.None);
        }

        await using var restored = await TestJournal.OpenAsync(state.Root);
        Assert.Empty(restored.OutstandingEntries);
    }

    [Fact]
    public async Task ARecordIsNotRefusedForItsSize()
    {
        // The record holds whatever a package captured; a size cap would block the very service whose
        // original it failed to keep.
        using TemporaryDirectory state = new();
        var large = new string('x', 64 * 1024);
        await using (var journal = await TestJournal.OpenAsync(state.Root))
        {
            _ = await journal.BeginAsync("fans", "fw-1", new TestState { Value = large }, CancellationToken.None);
            Assert.Null(journal.FailureReason);
        }

        await using var reopened = await TestJournal.OpenAsync(state.Root);
        Assert.Null(reopened.FailureReason);
        Assert.Equal(large, reopened.OriginalStateFor("fans")?.Value);
    }

    [Fact]
    public async Task AnUnresolvedRestoreRefusesANewEntryForTheSameService()
    {
        using TemporaryDirectory state = new();
        await using var journal = await TestJournal.OpenAsync(state.Root);
        _ = await journal.BeginAsync("power", "fw-1", new TestState { Value = "original" }, CancellationToken.None);
        await journal.SetStatusAsync("power", DeviceRecoveryStatus.RestoreFailed, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await journal.BeginAsync("power", "fw-1", new TestState { Value = "newer" }, CancellationToken.None));
        Assert.Equal("original", journal.OriginalStateFor("power")?.Value);
    }

    internal sealed record TestState
    {
        public required string Value { get; init; }
    }

    private sealed class TestJournal() : DeviceRecoveryJournal<TestState>(TestJournalJsonContext.Default.Document)
    {
        public static async Task<TestJournal> OpenAsync(string stateDirectory)
        {
            var journal = new TestJournal();
            await journal.LoadAsync(stateDirectory, CancellationToken.None);
            return journal;
        }

        protected override void ValidateEntry(DeviceRecoveryEntry<TestState> entry)
        {
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DeviceRecoveryDocument<DeviceRecoveryJournalTests.TestState>),
    TypeInfoPropertyName = "Document")]
internal sealed partial class TestJournalJsonContext : JsonSerializerContext;
