using WSGM.Core;
using WSGM.Testing;

namespace WSGM.Tests.Core;

public sealed class IniFileTests
{
    [Fact]
    public void SetValues_PreservesCommentsAndOtherSectionsWhileUpdatingMultipleKeys()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.GetPath("settings.ini");
        File.WriteAllText(path, ";keep\r\n[Folders]\r\nBios=old\r\nCache=unchanged\r\n[Other]\r\nBios=theirs\r\n");

        IniFile.SetValues(path, new Dictionary<string, string> { ["Bios"] = "new", ["Saves"] = "persistent" },
            "Folders");

        Assert.Equal(
            ";keep\r\n[Folders]\r\nBios=new\r\nCache=unchanged\r\nSaves=persistent\r\n[Other]\r\nBios=theirs\r\n",
            File.ReadAllText(path));
        Assert.Equal("theirs", IniFile.ReadValue(path, "Bios", "Other"));
    }
}
