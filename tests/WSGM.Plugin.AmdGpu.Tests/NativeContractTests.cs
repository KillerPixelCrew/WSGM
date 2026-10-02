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
            _table[4] = (nint)(delegate* unmanaged[Stdcall]<nint, byte*, int>)&Boolean;
            _table[13] = (nint)(delegate* unmanaged[Stdcall]<nint, nuint*, int>)&Size;
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
