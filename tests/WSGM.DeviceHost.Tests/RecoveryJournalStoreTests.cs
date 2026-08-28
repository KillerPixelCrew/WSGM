using WSGM.Device.Contracts.Capabilities;
using WSGM.Device.Contracts.Lifecycle;

namespace WSGM.DeviceHost.Tests;

public sealed class RecoveryJournalStoreTests
{
    [Fact]
    public async Task PlannedEntryIsDurableBeforeApplyingAndOutstandingAfterWrite()
    {
        using TemporaryDirectory temporary = new();
        RecoveryJournalEntry planned = Entry(JournalEntryStatus.Planned);
        await using (RecoveryJournalStore store = RecoveryJournalStore.Open("test.package", temporary.Path))
        {
            await store.PersistAsync(planned, 2, 3, CancellationToken.None);
            await store.PersistAsync(planned with { Status = JournalEntryStatus.Applying },
                2, 3, CancellationToken.None);
            await store.PersistAsync(planned with
            {
                Status = JournalEntryStatus.AppliedVerified,
                AppliedValue = Value(18),
            }, 2, 3, CancellationToken.None);
        }

        await using RecoveryJournalStore reopened = RecoveryJournalStore.Open(
            "test.package",
            temporary.Path);
        RecoveryJournalEntry outstanding = Assert.Single(reopened.Outstanding);
        Assert.Equal(JournalEntryStatus.AppliedVerified, outstanding.Status);
        Assert.Equal(18, outstanding.AppliedValue?.IntegerValue);
    }

    [Fact]
    public async Task ReplacingAnEntryCannotChangeItsCapturedOriginalValue()
    {
        using TemporaryDirectory temporary = new();
        RecoveryJournalEntry planned = Entry(JournalEntryStatus.Planned);
        await using RecoveryJournalStore store = RecoveryJournalStore.Open("test.package", temporary.Path);
        await store.PersistAsync(planned, 2, 3, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await store.PersistAsync(
            planned with
            {
                OriginalValue = Value(99),
                Status = JournalEntryStatus.Applying,
            }, 2, 3, CancellationToken.None));
    }

    [Fact]
    public async Task CorruptJournalIsMovedAsideAndBlocksActivationSignal()
    {
        using TemporaryDirectory temporary = new();
        await using (RecoveryJournalStore store = RecoveryJournalStore.Open("test.package", temporary.Path))
        {
            await store.PersistAsync(Entry(JournalEntryStatus.Planned), 2, 3,
                CancellationToken.None);
        }

        string journal = Assert.Single(Directory.GetFiles(temporary.Path, "*.journal.json"));
        await File.WriteAllTextAsync(journal, "{not-json");

        await using RecoveryJournalStore reopened = RecoveryJournalStore.Open(
            "test.package",
            temporary.Path);
        Assert.True(reopened.CorruptionQuarantined);
        Assert.Empty(reopened.Outstanding);
        Assert.Single(Directory.GetFiles(temporary.Path, "*.corrupt-*"));
    }

    private static RecoveryJournalEntry Entry(JournalEntryStatus status) => new()
    {
        Sequence = 10,
        PackageId = "test.package",
        DeviceId = "device.fixture",
        HostGeneration = 2,
        DeviceGeneration = 3,
        ResourceId = "power",
        CapabilityId = "power.primary-limit",
        FirmwareIdentity = "fixture-1",
        OriginalValue = Value(15),
        PlannedValue = Value(18),
        Status = status,
        OpenedAt = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero),
    };

    private static CapabilityValue Value(int value) => new()
    {
        Kind = CapabilityValueKind.Integer,
        IntegerValue = value,
    };

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"wsgm-journal-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
