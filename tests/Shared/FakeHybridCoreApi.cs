// SPDX-License-Identifier: MIT

using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Testing;

/// <summary>
///     An in-memory hybrid core power policy API that records reads, writes and refreshes. It is also the
///     scheme port, so the refresh the scheme owner makes lands in the same call log.
/// </summary>
internal sealed class FakeHybridCoreApi : IHybridCoreApi, IPowerSchemeApi
{
    private static readonly Guid Scheme = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    internal IReadOnlyList<HybridCoreClass> Classes { get; init; } = [new(0, 4, 4), new(1, 4, 4)];

    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    internal bool Configurable { get; init; } = true;

    internal bool IgnoreWrites { get; init; }
    internal Exception? NextWriteFailure { get; set; }

    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    internal IReadOnlyList<uint> HeterogeneousPolicies { get; init; } = [0, 1, 2, 3, 4];

    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    internal IReadOnlyList<HybridSchedulingPolicy> Policies { get; init; } =
    [
        HybridSchedulingPolicy.AllProcessors,
        HybridSchedulingPolicy.PerformantProcessors,
        HybridSchedulingPolicy.PreferPerformantProcessors,
        HybridSchedulingPolicy.EfficientProcessors,
        HybridSchedulingPolicy.PreferEfficientProcessors,
        HybridSchedulingPolicy.Automatic
    ];

    internal Dictionary<bool, HybridCoreState> States { get; } = new()
    {
        [false] = new HybridCoreState(0, HybridSchedulingPolicy.Automatic, HybridSchedulingPolicy.Automatic),
        [true] = new HybridCoreState(0, HybridSchedulingPolicy.Automatic, HybridSchedulingPolicy.Automatic)
    };

    // ReSharper disable once CollectionNeverQueried.Global
    internal List<string> Calls { get; } = [];

    internal int Refreshes { get; private set; }

    public Guid ReadActive()
    {
        return Scheme;
    }

    public HybridCoreSupport Query(Guid scheme)
    {
        return new HybridCoreSupport(Classes, Configurable, HeterogeneousPolicies, Policies, Policies);
    }

    public HybridCoreState Read(Guid scheme, bool onBattery)
    {
        Calls.Add("read");
        return States[onBattery];
    }

    public void Write(Guid scheme, bool onBattery, HybridCoreState state)
    {
        Calls.Add("write");
        if (NextWriteFailure is { } failure)
        {
            NextWriteFailure = null;
            throw failure;
        }

        if (!IgnoreWrites)
        {
            States[onBattery] = state;
        }
    }

    public Guid? Enumerate(uint index)
    {
        return index == 0 ? Scheme : null;
    }

    public string ReadName(Guid id)
    {
        return "Balanced";
    }

    /// <summary>The scheme owner's refresh re-activates the active scheme; that is the only activation here.</summary>
    public void SetActive(Guid id)
    {
        Calls.Add("refresh");
        Refreshes++;
    }

    public uint ReadSetting(Guid scheme, Guid subgroup, Guid setting, bool onBattery)
    {
        throw new InvalidOperationException("Unexpected power setting read");
    }

    public void WriteSetting(Guid scheme, Guid subgroup, Guid setting, bool onBattery, uint value)
    {
        throw new InvalidOperationException("Unexpected power setting write");
    }

    /// <summary>A core-placement owner over this fake as both its scheme and its core port.</summary>
    internal HybridCores Owner()
    {
        return new HybridCores(new PowerSchemes(this), this);
    }
}
