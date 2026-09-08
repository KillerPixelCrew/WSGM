using System.ComponentModel;
using System.Diagnostics;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class ControllerProcessPriorityTests
{
    [Theory]
    [InlineData(ProcessPriorityClass.Idle)]
    [InlineData(ProcessPriorityClass.BelowNormal)]
    [InlineData(ProcessPriorityClass.Normal)]
    [InlineData(ProcessPriorityClass.AboveNormal)]
    public void ActiveEmulationBoostsOnceAndRestoresTheOriginalPriority(ProcessPriorityClass original)
    {
        Harness harness = new(original);

        harness.Owner.SetActive(false);
        Assert.Empty(harness.Writes);
        harness.Owner.SetActive(true);
        harness.Owner.SetActive(true);
        Assert.Equal(ProcessPriorityClass.High, harness.Current);
        harness.Owner.SetActive(false);
        harness.Owner.SetActive(false);

        Assert.Equal(original, harness.Current);
        Assert.Equal([ProcessPriorityClass.High, original], harness.Writes);
        Assert.Empty(harness.Warnings);
    }

    [Theory]
    [InlineData(ProcessPriorityClass.High)]
    [InlineData(ProcessPriorityClass.RealTime)]
    public void ExistingHighPriorityIsNotOwnedOrLowered(ProcessPriorityClass original)
    {
        Harness harness = new(original);

        harness.Owner.SetActive(true);
        harness.Owner.SetActive(false);

        Assert.Empty(harness.Writes);
        Assert.Equal(original, harness.Current);
    }

    [Fact]
    public void ExternalChangesArePreservedAndTheNextActivationCapturesAFreshBaseline()
    {
        Harness harness = new();
        harness.Owner.SetActive(true);
        harness.Current = ProcessPriorityClass.AboveNormal;
        harness.Owner.SetActive(false);
        Assert.Equal(ProcessPriorityClass.AboveNormal, harness.Current);

        harness.Owner.SetActive(true);
        harness.Owner.SetActive(false);

        Assert.Equal(
            [ProcessPriorityClass.High, ProcessPriorityClass.High, ProcessPriorityClass.AboveNormal],
            harness.Writes);
    }

    [Fact]
    public void ReadFailureDoesNotBlockInputOrRetryOnRepeatedActiveState()
    {
        Harness harness = new() { FailRead = true };
        harness.Owner.SetActive(true);
        harness.Owner.SetActive(true);
        harness.Owner.SetActive(false);

        Assert.Single(harness.Warnings);
        Assert.Empty(harness.Writes);
    }

    [Fact]
    public void RejectedBoostDoesNotBlockInputOrWriteAnUnnecessaryRestore()
    {
        Harness harness = new() { FailWrite = true };
        harness.Owner.SetActive(true);
        harness.Owner.SetActive(true);
        harness.FailWrite = false;
        harness.Owner.SetActive(false);

        Assert.Single(harness.Warnings);
        Assert.Empty(harness.Writes);
        Assert.Equal(ProcessPriorityClass.Normal, harness.Current);
    }

    [Fact]
    public void FailedRestoreRetainsTheOriginalForTheNextControllerCycle()
    {
        Harness harness = new();
        harness.Owner.SetActive(true);
        harness.FailWrite = true;
        harness.Owner.SetActive(false);
        harness.Owner.SetActive(false);
        Assert.Single(harness.Warnings);

        harness.FailWrite = false;
        harness.Owner.SetActive(true);
        harness.Owner.SetActive(false);

        Assert.Equal([ProcessPriorityClass.High, ProcessPriorityClass.Normal], harness.Writes);
        Assert.Equal(ProcessPriorityClass.Normal, harness.Current);
    }

    private sealed class Harness
    {
        internal Harness(ProcessPriorityClass initial = ProcessPriorityClass.Normal)
        {
            Current = initial;
            Owner = new(
                () => FailRead ? throw new Win32Exception("read denied") : Current,
                priority =>
                {
                    if (FailWrite)
                    {
                        throw new Win32Exception("write denied");
                    }

                    Current = priority;
                    Writes.Add(priority);
                },
                _ => { },
                Warnings.Add);
        }

        internal ControllerProcessPriority Owner { get; }
        internal ProcessPriorityClass Current { get; set; }
        internal bool FailRead { get; set; }
        internal bool FailWrite { get; set; }
        internal List<ProcessPriorityClass> Writes { get; } = [];
        internal List<string> Warnings { get; } = [];
    }
}
