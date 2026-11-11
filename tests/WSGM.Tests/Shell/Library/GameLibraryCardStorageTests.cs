using WSGM.Core;
using WSGM.Shell;
using WSGM.Testing;

namespace WSGM.Tests.Shell.Library;

/// <summary>Import uses the current card's manifest, not an older card's volume locator.</summary>
public sealed class GameLibraryCardStorageTests
{
    private const string Volume = @"\\?\Volume{1bde88e7-d29a-11f1-8473-84d1c1f3b6a4}\";
    private const string OldId = "8f3d17fa-ae3a-4de2-ab50-661116a2aa49";

    [Fact]
    public void AFirstImportStartsANewLibraryDespiteAnOlderBindingAtTheSameVolume()
    {
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("library-import.json");
        var store = new ImportStateStore(path);
        var old = new StorageLibraryRecord
        {
            Id = OldId, Name = "Old card", VolumeId = Volume, ManifestRevision = 10, RepairRequired = true
        };
        store.SaveLibrary(old);
        store.SetActiveAccount(7);
        store.Save(new ImportedEntry
        {
            AccountId = 7, Source = "rom", Key = "old", Name = "Old title", AppId = 42,
            Mode = nameof(ImportMode.SteamIntegration),
            Content = new ManagedContentRecord
            {
                Id = "old-content", LibraryId = OldId,
                BackingPath = new ManagedContentPath { VolumeId = Volume }
            }
        });

        var first = GameLibraryCardStorage.PrepareImport(new ManagedContentRecord(), Volume, "New card",
            store.Libraries(), current: null);

        Assert.NotEqual(OldId, first.Manifest.Id);
        Assert.True(Guid.TryParseExact(first.Manifest.Id, "D", out _));
        Assert.Equal("New card", first.Manifest.Name);
        Assert.Empty(first.Manifest.Entries);
        Assert.Null(first.Library);
        Assert.Null(first.Revision);
        store.SaveLibrary(new StorageLibraryRecord
        {
            Id = first.Manifest.Id, Name = first.Manifest.Name, VolumeId = Volume, ManifestRevision = 1
        });

        var reloaded = new ImportStateStore(path);
        reloaded.SetActiveAccount(7);
        Assert.Equal(2, reloaded.Libraries().Count);
        Assert.Equal(10, reloaded.Libraries().Single(library => library.Id == OldId).ManifestRevision);
        var retained = Assert.Single(reloaded.Entries());
        Assert.Equal(42u, retained.AppId);
        Assert.Equal(OldId, retained.Content!.LibraryId);

        // The next title reads the newly created card manifest. The older binding must not win.
        var second = GameLibraryCardStorage.PrepareImport(new ManagedContentRecord(), Volume, "New card",
            reloaded.Libraries(), first.Manifest);
        Assert.Equal(first.Manifest.Id, second.Library!.Id);
        Assert.Same(first.Manifest, second.Manifest);
        Assert.Equal(1L, second.Revision);
    }

    [Fact]
    public void AnAlreadyBoundTitleDoesNotSilentlyReplaceItsLostLibrary()
    {
        var error = Assert.Throws<InvalidDataException>(() => GameLibraryCardStorage.PrepareImport(
            new ManagedContentRecord { LibraryId = OldId }, Volume, "Card", [], current: null));

        Assert.Contains("Repair library", error.Message);
    }

    [Fact]
    public void AnExistingForeignManifestStillRequiresAcceptance()
    {
        var manifest = new StorageLibraryManifest { Id = Guid.NewGuid().ToString("D"), Name = "Other card" };
        var olderBinding = new StorageLibraryRecord { Id = OldId, VolumeId = Volume };

        var error = Assert.Throws<InvalidDataException>(() => GameLibraryCardStorage.PrepareImport(
            new ManagedContentRecord(), Volume, "Card", [olderBinding], manifest));

        Assert.Contains("Use this card", error.Message);
        Assert.Empty(manifest.Entries);
    }

    [Fact]
    public void AKnownLibraryOnAnotherVolumeStillRequiresAcceptance()
    {
        var manifest = new StorageLibraryManifest { Id = OldId };
        var binding = new StorageLibraryRecord
        {
            Id = OldId, VolumeId = @"\\?\Volume{00000000-0000-4000-8000-000000000001}\"
        };

        Assert.Throws<InvalidDataException>(() => GameLibraryCardStorage.PrepareImport(
            new ManagedContentRecord(), Volume, "Card", [binding], manifest));
    }

    [Fact]
    public void ASwappedCardDoesNotReceiveATitleBoundToAnotherLibrary()
    {
        var manifest = new StorageLibraryManifest { Id = Guid.NewGuid().ToString("D") };
        var binding = new StorageLibraryRecord { Id = manifest.Id, VolumeId = Volume };

        var error = Assert.Throws<InvalidDataException>(() => GameLibraryCardStorage.PrepareImport(
            new ManagedContentRecord { LibraryId = OldId }, Volume, "Card", [binding], manifest));

        Assert.Contains("different library", error.Message);
    }
}
