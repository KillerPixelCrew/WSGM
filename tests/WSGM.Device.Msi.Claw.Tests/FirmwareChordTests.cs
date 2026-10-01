using System.Runtime.InteropServices;

namespace WSGM.Device.Msi.Claw.Tests;

[Collection("plugin-trace")]
public sealed class FirmwareChordTests
{
    [Fact]
    public void SyntheticInput_MatchesTheWindowsX64Layout()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(24, Marshal.SizeOf<NativeKeyboard.KeyboardInput>());
        Assert.Equal(32, Marshal.SizeOf<NativeKeyboard.InputUnion>());
        Assert.Equal(40, Marshal.SizeOf<NativeKeyboard.Input>());
        Assert.Equal(8, Marshal.OffsetOf<NativeKeyboard.Input>(nameof(NativeKeyboard.Input.Data)));
        Assert.Equal(16,
            Marshal.OffsetOf<NativeKeyboard.KeyboardInput>(nameof(NativeKeyboard.KeyboardInput.ExtraInfo)));

        const uint marker = 0x5753474D;
        var release = NativeKeyboard.KeyInput(NativeKeyboard.VK_LWIN, true, marker);
        Assert.Equal(NativeKeyboard.INPUT_KEYBOARD, release.Type);
        Assert.Equal(NativeKeyboard.VK_LWIN, release.Data.Keyboard.VirtualKey);
        Assert.Equal(NativeKeyboard.KEYEVENTF_KEYUP | NativeKeyboard.KEYEVENTF_EXTENDEDKEY,
            release.Data.Keyboard.Flags);
        Assert.Equal(marker, release.Data.Keyboard.ExtraInfo);
    }

    [Theory]
    [InlineData(NativeKeyboard.VK_LWIN, true, 3u)]
    [InlineData(NativeKeyboard.VK_RWIN, true, 3u)]
    [InlineData(NativeKeyboard.VK_LWIN, false, 1u)]
    [InlineData(NativeKeyboard.VK_DUMMY, false, 0u)]
    [InlineData(NativeKeyboard.VK_DUMMY, true, 2u)]
    public void SyntheticInput_MarksWindowsKeysAsExtended(uint key, bool keyUp, uint flags)
    {
        Assert.Equal(flags, NativeKeyboard.KeyInput(key, keyUp, 0).Data.Keyboard.Flags);
    }

    [Theory]
    [InlineData(NativeKeyboard.VK_G)]
    [InlineData(NativeKeyboard.VK_TAB)]
    public void FirmwareBurst_SuppressesTheOrphanAndReleasesWindowsOnce(uint target)
    {
        FirmwareChordStateMachine state = new();
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, true, false));
        Assert.Equal(
            new ChordDecision(true, true, false,
                target == NativeKeyboard.VK_G ? FirmwareChord.QuickSettings : FirmwareChord.QuickSettingsLong),
            state.Observe(target, false, false));

        state.CommitSyntheticReleases(true, false);
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, false, true));
        Assert.Equal(new ChordDecision(true, false, false), state.Observe(target, false, false));
        Assert.True(state.Observe(NativeKeyboard.VK_LWIN, false, false).Suppress);
        Assert.Equal(default, state.Observe(target, false, false));

        // A later physical Windows press and release must pass through normally.
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, true, false));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, false, false));
    }

    [Theory]
    [InlineData(NativeKeyboard.VK_LWIN, NativeKeyboard.VK_G, false)]
    [InlineData(NativeKeyboard.VK_RWIN, NativeKeyboard.VK_G, true)]
    [InlineData(NativeKeyboard.VK_LWIN, NativeKeyboard.VK_TAB, true)]
    [InlineData(NativeKeyboard.VK_RWIN, NativeKeyboard.VK_TAB, false)]
    public void CompleteKeyboardChordIncludingRepeatsPasses(uint windowsKey, uint target, bool windowsUpFirst)
    {
        FirmwareChordStateMachine state = new();
        Assert.Equal(default, state.Observe(windowsKey, true, false));
        Assert.Equal(default, state.Observe(target, true, false));
        Assert.Equal(default, state.Observe(target, true, false));
        if (windowsUpFirst)
        {
            Assert.Equal(default, state.Observe(windowsKey, false, false));
        }

        Assert.Equal(default, state.Observe(target, false, false));
        if (!windowsUpFirst)
        {
            Assert.Equal(default, state.Observe(windowsKey, false, false));
        }
    }

    [Theory]
    [InlineData(NativeKeyboard.VK_G, (int)FirmwareChord.QuickSettings)]
    [InlineData(NativeKeyboard.VK_TAB, (int)FirmwareChord.QuickSettingsLong)]
    public void FailedSyntheticReleaseDoesNotRetryOrRepeatTheAction(uint target, int expectedChord)
    {
        FirmwareChordStateMachine state = new();
        _ = state.Observe(NativeKeyboard.VK_LWIN, true, false);
        Assert.Equal(new ChordDecision(true, true, false, (FirmwareChord)expectedChord),
            state.Observe(target, false, false));
        state.CommitSyntheticReleases(false, false);
        Assert.Equal(default, state.Observe(target, false, false));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, false, false));
        _ = state.Observe(NativeKeyboard.VK_LWIN, true, false);
        Assert.Equal(new ChordDecision(true, true, false, (FirmwareChord)expectedChord),
            state.Observe(target, false, false));
    }

    [Fact]
    public void AcceptedOrphanDoesNotClaimLaterCompleteKeyboardChord()
    {
        FirmwareChordStateMachine state = new();
        _ = state.Observe(NativeKeyboard.VK_LWIN, true, false);
        _ = state.Observe(NativeKeyboard.VK_G, false, false);
        state.CommitSyntheticReleases(true, false);
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_G, true, false));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_G, false, false));
        Assert.True(state.Observe(NativeKeyboard.VK_LWIN, false, false).Suppress);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void TwoHeldWindowsKeysSuppressOnlyTheirAcceptedReleases(bool leftAccepted, bool rightAccepted)
    {
        FirmwareChordStateMachine state = new();
        _ = state.Observe(NativeKeyboard.VK_LWIN, true, false);
        _ = state.Observe(NativeKeyboard.VK_RWIN, true, false);
        Assert.Equal(new ChordDecision(true, true, true, FirmwareChord.QuickSettings),
            state.Observe(NativeKeyboard.VK_G, false, false));
        state.CommitSyntheticReleases(leftAccepted, rightAccepted);
        Assert.Equal(leftAccepted, state.Observe(NativeKeyboard.VK_LWIN, false, false).Suppress);
        Assert.Equal(rightAccepted, state.Observe(NativeKeyboard.VK_RWIN, false, false).Suppress);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void ModifiedOrphans_PassThrough(bool control, bool alt, bool shift)
    {
        FirmwareChordStateMachine state = new();
        _ = state.Observe(NativeKeyboard.VK_LWIN, true, false);
        state.SynchronizeModifiers(control, alt, shift);
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_G, false, false));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_TAB, false, false));
    }

    [Theory]
    [InlineData(0xADu)] // Volume mute
    [InlineData(0xAEu)] // Volume down
    [InlineData(0xAFu)] // Volume up
    [InlineData(0x48u)] // Unknown future firmware target
    public void OtherKeys_PassThroughEvenWithWindowsHeld(uint target)
    {
        FirmwareChordStateMachine state = new();
        _ = state.Observe(NativeKeyboard.VK_LWIN, true, false);
        Assert.Equal(default, state.Observe(target, false, false));
        Assert.Equal(default, state.Observe(target, true, false));
        Assert.Equal(default, state.Observe(target, false, false));
    }

    [Fact]
    public void InjectedChord_DoesNotChangePhysicalKeyState()
    {
        FirmwareChordStateMachine state = new();
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, true, true));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_G, false, true));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_G, false, false));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, false, true));
    }

    [Fact]
    public void FailedSyntheticRelease_LeavesThePhysicalWindowsReleaseAlone()
    {
        FirmwareChordStateMachine state = new();
        _ = state.Observe(NativeKeyboard.VK_LWIN, true, false);
        Assert.True(state.Observe(NativeKeyboard.VK_G, false, false).ReleaseLeftWindows);
        state.CommitSyntheticReleases(false, false);
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, false, false));
    }

    [Fact]
    public void HookStartup_PreservesKeysAlreadyHeldOnTheDesktop()
    {
        FirmwareChordStateMachine state = new();
        state.InitializePreexisting(true, false, false, false, false, true, true);
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_G, false, false));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_TAB, false, false));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, false, false));
    }

    [Fact]
    public void Reset_DropsPendingSyntheticReleaseState()
    {
        FirmwareChordStateMachine state = new();
        _ = state.Observe(NativeKeyboard.VK_LWIN, true, false);
        _ = state.Observe(NativeKeyboard.VK_G, false, false);
        state.CommitSyntheticReleases(true, false);
        state.Reset();
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, false, false));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_G, false, false));
    }
}
