using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>What leaves the machine in an uploaded log: no user folder or name, everything else unchanged.</summary>
public sealed class LogMaskTests
{
    [Theory]
    [InlineData(@"Config: C:\Users\chris\AppData\Local\WSGM", @"Config: %USERPROFILE%\AppData\Local\WSGM")]
    [InlineData("Steam at c:/users/CHRIS/scoop/apps/steam", "Steam at %USERPROFILE%/scoop/apps/steam")]
    [InlineData(@"json ""C:\\Users\\chris\\Games""", @"json ""%USERPROFILE%\\Games""")]
    [InlineData("file:///C:/Users/chris/Videos/clip.mp4", "file:///%USERPROFILE%/Videos/clip.mp4")]
    [InlineData(@"Profile C:\Users\chris", "Profile %USERPROFILE%")]
    public void TheUserFolderIsMaskedInEitherSlashFormAndAnyCase(string line, string expected)
    {
        Assert.Equal(expected, new LogMask(@"C:\Users\chris", "chris").Apply(line));
    }

    [Theory]
    [InlineData(@"Shortcut D:\Games\chris\save.dat", @"Shortcut D:\Games\<user>\save.dat")]
    [InlineData("Signed in as Chris.", "Signed in as <user>.")]
    [InlineData("Christmas Sale banner", "Christmas Sale banner")]
    [InlineData("christopher_robin and chrissy", "christopher_robin and chrissy")]
    [InlineData(@"C:\Users\chris2\Desktop", @"C:\Users\chris2\Desktop")]
    [InlineData(@"C:\Users\chris-old\Desktop", @"C:\Users\<user>-old\Desktop")]
    public void TheNameChrisIsMaskedOnlyAsAWholeWordOrPathSegment(string line, string expected)
    {
        Assert.Equal(expected, new LogMask(@"C:\Users\chris", "chris").Apply(line));
    }

    [Theory]
    [InlineData(@"C:\Users\N1GHT\Saved Games", @"%USERPROFILE%\Saved Games")]
    [InlineData("owner n1ght, steam N1GHT_2", "owner <user>, steam N1GHT_2")]
    [InlineData("N1GHTSTORM joined", "N1GHTSTORM joined")]
    [InlineData(@"\\NAS\share\N1GHT\roms", @"\\NAS\share\<user>\roms")]
    public void TheNameN1ghtIsMaskedWithoutTouchingLongerWords(string line, string expected)
    {
        Assert.Equal(expected, new LogMask(@"C:\Users\N1GHT", "N1GHT").Apply(line));
    }

    [Fact]
    public void DeviceGameAndHardwareDetailsStayExactlyAsWritten()
    {
        const string line = "Detected ROG Ally RC71L (BIOS 336), Radeon 780M driver 31.0.24027; "
                            + "launching Cyberpunk 2077 from D:\\SteamLibrary";

        Assert.Equal(line, new LogMask(@"C:\Users\chris", "chris").Apply(line));
    }

    [Fact]
    public void AReplacementIsNeverMatchedAgain()
    {
        var mask = new LogMask(@"C:\Users\user", "user");

        Assert.Equal(@"%USERPROFILE%\AppData by <user> for Users",
            mask.Apply(@"C:\Users\user\AppData by User for Users"));
    }

    [Fact]
    public void AFolderNamedDifferentlyFromTheAccountIsMaskedToo()
    {
        var mask = new LogMask(@"C:\Users\night", "N1GHT");

        Assert.Equal(@"%USERPROFILE%\x, E:\backup\<user>\y, <user>",
            mask.Apply(@"C:\Users\night\x, E:\backup\night\y, N1GHT"));
    }

    [Fact]
    public void AShortNameIsMaskedOnlyAsAPathSegment()
    {
        var mask = new LogMask(@"C:\Users\al", "al");

        Assert.Equal(@"%USERPROFILE%\x and D:\<user>\y, al dente",
            mask.Apply(@"C:\Users\al\x and D:\al\y, al dente"));
    }

    [Fact]
    public void AnEmptyAccountMasksNothing()
    {
        Assert.Equal(@"C:\Users\chris", new LogMask("", "").Apply(@"C:\Users\chris"));
    }
}
