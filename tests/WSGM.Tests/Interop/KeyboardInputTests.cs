using WSGM.Interop;

namespace WSGM.Tests.Interop;

public sealed class KeyboardInputTests
{
    private const uint ExtendedKey = 0x0001;
    private const uint KeyUp = 0x0002;
    private const uint ScanCodeFlag = 0x0008;

    [Fact]
    public void AChordGoesDownInOrderWaitsThenComesUpInTheSameOrder()
    {
        var port = new RecordingPort();

        var result = KeyboardInput.SendChord([0x11, 0x12, 0x53], 100, port);

        Assert.True(result.Complete);
        Assert.Equal(6u, result.Sent);
        Assert.Equal(["send", "wait 100", "send"], port.Calls);
        Assert.Equal(2, port.Batches.Count);
        AssertKeys(port.Batches[0], [0x11, 0x12, 0x53], false);
        AssertKeys(port.Batches[1], [0x11, 0x12, 0x53], true);
    }

    [Fact]
    public void EveryEventCarriesTheVirtualKeyAndTheLowByteOfItsScanCodeWithoutTheScanCodeFlag()
    {
        var port = new RecordingPort { Scan = virtualKey => 0xE000u | virtualKey };

        KeyboardInput.SendChord([0x2D, 0x41], 0, port);

        foreach (var input in port.Batches.SelectMany(batch => batch))
        {
            Assert.Equal(1u, input.type);
            Assert.Equal((ushort)(input.data.keyboard.virtualKey & 0xFF), input.data.keyboard.scanCode);
            Assert.Equal(0u, input.data.keyboard.flags & ScanCodeFlag);
        }

        Assert.Equal(["send", "send"], port.Calls);
    }

    [Theory]
    [InlineData(0x2Du, true)] // Insert
    [InlineData(0x2Eu, true)] // Delete
    [InlineData(0x11u, true)] // Ctrl, extended in Handheld Companion's list
    [InlineData(0x12u, true)] // Alt
    [InlineData(0x26u, true)] // Up
    [InlineData(0x2Cu, true)] // Print Screen
    [InlineData(0x6Fu, true)] // Numpad divide
    [InlineData(0x90u, true)] // Num Lock
    [InlineData(0x0Du, false)] // Enter
    [InlineData(0x10u, false)] // Shift
    [InlineData(0x5Bu, false)] // Win, absent from the list
    [InlineData(0x53u, false)] // S
    public void TheExtendedFlagFollowsHandheldCompanionsList(uint key, bool extended)
    {
        var port = new RecordingPort();

        KeyboardInput.SendChord([key], 0, port);

        Assert.Equal(extended ? ExtendedKey : 0u, port.Batches[0][0].data.keyboard.flags);
        Assert.Equal(extended ? ExtendedKey | KeyUp : KeyUp, port.Batches[1][0].data.keyboard.flags);
    }

    [Fact]
    public void NumpadEnterSendsEnterWithTheExtendedFlag()
    {
        var port = new RecordingPort();

        KeyboardInput.SendChord([KeyboardInput.NumpadEnter], 0, port);

        var down = port.Batches[0][0].data.keyboard;
        Assert.Equal((ushort)0x0D, down.virtualKey);
        Assert.Equal(ExtendedKey, down.flags);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-20, null)]
    [InlineData(250, "wait 250")]
    [InlineData(9000, "wait 5000")]
    public void ThePressDelayIsClampedToFiveSeconds(int delay, string? wait)
    {
        var port = new RecordingPort();

        KeyboardInput.SendChord([0x2D], delay, port);

        Assert.Equal(wait is null ? ["send", "send"] : ["send", wait, "send"], port.Calls);
    }

    [Fact]
    public void APartiallyAcceptedPressReleasesOnlyTheKeysThatWentDown()
    {
        var port = new RecordingPort { Accept = [1u, 1u], Error = 5 };

        var result = KeyboardInput.SendChord([0x11, 0x12, 0x53], 1000, port);

        Assert.False(result.Complete);
        Assert.Equal(1u, result.Sent);
        Assert.Equal(6u, result.Requested);
        Assert.Equal(5, result.Error);
        Assert.Equal(["send", "send"], port.Calls);
        AssertKeys(port.Batches[1], [0x11], true);
    }

    [Fact]
    public void APartiallyAcceptedReleaseReleasesTheRestOnce()
    {
        var port = new RecordingPort { Accept = [3u, 1u, 2u], Error = 5 };

        var result = KeyboardInput.SendChord([0x11, 0x12, 0x53], 0, port);

        Assert.Equal(4u, result.Sent);
        Assert.Equal(5, result.Error);
        Assert.Equal(3, port.Batches.Count);
        AssertKeys(port.Batches[2], [0x12, 0x53], true);
    }

    [Fact]
    public void AnEmptyChordSendsNothing()
    {
        var port = new RecordingPort();

        var result = KeyboardInput.SendChord([], 100, port);

        Assert.False(result.Complete);
        Assert.Empty(port.Calls);
    }

    private static void AssertKeys(NativeMethods.InputRecord[] batch, ushort[] keys, bool up)
    {
        Assert.Equal(keys, batch.Select(input => input.data.keyboard.virtualKey));
        Assert.All(batch, input => Assert.Equal(up, (input.data.keyboard.flags & KeyUp) != 0));
    }

    private sealed class RecordingPort : KeyboardInput.IInputPort
    {
        private readonly Queue<uint> _accept = new();

        internal Func<ushort, uint> Scan { get; init; } = virtualKey => virtualKey;
        internal List<NativeMethods.InputRecord[]> Batches { get; } = [];
        internal List<string> Calls { get; } = [];
        internal int Error { get; init; }

        internal uint[] Accept
        {
            init
            {
                foreach (var count in value)
                {
                    _accept.Enqueue(count);
                }
            }
        }

        public int LastError => Error;

        public uint Send(NativeMethods.InputRecord[] inputs)
        {
            Batches.Add(inputs);
            Calls.Add("send");
            return _accept.Count > 0 ? _accept.Dequeue() : (uint)inputs.Length;
        }

        public uint ScanCode(ushort virtualKey)
        {
            return Scan(virtualKey);
        }

        public void Wait(int milliseconds)
        {
            Calls.Add("wait " + milliseconds);
        }
    }
}
