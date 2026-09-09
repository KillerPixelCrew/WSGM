using System.Text.Json;
using WSGM.Core;
using WSGM.Plugin.Sdk;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class DisplayRouteConfigurationTests
{
    [Fact]
    public void SourceGeneratedConfigPreservesBindingsAndCapturedProfile()
    {
        AppConfig config = new()
        {
            DisplayRoutes = new()
            {
                Enabled = true,
                EnterGameMode = new()
                {
                    Plugin = new("ir", "living-room"),
                    ActionId = "send",
                    Arguments = new() { ["command"] = new(Text: "PC") },
                    Target = new("monitor-path", 1, 2, "TV", 3, 4, 5),
                    Profile = new(1, [], [new byte[] { 1, 2, 3 }], [new byte[] { 4, 5, 6 }]),
                },
                DesktopWake = new() { Plugin = new("ir", "living-room"), ActionId = "restore" },
            },
        };
        var json = JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig);
        var restored = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig)!;
        Assert.True(restored.DisplayRoutes!.Enabled);
        var entry = restored.DisplayRoutes.EnterGameMode!;
        Assert.Equal(new PluginInstanceIdentity("ir", "living-room"), entry.Plugin);
        Assert.Equal("PC", entry.Arguments["command"].Text);
        Assert.Equal("monitor-path", entry.Target!.DevicePath);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(entry.Profile!.PathData));
        Assert.Equal("restore", restored.DisplayRoutes.DesktopWake!.ActionId);
        Assert.Null(new AppConfig().DisplayRoutes);
        Assert.False(new DisplayRouteConfiguration().Enabled);
    }

    [Fact]
    public void PlanCapturesArgumentsAndRejectsIncompleteBinding()
    {
        DisplayRouteBinding binding = new()
        {
            Plugin = new("ir", "one"),
            ActionId = "send",
            Arguments = new() { ["command"] = new(Text: "PC") },
        };
        var plan = DisplayRoutePlan.FromBinding(binding);
        binding.Arguments["command"] = new(Text: "TV");
        Assert.Equal("PC", plan.Action!.Arguments["command"].Text);
        binding.ActionId = null;
        Assert.Throws<ArgumentException>(() => DisplayRoutePlan.FromBinding(binding));
        Assert.Throws<ArgumentException>(() => DisplayRoutePlan.FromBinding(new() { TimeoutSeconds = 121 }));
    }
}
