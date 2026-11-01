using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class GameFolderLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wsgm-layout-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void UnrealPluginLibrariesBelowTheNormalDepthAreFound()
    {
        Touch("Engine/Plugins/Marketplace/nvidia/DLSS/Binaries/ThirdParty/Win64/nvngx_dlss.dll");
        Touch("Engine/Plugins/Marketplace/nvidia/Streamline/Binaries/ThirdParty/Win64/sl.interposer.dll");
        Touch("Engine/Plugins/Marketplace/nvidia/Streamline/Binaries/ThirdParty/Win64/nvngx_dlssg.dll");

        var upscalers = UpscalerScan.Scan(GameFolderLayout.Scan(_root), new HashSet<string>());

        Assert.Equal(
            [
                "Engine/Plugins/Marketplace/nvidia/DLSS/Binaries/ThirdParty/Win64/nvngx_dlss.dll",
                "Engine/Plugins/Marketplace/nvidia/Streamline/Binaries/ThirdParty/Win64/nvngx_dlssg.dll",
                "Engine/Plugins/Marketplace/nvidia/Streamline/Binaries/ThirdParty/Win64/sl.interposer.dll"
            ],
            upscalers.Select(file => file.RelativePath));
        Assert.Equal(["DLSS", "DLSS Frame Generation", "Streamline"], upscalers.Select(file => file.Technology));
    }

    [Fact]
    public void UnityPluginLibrariesAreFound()
    {
        Touch("Game_Data/Plugins/x86_64/nvngx_dlss.dll");
        Touch("Game_Data/Plugins/x86_64/libxess.dll");

        var upscalers = UpscalerScan.Scan(GameFolderLayout.Scan(_root), new HashSet<string>());

        Assert.Equal(["XeSS", "DLSS"], upscalers.Select(file => file.Technology));
    }

    [Fact]
    public void FoldersOutsidePluginsKeepTheNormalDepth()
    {
        Touch("a/b/c/d/e/f/shallow.dll");
        Touch("a/b/c/d/e/f/g/deep.dll");

        var layout = GameFolderLayout.Scan(_root);

        Assert.True(layout.HasFile("a/b/c/d/e/f/shallow.dll"));
        Assert.False(layout.HasFile("a/b/c/d/e/f/g/deep.dll"));
    }

    private void Touch(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
    }
}
