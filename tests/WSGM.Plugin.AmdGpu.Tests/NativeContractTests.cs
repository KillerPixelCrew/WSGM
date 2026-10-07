using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WSGM.Plugin.Gpu;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace WSGM.Plugin.AmdGpu.Tests;

public sealed unsafe class NativeContractTests
{
    private static int _releases;
    private static nint _self;
    private static uint _listCount;
    private static uint _firstItem;
    private static uint _lastItem;

    [Theory]
    [InlineData(14)]
    [InlineData(17)]
    public void ServiceWideGraphicsFeaturesUseAnOutputPointerAndPreserveTheGpu(int slot)
    {
        using var service = new Vtable();
        using var gpu = new Vtable();
        var original = *(nint*)gpu.Instance;
        using var feature = AdlxNative.GraphicsFeature(service.Instance, slot, gpu.Instance);
        Assert.Equal(service.Instance, feature.Pointer);
        Assert.Equal(original, *(nint*)gpu.Instance);
    }

    [Fact]
    public void NativeListCountAbove1024IsEnumeratedFromItsReportedBegin()
    {
        using var table = new Vtable();
        _listCount = 1500;
        _firstItem = uint.MaxValue;
        _lastItem = 0;
        _releases = 0;
        var items = AdlxNative.Items(table.Instance);
        try
        {
            Assert.Equal(1500, items.Count);
            Assert.Equal(100u, _firstItem);
            Assert.Equal(1599u, _lastItem);
            Assert.All(items, item => Assert.Equal(table.Instance, item.Pointer));
        }
        finally
        {
            foreach (var item in items)
            {
                item.Dispose();
            }
        }

        Assert.Equal(1500, _releases);
    }

    [Fact]
    public void IntegerRangeUsesTheDocumentedTwelveByteLayout()
    {
        Assert.Equal(12, Marshal.SizeOf<AdlxRange>());
        Assert.Equal(0, Marshal.OffsetOf<AdlxRange>(nameof(AdlxRange.Minimum)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<AdlxRange>(nameof(AdlxRange.Maximum)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<AdlxRange>(nameof(AdlxRange.Step)).ToInt32());
    }

    [Fact]
    public void BooleanGetterUsesTheOneByteAbiAndPassesThis()
    {
        using var table = new Vtable();
        _self = 0;
        Assert.True(AdlxNative.Boolean(table.Instance, 4));
        Assert.Equal(table.Instance, _self);
    }

    [Fact]
    public void DisplayUniqueIdKeepsAllPointerSizedBits()
    {
        using var table = new Vtable();
        Assert.Equal(unchecked((nuint)uint.MaxValue + 2), AdlxNative.Size(table.Instance, 13));
    }

    [Fact]
    public void ReferenceReleaseIsBalancedAndIdempotent()
    {
        using var table = new Vtable();
        _releases = 0;
        var reference = new AdlxObject(table.Instance);
        reference.Dispose();
        reference.Dispose();
        Assert.Equal(1, _releases);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(11)]
    public void TerminatedOrOrphanedInterfacesAreNeverReleasedThroughInvalidMemory(int status)
    {
        using var table = new Vtable();
        _releases = 0;
        var reference = new AdlxObject(table.Instance);
        var error = Assert.Throws<DriverFailure>(() => AdlxNative.Check(status, "fixture"));
        Assert.True(error.Lost);
        Assert.Equal(0, reference.Pointer);
        table.Dispose();
        reference.Dispose();
        Assert.Equal(0, _releases);
    }

    [Fact]
    public void OrdinaryFeatureFailureDoesNotInvalidateOtherInterfaces()
    {
        using var table = new Vtable();
        using var reference = new AdlxObject(table.Instance);
        var error = Assert.Throws<DriverFailure>(() => AdlxNative.Check(12, "fixture", true));
        Assert.False(error.Lost);
        Assert.False(error.Attempted);
        Assert.Equal(table.Instance, reference.Pointer);
    }

    [Theory]
    [InlineData(-180, true)]
    [InlineData(-175, true)]
    [InlineData(-179, false)]
    [InlineData(181, false)]
    public void SignedColorRangeAndStepAreRevalidated(int value, bool accepted)
    {
        Assert.Equal(accepted, AdlxSession.Within(value, new AdlxRange { Minimum = -180, Maximum = 180, Step = 5 }));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint ListSize(nint instance)
    {
        return _listCount;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint ListBegin(nint instance)
    {
        return 100;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ListAt(nint instance, uint index, nint* value)
    {
        if (_firstItem == uint.MaxValue)
        {
            _firstItem = index;
        }

        _lastItem = index;
        *value = instance;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Release(nint instance)
    {
        _releases++;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Boolean(nint instance, byte* value)
    {
        _self = instance;
        *value = 1;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ServiceFeature(nint instance, nint* value)
    {
        *value = instance;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Size(nint instance, nuint* value)
    {
        *value = unchecked((nuint)uint.MaxValue + 2);
        return 0;
    }

    private sealed class Vtable : IDisposable
    {
        private nint* _table;

        internal Vtable()
        {
            _table = (nint*)NativeMemory.AllocZeroed(24, (nuint)sizeof(nint));
            _table[1] = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Release;
            _table[3] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&ListSize;
            _table[4] = (nint)(delegate* unmanaged[Stdcall]<nint, byte*, int>)&Boolean;
            _table[5] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&ListBegin;
            _table[11] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)&ListAt;
            _table[13] = (nint)(delegate* unmanaged[Stdcall]<nint, nuint*, int>)&Size;
            _table[14] = (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&ServiceFeature;
            _table[17] = (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&ServiceFeature;
            var instance = (nint*)NativeMemory.Alloc((nuint)sizeof(nint));
            *instance = (nint)_table;
            Instance = (nint)instance;
        }

        internal nint Instance { get; private set; }

        public void Dispose()
        {
            if (Instance != 0)
            {
                NativeMemory.Free((void*)Instance);
                Instance = 0;
                NativeMemory.Free(_table);
                _table = null;
            }
        }
    }
}
