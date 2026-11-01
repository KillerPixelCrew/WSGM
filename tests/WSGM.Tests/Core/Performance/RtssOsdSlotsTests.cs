using System.Text;
using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class RtssOsdSlotsTests
{
    private const int EntrySize = 4608;
    private const int ArrayOffset = 96;
    private const int Slots = 4;
    private const int OwnerOffset = 256;
    private const int TextExOffset = 512;
    private const int FrameOffset = 32;

    [Fact]
    public void ClaimsTheFirstFreeSlotAndSkipsRtssOwnSlot()
    {
        var region = NewRegion();

        Assert.True(RtssOsdSlots.TryWrite(region, "WSGM", "hello"));

        Assert.Equal("WSGM", region.ReadString(EntryOffset(1) + OwnerOffset));
        Assert.Equal("hello", region.ReadString(EntryOffset(1) + TextExOffset));
        // Slot 0 belongs to RTSS itself and stays untouched.
        Assert.Equal(string.Empty, region.ReadString(EntryOffset(0) + OwnerOffset));
        Assert.Equal(1u, region.ReadUInt32(FrameOffset));
    }

    [Fact]
    public void ReusesItsOwnSlotAndRespectsOtherOwners()
    {
        var region = NewRegion();
        region.WriteString(EntryOffset(1) + OwnerOffset, "RTSSSharedMemorySample");
        region.WriteString(EntryOffset(2) + OwnerOffset, "WSGM");

        Assert.True(RtssOsdSlots.TryWrite(region, "WSGM", "updated"));

        Assert.Equal("updated", region.ReadString(EntryOffset(2) + TextExOffset));
        Assert.Equal(string.Empty, region.ReadString(EntryOffset(1) + TextExOffset));
        Assert.Equal(string.Empty, region.ReadString(EntryOffset(3) + TextExOffset));
    }

    [Fact]
    public void RefusesWhenEverySlotIsForeignOwned()
    {
        var region = NewRegion();
        for (var slot = 1; slot < Slots; slot++)
        {
            region.WriteString(EntryOffset(slot) + OwnerOffset, $"owner-{slot}");
        }

        Assert.False(RtssOsdSlots.TryWrite(region, "WSGM", "hello"));
        Assert.Equal(0u, region.ReadUInt32(FrameOffset));
    }

    [Fact]
    public void ReleaseZeroesOnlyTheOwnedEntry()
    {
        var region = NewRegion();
        region.WriteString(EntryOffset(1) + OwnerOffset, "someone-else");
        region.WriteString(EntryOffset(1) + TextExOffset, "theirs");
        Assert.True(RtssOsdSlots.TryWrite(region, "WSGM", "ours"));

        RtssOsdSlots.Release(region, "WSGM");

        Assert.Equal(string.Empty, region.ReadString(EntryOffset(2) + OwnerOffset));
        Assert.Equal(string.Empty, region.ReadString(EntryOffset(2) + TextExOffset));
        Assert.Equal("someone-else", region.ReadString(EntryOffset(1) + OwnerOffset));
        Assert.Equal("theirs", region.ReadString(EntryOffset(1) + TextExOffset));
    }

    [Fact]
    public void SkipsTheTextWriteWhenTheBusyFlagIsHeld()
    {
        var region = NewRegion();
        region.BusyHeld = true;

        Assert.False(RtssOsdSlots.TryWrite(region, "WSGM", "hello"));

        Assert.Equal(string.Empty, region.ReadString(EntryOffset(1) + OwnerOffset));
        Assert.Equal(string.Empty, region.ReadString(EntryOffset(1) + TextExOffset));
        Assert.Equal(0u, region.ReadUInt32(FrameOffset));
        region.BusyHeld = false;
        Assert.True(RtssOsdSlots.TryWrite(region, "WSGM", "hello"));
        Assert.Equal("hello", region.ReadString(EntryOffset(1) + TextExOffset));
    }

    [Fact]
    public void AFailedTextWriteAlwaysReleasesTheBusyFlag()
    {
        var region = NewRegion();
        Assert.True(RtssOsdSlots.TryWrite(region, "WSGM", "old"));
        region.ThrowOnWriteAt = EntryOffset(1) + TextExOffset;

        Assert.Throws<IOException>(() => RtssOsdSlots.TryWrite(region, "WSGM", "new"));

        Assert.False(region.BusyHeld);
        Assert.Equal(1u, region.ReadUInt32(FrameOffset));
        region.ThrowOnWriteAt = null;
        Assert.True(RtssOsdSlots.TryWrite(region, "WSGM", "new"));
    }

    [Fact]
    public void ClearingAnAbsentDisplayDoesNotClaimAFreeSlot()
    {
        var region = NewRegion();

        Assert.True(RtssOsdSlots.TryWrite(region, "WSGM", string.Empty));

        Assert.Equal(string.Empty, region.ReadString(EntryOffset(1) + OwnerOffset));
        Assert.Equal(0u, region.ReadUInt32(FrameOffset));
    }

    [Fact]
    public void ReleaseRespectsTheBusyFlagAndClearsEveryMatchingOwner()
    {
        var region = NewRegion();
        region.WriteString(EntryOffset(1) + OwnerOffset, "WSGM");
        region.WriteString(EntryOffset(2) + OwnerOffset, "WSGM");
        region.WriteString(EntryOffset(3) + OwnerOffset, "foreign");
        region.BusyHeld = true;

        RtssOsdSlots.Release(region, "WSGM");
        Assert.Equal("WSGM", region.ReadString(EntryOffset(1) + OwnerOffset));

        region.BusyHeld = false;
        RtssOsdSlots.Release(region, "WSGM");
        Assert.Equal(string.Empty, region.ReadString(EntryOffset(1) + OwnerOffset));
        Assert.Equal(string.Empty, region.ReadString(EntryOffset(2) + OwnerOffset));
        Assert.Equal("foreign", region.ReadString(EntryOffset(3) + OwnerOffset));
        Assert.False(region.BusyHeld);
    }

    [Fact]
    public void RefusesAForeignMapping()
    {
        var region = NewRegion();
        region.WriteUInt32(0, 0x12345678);

        Assert.False(RtssOsdSlots.TryWrite(region, "WSGM", "hello"));
    }

    private static long EntryOffset(int slot)
    {
        return ArrayOffset + (long)slot * EntrySize;
    }

    private static FakeRegion NewRegion()
    {
        var region = new FakeRegion(ArrayOffset + Slots * EntrySize);
        // 'RTSS' as the server's C multichar constant — bytes "SSTR" in memory.
        region.WriteUInt32(0, 0x52545353);
        region.WriteUInt32(4, 0x0002_0015);
        region.WriteUInt32(20, EntrySize);
        region.WriteUInt32(24, ArrayOffset);
        region.WriteUInt32(28, Slots);
        return region;
    }

    private sealed class FakeRegion(long capacity) : IRtssOsdRegion
    {
        private readonly byte[] _memory = new byte[capacity];

        public bool BusyHeld { get; set; }
        public long? ThrowOnWriteAt { get; set; }

        public long Capacity => _memory.Length;

        public uint ReadUInt32(long offset)
        {
            return BitConverter.ToUInt32(_memory, (int)offset);
        }

        public void WriteUInt32(long offset, uint value)
        {
            BitConverter.GetBytes(value).CopyTo(_memory, (int)offset);
        }

        public void ReadBytes(long offset, byte[] buffer, int count)
        {
            Array.Copy(_memory, offset, buffer, 0, count);
        }

        public void WriteBytes(long offset, byte[] buffer, int count)
        {
            if (offset == ThrowOnWriteAt)
            {
                throw new IOException("Injected slot-write failure.");
            }

            Array.Copy(buffer, 0, _memory, offset, count);
        }

        public bool TryAcquireBusy(long offset)
        {
            if (BusyHeld)
            {
                return false;
            }

            BusyHeld = true;
            return true;
        }

        public void ReleaseBusy(long offset)
        {
            BusyHeld = false;
        }

        public string ReadString(long offset)
        {
            var end = (int)offset;
            while (end < _memory.Length && _memory[end] != 0)
            {
                end++;
            }

            return Encoding.ASCII.GetString(_memory, (int)offset, end - (int)offset);
        }

        public void WriteString(long offset, string value)
        {
            Encoding.ASCII.GetBytes(value).CopyTo(_memory, (int)offset);
        }
    }
}
