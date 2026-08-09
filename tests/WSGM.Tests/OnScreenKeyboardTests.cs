using WSGM.Controls;

namespace WSGM.Tests;

public class OnScreenKeyboardTests
{
    [Fact]
    public void EveryCharacterAWpaPassphraseMayContainIsReachable()
    {
        // This keyboard is the only text entry in game mode: Windows' own touch
        // keyboard is rendered by an immersive-shell AppX that cannot activate
        // with no Explorer running. A printable ASCII character missing from the
        // layout is therefore a network whose password cannot be typed at all.
        var reachable = new HashSet<char>(string.Concat(OnScreenKeyboard.AllKeys()));
        var missing = new List<char>();
        for (var c = ' '; c <= '~'; c++)
        {
            if (!reachable.Contains(c))
            {
                missing.Add(c);
            }
        }
        Assert.Empty(missing);
    }

    [Fact]
    public void TheLayoutOffersBothLetterCases()
    {
        var reachable = new HashSet<char>(string.Concat(OnScreenKeyboard.AllKeys()));
        Assert.Contains('a', reachable);
        Assert.Contains('Z', reachable);
    }
}
