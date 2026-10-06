using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Plugin.Gpu;
using WSGM.Plugin.IntelGpu.Controls;
using WSGM.Plugin.IntelGpu.Display;
using WSGM.Plugin.IntelGpu.Igcl;
using WSGM.Plugin.IntelGpu.Tests.Fakes;
using WSGM.Testing;
using Xunit;

namespace WSGM.Plugin.IntelGpu.Tests;

public sealed class ColorPipelineTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(40)]
    public void AnUntouchedPipeRetainsItsNeutralDefaultWithoutAProbeWrite(int blocks)
    {
        var driver = new FakeIgclDriver { ColorBlockCount = (uint)blocks };
        using var session = driver.OpenSession();
        var output = new IgclOutput(3, Assert.Single(session.Adapters), 0, default, true);
        var pipeline = ColorPipeline.TryCreate(session, output, "display-fixture", ColorStore.Load(null, IntelLog.None),
            IntelLog.None);
        Assert.NotNull(pipeline);
        var probe = pipeline.ProbeSupport(false,
            new WriteAdmission(CancellationToken.None, Deadline.Never, () => true));
        Assert.Equal(WriteStatus.Applied, probe.Status);
        Assert.Equal(CapabilityValue.Integer(ColorSettings.Neutral.Brightness),
            pipeline.Read(ColorField.Brightness).Value);
        Assert.Equal(0, driver.ColorWrites);
        session.Dispose();
        GC.KeepAlive(driver);
    }

    [Fact]
    public void UnreadableColorRecordIsPreservedWhileTheWrittenValueRemainsAvailable()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Root, "color.v1.json");
        File.WriteAllText(path, "corrupt color record");
        var store = ColorStore.Load(directory.Root, IntelLog.None);
        var written = ColorSettings.Neutral with { Brightness = 5 };
        store.Set("display-fixture", written);
        Assert.Equal(written, store.Get("display-fixture"));
        Assert.Equal("corrupt color record", File.ReadAllText(path));
    }

    [Fact]
    public void FailedColorSaveDoesNotUndoTheWrittenValue()
    {
        using var directory = new TemporaryDirectory();
        var store = ColorStore.Load(directory.Root, IntelLog.None);
        Directory.CreateDirectory(Path.Combine(directory.Root, "color.v1.json"));
        var written = ColorSettings.Neutral with { Brightness = 5 };
        store.Set("display-fixture", written);
        Assert.Equal(written, store.Get("display-fixture"));
    }
}
