using WSGM.Device.Sdk.Authoring;
using WSGM.Device.Sdk.Testing;

namespace WSGM.Device.Sdk.Generators.Tests;

public sealed class PluginAuthoringTests
{
    [Fact]
    public void Template_CreateIsDeterministicAndSeparatesFileOwnership()
    {
        var request = new PluginSourceGenerationRequest
        {
            PackageId = "com.example.handheld",
            RootNamespace = "Example.Handheld",
            RuntimeApiVersion = 1,
        };

        IReadOnlyList<PluginTemplateFile> first = PluginProjectTemplate.Create(request);
        IReadOnlyList<PluginTemplateFile> second = PluginProjectTemplate.Create(request);

        Assert.Equal(first, second);
        Assert.Equal(
            ["Generated/PluginMetadata.g.cs", "Plugin.cs", "README.md"],
            first.Select(file => file.RelativePath));
        Assert.Equal(PluginTemplateOwnership.Generated, first[0].Ownership);
        Assert.All(first.Skip(1), file =>
            Assert.Equal(PluginTemplateOwnership.AuthorOwned, file.Ownership));
        Assert.Contains(PluginProjectTemplate.GeneratedMarker, first[0].Content);
        Assert.All(first, file => Assert.DoesNotContain("\r", file.Content, StringComparison.Ordinal));
    }

    [Fact]
    public void Template_ContainsNoInjectionOrRawHardwareSurface()
    {
        IReadOnlyList<PluginTemplateFile> files = PluginProjectTemplate.Create(new()
        {
            PackageId = "com.example.handheld",
            RootNamespace = "Example.Handheld",
            RuntimeApiVersion = 1,
        });
        string combined = string.Join('\n', files.Select(file => file.Content));

        Assert.DoesNotContain("Runtime.evaluate", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Chrome DevTools", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("querySelector", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HidHide", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HIDMaestro", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Steam Input", combined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Template_RejectsNamespaceSourceInjection()
    {
        Assert.Throws<ArgumentException>(() => PluginProjectTemplate.Create(new()
        {
            PackageId = "com.example.handheld",
            RootNamespace = "Example; public class Injected",
            RuntimeApiVersion = 1,
        }));
    }

    [Fact]
    public void ObservationAnalyzer_ProducesPureSemanticResult()
    {
        IPluginObservationAnalyzer<string, int> analyzer = new LengthAnalyzer();

        int result = analyzer.Analyze("fixture");

        Assert.Equal("text.length", analyzer.AnalyzerId);
        Assert.Equal("1", analyzer.AnalyzerVersion);
        Assert.Equal(7, result);
    }

    [Fact]
    public async Task TestHostAdapter_RecordsSemanticPublicationsInOrder()
    {
        var host = new TestPluginHostAdapter(hostGeneration: 3, deviceGeneration: 8);

        await host.PublishResourceStateAsync(new()
        {
            ResourceId = "fan",
            State = WSGM.Device.Contracts.Lifecycle.ResourceState.Acquiring,
            DeviceGeneration = 8,
        }, CancellationToken.None);
        await host.PublishResourceStateAsync(new()
        {
            ResourceId = "fan",
            State = WSGM.Device.Contracts.Lifecycle.ResourceState.Owned,
            DeviceGeneration = 8,
        }, CancellationToken.None);

        Assert.Equal(
            [WSGM.Device.Contracts.Lifecycle.ResourceState.Acquiring,
                WSGM.Device.Contracts.Lifecycle.ResourceState.Owned],
            host.ResourceStates.Select(state => state.State));
    }

    private sealed class LengthAnalyzer : IPluginObservationAnalyzer<string, int>
    {
        public string AnalyzerId => "text.length";

        public string AnalyzerVersion => "1";

        public int Analyze(string observation) => observation.Length;
    }
}
