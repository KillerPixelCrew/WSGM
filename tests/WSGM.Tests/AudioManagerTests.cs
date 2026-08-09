using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using WSGM.Interop;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class AudioManagerTests
{
    [Theory]
    [InlineData(-20, 0)]
    [InlineData(0, 0)]
    [InlineData(48.4, 48)]
    [InlineData(48.6, 49)]
    [InlineData(100, 100)]
    [InlineData(150, 100)]
    public void SliderValuesAreRoundedAndBoundedForTheNativeAbi(double value, int expected)
        => Assert.Equal(expected, AudioManager.NormalizeVolume(value));

    [Fact]
    public void NonFiniteSliderValuesFailClosedToZero()
    {
        Assert.Equal(0, AudioManager.NormalizeVolume(double.NaN));
        Assert.Equal(0, AudioManager.NormalizeVolume(double.PositiveInfinity));
    }

    [Fact]
    public void EndpointRecordsDecodeTheFixedNativeLayout()
    {
        Assert.Equal(1540, NativeVolumeControl.EndpointRecordSize);
        var memory = Marshal.AllocHGlobal(NativeVolumeControl.EndpointRecordSize);
        try
        {
            Marshal.Copy(new byte[NativeVolumeControl.EndpointRecordSize], 0, memory,
                NativeVolumeControl.EndpointRecordSize);
            WriteUtf16(memory, 0, "{render-device-id}");
            WriteUtf16(memory, 512 * 2, "Handheld speakers");
            Marshal.WriteInt32(memory, NativeVolumeControl.EndpointRecordSize - 4, 1);

            var endpoint = NativeVolumeControl.ReadEndpoint(memory);

            Assert.Equal("{render-device-id}", endpoint.Id);
            Assert.Equal("Handheld speakers", endpoint.Name);
            Assert.True(endpoint.IsDefault);
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    [Fact]
    public void EndpointRowsOnlyNotifyWhenTheirFriendlyNameChanges()
    {
        var endpoint = new AudioEndpointEntry("id", "Speakers");
        var changes = new List<string>();
        endpoint.PropertyChanged += (_, e) => changes.Add(e.PropertyName ?? "");

        endpoint.Name = "Speakers";
        endpoint.Name = "Headset";

        Assert.Equal([nameof(AudioEndpointEntry.Name)], changes);
    }

    [Fact]
    public void EndpointRefreshesKeepSurvivingRowsAndUpdateThemInPlace()
    {
        var entries = new System.Collections.ObjectModel.ObservableCollection<AudioEndpointEntry>
        {
            new("stay", "Old name"),
            new("gone", "Disconnected headset"),
        };
        var survivor = entries[0];

        AudioManager.Reconcile(
            entries,
            [
                new NativeVolumeControl.AudioEndpoint("stay", "New name", true),
                new NativeVolumeControl.AudioEndpoint("new", "Dock speakers", false),
            ]);

        Assert.Equal(2, entries.Count);
        Assert.Same(survivor, entries[0]);
        Assert.Equal("New name", survivor.Name);
        Assert.Equal("Dock speakers", entries[1].Name);
    }

    [Fact]
    public void RapidEndpointSelectionsOfOneFlowAreSerializedAndFinishWithTheLatestChoice()
    {
        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var calls = new ConcurrentQueue<string>();
        var activeCalls = 0;
        var overlappingCalls = 0;
        using var manager = new AudioManager(
            endpointId =>
            {
                if (Interlocked.Increment(ref activeCalls) != 1)
                {
                    Interlocked.Increment(ref overlappingCalls);
                }
                calls.Enqueue(endpointId);
                if (endpointId == "first")
                {
                    firstStarted.Set();
                    releaseFirst.Wait(TimeSpan.FromSeconds(5));
                }
                Interlocked.Decrement(ref activeCalls);
                return 0;
            },
            _ => { });

        manager.SelectedOutput = new AudioEndpointEntry("first", "Speakers");
        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(5)));
        manager.SelectedOutput = new AudioEndpointEntry("second", "Headset");
        releaseFirst.Set();

        Assert.True(SpinWait.SpinUntil(
            () => calls.Count == 2 && Volatile.Read(ref activeCalls) == 0,
            TimeSpan.FromSeconds(5)));
        Assert.Equal(["first", "second"], calls);
        Assert.Equal(0, overlappingCalls);
    }

    private static void WriteUtf16(nint record, int offset, string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value + '\0');
        Marshal.Copy(bytes, 0, record + offset, bytes.Length);
    }
}
