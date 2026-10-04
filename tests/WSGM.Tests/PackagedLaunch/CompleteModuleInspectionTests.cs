using WSGM.PackagedLaunch;

namespace WSGM.Tests.PackagedLaunch;

public sealed class CompleteModuleInspectionTests
{
    [Fact]
    public void FindsAModuleBeyond1024WithItsCompleteLongPath()
    {
        var path = @"C:\" + new string('x', 1600) + @"\Target.dll";
        var queries = 0;
        var paths = 0;
        var handle = CompleteModuleInspection.Find("Target.dll", modules =>
        {
            queries++;
            for (var index = 0; index < Math.Min(modules.Length, 1500); index++)
            {
                modules[index] = index + 1;
            }

            return (true, (uint)(1500 * nint.Size));
        }, (module, text) =>
        {
            var name = module == 1500 ? path : @"C:\other.dll";
            var copied = Math.Min(name.Length, text.Capacity - 1);
            text.Append(name.AsSpan(0, copied));
            if (module == 1500)
            {
                paths++;
            }

            return (uint)copied;
        });
        Assert.Equal(1500, handle);
        Assert.Equal(2, queries);
        Assert.True(paths > 2);
    }

    [Fact]
    public void AListThatGrowsBetweenQueriesIsReadAtItsNewSize()
    {
        var calls = 0;
        var found = CompleteModuleInspection.Find("target.dll", modules =>
        {
            var count = ++calls == 1 ? 200 : 1600;
            if (modules.Length >= count)
            {
                modules[count - 1] = 42;
            }

            return (true, (uint)(count * nint.Size));
        }, (module, text) =>
        {
            text.Append(module == 42 ? @"C:\target.dll" : @"C:\other.dll");
            return (uint)text.Length;
        });
        Assert.Equal(42, found);
        Assert.Equal(3, calls);
    }
}
