using WindowsDeviceControl;
using WSGM.Interop;

namespace WSGM.Tests.Fakes;

/// <summary>An in-memory hybrid core power policy API that records reads, writes and refreshes.</summary>
internal sealed class FakeHybridCoreApi : IHybridCoreApi
{
    private static readonly Guid Scheme = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    internal IReadOnlyList<HybridCoreClass> Classes { get; init; } = [new(0, 4, 4), new(1, 4, 4)];

    internal bool Configurable { get; init; } = true;

    internal bool IgnoreWrites { get; init; }

    internal IReadOnlyList<uint> HeterogeneousPolicies { get; init; } = [0, 1, 2, 3, 4];

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

    internal List<string> Calls { get; } = [];

    internal int Refreshes { get; private set; }

    public Guid ReadActiveScheme()
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
        if (!IgnoreWrites)
        {
            States[onBattery] = state;
        }
    }

    public void RefreshActiveScheme()
    {
        Calls.Add("refresh");
        Refreshes++;
    }
}
