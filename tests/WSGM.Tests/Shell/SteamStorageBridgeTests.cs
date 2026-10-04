using WSGM.Shell;
using WSGM.Tests.Fakes;

namespace WSGM.Tests.Shell;

/// <summary>
///     The rules the Steam storage bridge enforces before anything reaches a storage manager.
/// </summary>
/// <remarks>
///     The managers themselves are not exercised here: they own disks, and this project never touches
///     real storage. What is asserted is the part that belongs to the bridge — when a drive list is
///     published at all, and that every refusal carries a reason rather than being a control that
///     silently does nothing.
/// </remarks>
public sealed class SteamStorageBridgeTests
{
    [Fact]
    public void NothingIsPublishedBeforeTheFirstScanAndEmptyIsPublishedAfterIt()
    {
        using var config = new TemporaryConfigStore();
        using var drives = new RemovableDriveManager();
        using var bridge = new SteamStorageBridge(drives, new SdFormatManager(config.Store), () => false);

        // Before any enumeration, silence: an empty answer here would tell Steam "no drives" to
        // someone holding a card the first scan has not reached yet.
        Assert.Null(bridge.ReadState());

        // After one, an empty list is a real answer and has to reach Steam, or a card pulled from
        // the reader -- or ejected from Windows rather than from Steam -- stays on its page.
        drives.Apply([]);
        var state = bridge.ReadState();

        Assert.NotNull(state);
        Assert.Empty(state.Drives);
        Assert.Empty(state.BlockDevices);
        Assert.False(state.UnmountSupported);
    }
}
