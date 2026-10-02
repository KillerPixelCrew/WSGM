using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Gpu;
using Xunit;

namespace WSGM.Plugin.NvidiaGpu.Tests;

public sealed class NvColorTests
{
    [Fact]
    public void BitDepthEditPreservesFormatRangeColorimetryAndDesktopDepth()
    {
        var original = new byte[NvApi.ColorSize];
        original[8] = 2;
        original[9] = 2;
        original[10] = 1;
        NvApi.Number(original, 12, 2);
        NvApi.Number(original, 16, 1);
        NvApi.Number(original, 20, 3);
        var field = NvColorField.All.Single(item => item.Id == "depth");
        var edited = field.WithValue(original, 3);
        Assert.Equal(2, edited[8]);
        Assert.Equal(2, edited[9]);
        Assert.Equal(1, edited[10]);
        Assert.Equal(3u, NvApi.Number(edited, 12));
        Assert.Equal(0u, NvApi.Number(edited, 16));
        Assert.Equal(3u, NvApi.Number(edited, 20));
        Assert.Equal(2u, NvApi.Number(original, 12));
    }

    [Fact]
    public void EncodingChangeAllowsDriverToChooseCompatibleColorimetry()
    {
        var original = new byte[NvApi.ColorSize];
        NvApi.Number(original, 12, 3);
        var edited = NvColorField.All.Single(field => field.Id == "format").WithValue(original, 1);
        Assert.Equal(1, edited[8]);
        Assert.Equal(255, edited[9]);
        Assert.Equal(3u, NvApi.Number(edited, 12));
    }

    [Fact]
    public void ActionAdmissionRequiresNullInsteadOfAValue()
    {
        var descriptor =
            DriverDescriptors.Toggle("cache-reset", "gpu", "Cache", "graphics", CapabilityProfileScope.GlobalOnly)
                with
                {
                    SupportsAction = true, SupportsRead = false, SupportsWrite = false,
                    ValueKind = CapabilityValueKind.None
                };
        var control = new ActionControl(descriptor);
        Assert.True(control.Accepts(null));
        Assert.False(control.Accepts(CapabilityValue.None()));
        Assert.False(control.Accepts(CapabilityValue.Boolean(true)));
    }

    private sealed class ActionControl(CapabilityDescriptor descriptor) : DriverControl(descriptor)
    {
        internal override CapabilityValue Read()
        {
            return CapabilityValue.None();
        }

        internal override void Write(CapabilityValue value)
        {
        }
    }
}
