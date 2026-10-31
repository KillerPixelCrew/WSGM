using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>
///     The packaged route's own reading of the shortcuts it writes. Ownership decides what a sync is
///     allowed to overwrite or delete, so it has to be exact.
/// </summary>
public sealed class PackagedLauncherShortcutTests
{
    private const string Launcher = @"C:\WSGM\WSGM.PackagedLaunch.exe";
    private const string Aumid = "Publisher.Game_abc!App";

    private static ExistingShortcut Shortcut(string? target = null, string? options = null)
    {
        return new ExistingShortcut(2147483650u, target ?? "\"" + Launcher + "\"",
            options ?? $"--aumid {Aumid} --mode controller-only");
    }

    [Fact]
    public void OwnershipNeedsBothTheTargetAndTheIdentity()
    {
        Assert.True(PackagedLauncherShortcut.Owns(Shortcut(), Launcher, Aumid));

        // Our launcher, somebody else's game.
        Assert.False(PackagedLauncherShortcut.Owns(
            Shortcut(options: "--aumid Other_z!App --mode controller-only"), Launcher, Aumid));

        // Our game named in a shortcut that runs something else.
        Assert.False(PackagedLauncherShortcut.Owns(Shortcut(@"""C:\other.exe"""), Launcher, Aumid));
    }

    [Fact]
    public void QuotingDoesNotChangeWhetherAnEntryIsOurs()
    {
        Assert.True(PackagedLauncherShortcut.Owns(Shortcut(Launcher), Launcher, Aumid));
        Assert.True(PackagedLauncherShortcut.Owns(Shortcut("\"" + Launcher + "\""), Launcher, Aumid));
    }

    [Fact]
    public void AnEntryWhoseAumidMerelyStartsWithOursIsNotOurs()
    {
        // A prefix match would let the next sync overwrite, or delete, a shortcut the user had
        // pointed at a different application.
        ExistingShortcut other = new(
            7, Launcher, "--aumid Publisher.Game_abc!AppTwo --mode controller-only");

        Assert.False(PackagedLauncherShortcut.Owns(other, Launcher, "Publisher.Game_abc!App"));
        Assert.True(PackagedLauncherShortcut.Owns(
            new ExistingShortcut(7, Launcher, "--aumid Publisher.Game_abc!App --mode controller-only"),
            Launcher,
            "Publisher.Game_abc!App"));
    }

    [Fact]
    public void AComposedShortcutReadsBackItsKeyAndMode()
    {
        var fields = PackagedLauncherShortcut.Compose(Launcher, Aumid, ImportMode.SteamIntegration, false, false);

        Assert.True(PackagedLauncherShortcut.TryReadKey(fields.LaunchOptions, out var key));
        Assert.Equal(Aumid, key);
        Assert.True(PackagedLauncherShortcut.TryReadMode(fields.LaunchOptions, out var mode));
        Assert.Equal(ImportMode.SteamIntegration, mode);
    }

    [Fact]
    public void ArgumentsThisRouteDidNotComposeReadAsNothing()
    {
        Assert.False(PackagedLauncherShortcut.TryReadKey("-fullscreen -nosplash", out var key));
        Assert.Empty(key);
        Assert.False(PackagedLauncherShortcut.TryReadMode("", out _));
    }
}
