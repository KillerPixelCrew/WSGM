using WSGM.Core;

namespace WSGM.Tests.Core.Library;

public sealed class RomProfilesTests
{
    /// <summary>Screenscraper's system ids, as ES-DE's scraper maps the same systems.</summary>
    [Theory]
    [InlineData("nes", 3)]
    [InlineData("snes", 4)]
    [InlineData("gb", 9)]
    [InlineData("gbc", 10)]
    [InlineData("gba", 12)]
    [InlineData("n64", 14)]
    [InlineData("nds", 15)]
    [InlineData("megadrive", 1)]
    [InlineData("mastersystem", 2)]
    [InlineData("gamegear", 21)]
    [InlineData("sega32x", 19)]
    [InlineData("pico", 250)]
    [InlineData("saturn", 22)]
    [InlineData("dreamcast", 23)]
    [InlineData("psp", 61)]
    [InlineData("psx", 57)]
    [InlineData("ps2", 58)]
    [InlineData("ps3", 59)]
    [InlineData("gamecube", 13)]
    [InlineData("wii", 16)]
    [InlineData("switch", 225)]
    [InlineData("3ds", 17)]
    [InlineData("wiiu", 18)]
    [InlineData("xbox", 32)]
    [InlineData("xbox360", 33)]
    [InlineData("psvita", 62)]
    [InlineData("ps4", 60)]
    [InlineData("jaguar", 27)]
    [InlineData("jaguarcd", 171)]
    [InlineData("arcade", 75)]
    [InlineData("naomi", 56)]
    [InlineData("atomiswave", 53)]
    [InlineData("scummvm", 123)]
    [InlineData("pcengine", 31)]
    public void EachSystemSearchesScreenscraperUnderItsOwnSystemId(string system, int screenscraperId)
    {
        Assert.Equal(screenscraperId, RomProfiles.Find(system)!.ScreenscraperSystemId);
    }
}
