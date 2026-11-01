using WSGM.Shell;

namespace WSGM.Tests.Shell;

/// <summary>
///     What a deliberate eject means once the card is still sitting in the reader.
/// </summary>
/// <remarks>
///     A media-level eject does not remove the card, and Windows remounts it within seconds. The
///     mounted volume then looks exactly like a fresh insert, which is why the reconcile used to put
///     back the registration the user had just ejected.
/// </remarks>
public class LibraryEjectIntentTests
{
    [Fact]
    public void AnEjectedLibraryIsHeldOutOfSteamsListWhileTheCardStaysInTheReader()
    {
        var policy = new LibraryPolicy();
        policy.NoteEjected([@"D:\SteamLibrary"], "777");

        Assert.True(policy.IsHeldEjected(@"D:\SteamLibrary", "777"));
    }

    [Fact]
    public void ADifferentCardInTheSameSlotIsNotCoveredByTheEject()
    {
        var policy = new LibraryPolicy();
        policy.NoteEjected([@"D:\SteamLibrary"], "777");

        // The intent was about the card that left, so the one that arrived registers normally.
        Assert.False(policy.IsHeldEjected(@"D:\SteamLibrary", "222"));

        // And it is dropped rather than left to suppress the original card on a later insert.
        Assert.False(policy.IsHeldEjected(@"D:\SteamLibrary", "777"));
    }

    [Fact]
    public void EjectingAVolumeCarryingNoLibraryHoldsNothingBack()
    {
        var policy = new LibraryPolicy();
        policy.NoteEjected([@"D:\SteamLibrary"], "");

        // A blank card that later gains a library must be able to register it.
        Assert.False(policy.IsHeldEjected(@"D:\SteamLibrary", "777"));
    }

    [Fact]
    public void TheMediaActuallyLeavingServesTheIntent()
    {
        var policy = new LibraryPolicy();
        policy.NoteEjected([@"D:\SteamLibrary"], "777");

        policy.ClearEjected(@"D:\SteamLibrary");

        // Reinserting the same card is an ordinary insert.
        Assert.False(policy.IsHeldEjected(@"D:\SteamLibrary", "777"));
    }

    [Fact]
    public void AnEjectOnOneVolumeDoesNotHoldBackAnother()
    {
        var policy = new LibraryPolicy();
        policy.NoteEjected([@"D:\SteamLibrary"], "777");

        Assert.False(policy.IsHeldEjected(@"E:\SteamLibrary", "777"));
    }
}
