using WSGM.Core;
using WSGM.Input;

namespace WSGM.Tests.Input;

public sealed class UsbipToolTests
{
    [Fact]
    public void AnExistingPathToolNeedsNoInstallEnumeration()
    {
        var location = UsbipTool.Resolve(@" C:\Tools ", UnexpectedEntries(), @"C:\Programs",
            path => path == @"C:\Tools\usbip.exe");

        Assert.True(location.OnPath);
        Assert.Null(location.Folder);

        static IEnumerable<UninstallEntry> UnexpectedEntries()
        {
            yield return UnexpectedEntry();
        }

        static UninstallEntry UnexpectedEntry()
        {
            throw new InvalidOperationException("PATH already resolved the tool.");
        }
    }

    [Theory]
    [InlineData(true, true, @"D:\Driver")]
    [InlineData(false, true, @"C:\Programs\USBip")]
    [InlineData(false, false, null)]
    public void MissingInstallExecutablesFallThroughToTheDefaultFolder(bool installed, bool fallback,
        string? expectedFolder)
    {
        UninstallEntry entry = new("usbip", "USBip-win2", @"D:\Driver\", "", "", "");
        var location = UsbipTool.Resolve("", [entry], @"C:\Programs",
            path => (installed && path == @"D:\Driver\usbip.exe")
                    || (fallback && path == @"C:\Programs\USBip\usbip.exe"));

        Assert.False(location.OnPath);
        Assert.Equal(expectedFolder, location.Folder);
    }

    [Fact]
    public void TheDefaultFolderIsUsedWithoutAMatchingInstallEntry()
    {
        UninstallEntry unrelated = new("other", "Other driver", @"D:\Other", "", "", "");
        var location = UsbipTool.Resolve("", [unrelated], @"C:\Programs",
            path => path == @"C:\Programs\USBip\usbip.exe");

        Assert.False(location.OnPath);
        Assert.Equal(@"C:\Programs\USBip", location.Folder);
    }
}
